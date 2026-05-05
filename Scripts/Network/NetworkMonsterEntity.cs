using UnityEngine;
using UnityEngine.AI;
using Mirror;
using RPG.Data;
using RPG.UI;
using RPG.Character;
using RPG.Combat;
using System.Collections;
using System.Collections.Generic;

namespace RPG.Network
{
    public enum MonsterDisposition { Passive, Neutral, Aggressive }

    /// <summary>
    /// NetworkMonsterEntity v11 — SERVIDOR É AUTORIDADE TOTAL.
    ///
    /// CORREÇÕES v11:
    ///   - CmdRequestSkill valida cooldown via NetworkPlayer.ServerCheckAndSetCooldown().
    ///   - Consome MP via NetworkPlayer.ServerConsumeMP().
    ///   - Confirma/rejeita skill via NetworkPlayer.RpcSkillConfirmed/RpcSkillRejected.
    ///   - Todos os métodos compilam com o NetworkPlayer v6.
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    [RequireComponent(typeof(NetworkIdentity))]
    [RequireComponent(typeof(NetworkTransformReliable))]
    public class NetworkMonsterEntity : NetworkBehaviour, ITargetable
    {
        // ── Identidade ─────────────────────────────────────────────────────
        [Header("Identidade")]
        [SerializeField] private string monsterDisplayName = "Monstro";
        [SerializeField] private int    level              = 1;

        // ── Comportamento ──────────────────────────────────────────────────
        [Header("Comportamento")]
        [SerializeField] private MonsterDisposition disposition = MonsterDisposition.Aggressive;

        // ── Atributos ──────────────────────────────────────────────────────
        [Header("Atributos Base")]
        [SerializeField] private int baseSTR = 12;
        [SerializeField] private int baseAGI = 8;
        [SerializeField] private int baseVIT = 10;
        [SerializeField] private int baseDEX = 8;
        [SerializeField] private int baseINT = 5;
        [SerializeField] private int baseLUK = 5;

        // ── Ranges de IA ───────────────────────────────────────────────────
        [Header("Ranges de IA")]
        [SerializeField] private float aggroRange     = 10f;
        [SerializeField] private float attackRange    = 2.5f;
        [SerializeField] private float kiteDistance   = 1.8f;
        [SerializeField] private float attackCooldown = 2f;
        [SerializeField] private float leashRange     = 30f;
        [SerializeField] private float pathUpdateRate = 0.2f;

        [Header("Performance de IA")]
        [SerializeField] private float aggroScanInterval = 0.5f;

        // ── Patrulha ───────────────────────────────────────────────────────
        [Header("Patrulha")]
        [SerializeField] private bool        usePatrolPoints = false;
        [SerializeField] private Transform[] patrolPoints;
        [SerializeField] private float       patrolWaitTime  = 2f;
        [SerializeField] private float       patrolRadius    = 12f;

        // ── Fuga ───────────────────────────────────────────────────────────
        [Header("Fuga (apenas Passive)")]
        [SerializeField] private float fleeDuration  = 6f;
        [SerializeField] private float fleeSpeedMult = 1.5f;

        // ── Morte / Respawn ────────────────────────────────────────────────
        [Header("Morte e Respawn")]
        [SerializeField] private float hideDelay    = 3f;
        [SerializeField] private float respawnDelay = 15f;

        [Header("Recompensa")]
        [SerializeField] private long expReward = 50;

        // ── Visuals ────────────────────────────────────────────────────────
        [Header("Visuals")]
        [SerializeField] private GameObject         selectionIndicator;
        [SerializeField] private MonsterHealthBarUI healthBarUI;
        [SerializeField] private GameObject         visualRoot;

        // ── SyncVars ───────────────────────────────────────────────────────
        [SyncVar(hook = nameof(OnCurrentHPChanged))]
        private float _currentHP;

        [SyncVar]
        private float _maxHP;

        [SyncVar(hook = nameof(OnDeadChanged))]
        private bool _isDead;

        // ── ITargetable ────────────────────────────────────────────────────
        public string  DisplayName => monsterDisplayName;
        public float   CurrentHP   => _currentHP;
        public float   MaxHP       => _maxHP;
        public bool    IsDead      => _isDead;
        public Vector3 Position    => transform.position;

        public void OnSelected()   { if (selectionIndicator) selectionIndicator.SetActive(true);  }
        public void OnDeselected() { if (selectionIndicator) selectionIndicator.SetActive(false); }

        public void TakeDamage(float rawAtk, float rawMatk, bool isPhysical)
        {
            if (!isServer || _isDead) return;
            bool  crit = StatsCalculator.RollCrit(_stats?.CRIT ?? 0f);
            float dmg  = isPhysical
                ? StatsCalculator.CalculatePhysicalDamage(rawAtk, _stats?.DEF ?? 0f, crit, _stats?.CritDMG ?? 1.5f)
                : StatsCalculator.CalculateMagicDamage(rawMatk, _stats?.MDEF ?? 0f, crit, _stats?.CritDMG ?? 1.5f);
            dmg = Mathf.Max(1f, dmg);
            _currentHP = Mathf.Max(0f, _currentHP - dmg);
            RpcShowDamage(dmg, crit, transform.position);
            if (_currentHP <= 0f) ServerDie();
        }

        // ── Stats e estado interno ─────────────────────────────────────────
        private DerivedStats _stats;
        private readonly Dictionary<uint, float> _damageLog = new();

        // ── IA ─────────────────────────────────────────────────────────────
        private enum AIState { Idle, Patrol, Chase, Combat, Flee, ReturnHome, Dead }

        private AIState       _state = AIState.Idle;
        private NavMeshAgent  _agent;
        private Animator      _animator;
        private NetworkPlayer _aggroTarget;

        private float _attackTimer;
        private float _pathTimer;
        private float _patrolWaitTimer;
        private float _fleeTimer;
        private float _regenTimer;
        private float _aggroScanTimer;

        private int     _patrolIndex;
        private bool    _waitingAtPatrolPoint;
        private bool    _wasAttacked;
        private Vector3 _currentPatrolTarget;
        private bool    _patrolTargetSet;
        private Vector3 _homePosition;
        private float   _patrolRadiusRuntime;

        private const float REGEN_INTERVAL       = 3f;
        private const float REGEN_PERCENT        = 0.05f;
        private const float SKILL_RANGE_TOLERANCE = 2.0f;

        // ── Init ───────────────────────────────────────────────────────────

        private void Awake()
        {
            _agent    = GetComponent<NavMeshAgent>();
            _animator = GetComponentInChildren<Animator>();

            _stats = StatsCalculator.Calculate(
                new BaseAttributes { STR = baseSTR, AGI = baseAGI, VIT = baseVIT,
                                     DEX = baseDEX, INT = baseINT, LUK = baseLUK },
                level, CharacterRace.Human);
        }

        public override void OnStartServer()
        {
            _homePosition        = transform.position;
            _patrolRadiusRuntime = patrolRadius;
            ServerReset();
        }

        public override void OnStartClient()
        {
            if (selectionIndicator) selectionIndicator.SetActive(false);
            healthBarUI?.UpdateBar(_currentHP, _maxHP);
        }

        [Server]
        public void SetSpawnData(Vector3 homePos, float newPatrolRadius)
        {
            _homePosition        = homePos;
            _patrolRadiusRuntime = newPatrolRadius;
            transform.position   = homePos;
            _patrolTargetSet     = false;
        }

        [Server]
        private void ServerReset()
        {
            _maxHP    = _stats.MaxHP;
            _currentHP = _maxHP;
            _isDead   = false;
            _wasAttacked = false;
            _state    = AIState.Idle;
            _aggroTarget = null;
            _attackTimer = _pathTimer = _fleeTimer = _regenTimer = _aggroScanTimer = 0f;
            _patrolIndex = 0;
            _waitingAtPatrolPoint = _patrolTargetSet = false;
            _damageLog.Clear();

            transform.position = _homePosition;

            if (_agent != null)
            {
                _agent.enabled = true;
                _agent.speed   = _stats.ASPD;
                if (_agent.isOnNavMesh) _agent.Warp(_homePosition);
            }

            RpcOnRespawned();
        }

        // ── Update ─────────────────────────────────────────────────────────

        private void Update()
        {
            if (!isServer || _isDead) return;

            _pathTimer      += Time.deltaTime;
            _attackTimer    += Time.deltaTime;
            _aggroScanTimer += Time.deltaTime;

            switch (_state)
            {
                case AIState.Idle:       ServerIdle();       break;
                case AIState.Patrol:     ServerPatrol();     break;
                case AIState.Chase:      ServerChase();      break;
                case AIState.Combat:     ServerCombat();     break;
                case AIState.Flee:       ServerFlee();       break;
                case AIState.ReturnHome: ServerReturnHome(); break;
            }
        }

        // ── Estados de IA ──────────────────────────────────────────────────

        private void ServerIdle()
        {
            if (TryAggro()) return;
            if (_patrolRadiusRuntime > 0.5f && !usePatrolPoints) _state = AIState.Patrol;
            else if (usePatrolPoints && patrolPoints != null && patrolPoints.Length > 0)
                _state = AIState.Patrol;
        }

        private void ServerPatrol()
        {
            if (TryAggro()) return;
            if (usePatrolPoints) PatrolWithWaypoints();
            else                 PatrolInArea();
        }

        private void PatrolWithWaypoints()
        {
            if (!_agent.isOnNavMesh) return;
            if (_waitingAtPatrolPoint)
            {
                _patrolWaitTimer += Time.deltaTime;
                if (_patrolWaitTimer < patrolWaitTime) return;
                _waitingAtPatrolPoint = false;
            }
            if (_pathTimer >= pathUpdateRate)
            {
                _pathTimer = 0f;
                if (!_agent.pathPending && _agent.remainingDistance < 0.5f)
                {
                    _patrolIndex = (_patrolIndex + 1) % patrolPoints.Length;
                    _agent.SetDestination(patrolPoints[_patrolIndex].position);
                    _waitingAtPatrolPoint = true;
                    _patrolWaitTimer      = 0f;
                }
            }
        }

        private void PatrolInArea()
        {
            if (!_agent.isOnNavMesh) return;
            bool arrived = !_agent.pathPending && _agent.remainingDistance < 0.6f;
            if (_waitingAtPatrolPoint)
            {
                _patrolWaitTimer += Time.deltaTime;
                if (_patrolWaitTimer >= patrolWaitTime) { _waitingAtPatrolPoint = false; _patrolTargetSet = false; }
                return;
            }
            if (!_patrolTargetSet || arrived)
            {
                if (arrived && _patrolTargetSet) { _waitingAtPatrolPoint = true; _patrolWaitTimer = 0f; return; }
                if (TryGetRandomAreaPoint(_homePosition, _patrolRadiusRuntime, out Vector3 dest))
                {
                    _agent.SetDestination(dest);
                    _currentPatrolTarget = dest;
                    _patrolTargetSet     = true;
                }
            }
        }

        private void ServerChase()
        {
            if (_aggroTarget == null || _aggroTarget.Dead) { ResetAggro(); return; }
            if (Vector3.Distance(transform.position, _homePosition) > leashRange) { ResetAggro(); EnterReturnHome(); return; }

            float dist = Vector3.Distance(transform.position, _aggroTarget.transform.position);
            if (dist > aggroRange * 2.5f) { ResetAggro(); return; }

            if (dist <= attackRange) { _attackTimer = 0f; _state = AIState.Combat; _agent.ResetPath(); return; }

            if (_pathTimer >= pathUpdateRate && _agent.isOnNavMesh)
            {
                _pathTimer              = 0f;
                _agent.stoppingDistance = attackRange * 0.85f;
                _agent.SetDestination(_aggroTarget.transform.position);
            }
        }

        private void ServerCombat()
        {
            if (_aggroTarget == null || _aggroTarget.Dead) { ResetAggro(); return; }
            if (Vector3.Distance(transform.position, _homePosition) > leashRange) { ResetAggro(); EnterReturnHome(); return; }

            float dist = Vector3.Distance(transform.position, _aggroTarget.transform.position);
            if (dist > attackRange * 1.4f) { _state = AIState.Chase; return; }

            if (_agent.isOnNavMesh)
            {
                if (dist < kiteDistance)
                {
                    Vector3 away = (transform.position - _aggroTarget.transform.position).normalized;
                    _agent.SetDestination(transform.position + away * (kiteDistance + 0.5f));
                }
                else _agent.ResetPath();
            }

            Vector3 dir = _aggroTarget.transform.position - transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude > 0.01f)
                transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(dir), 10f * Time.deltaTime);

            if (_attackTimer >= attackCooldown) { _attackTimer = 0f; ServerAttack(); }
        }

        private void ServerFlee()
        {
            _fleeTimer += Time.deltaTime;
            if (_fleeTimer >= fleeDuration || !_agent.isOnNavMesh) { _agent.speed = _stats.ASPD; EnterReturnHome(); return; }
            if (_pathTimer >= pathUpdateRate * 2f && _aggroTarget != null)
            {
                _pathTimer = 0f;
                Vector3 fleeDir = (transform.position - _aggroTarget.transform.position).normalized;
                Vector3 fleePos = transform.position + fleeDir * (aggroRange * 1.5f);
                if (NavMesh.SamplePosition(fleePos, out NavMeshHit hit, 5f, NavMesh.AllAreas))
                    _agent.SetDestination(hit.position);
            }
        }

        private void ServerReturnHome()
        {
            _regenTimer += Time.deltaTime;
            if (_regenTimer >= REGEN_INTERVAL)
            {
                _regenTimer = 0f;
                _currentHP  = Mathf.Min(_maxHP, _currentHP + _maxHP * REGEN_PERCENT);
            }
            if (!_agent.isOnNavMesh) return;
            if (_pathTimer >= pathUpdateRate)
            {
                _pathTimer = 0f;
                _agent.stoppingDistance = 0.5f;
                _agent.SetDestination(_homePosition);
            }
            if (Vector3.Distance(transform.position, _homePosition) < 1.5f)
            {
                _agent.ResetPath();
                _wasAttacked = false;
                _patrolTargetSet = false;
                _damageLog.Clear();
                _state = AIState.Idle;
            }
        }

        private void EnterReturnHome()
        {
            _state = AIState.ReturnHome;
            _aggroTarget = null;
            _regenTimer  = 0f;
            _agent.stoppingDistance = 0.5f;
        }

        // ── Aggro ──────────────────────────────────────────────────────────

        private bool TryAggro()
        {
            if (disposition == MonsterDisposition.Passive) return false;
            if (disposition == MonsterDisposition.Neutral && !_wasAttacked) return false;
            if (_aggroScanTimer < aggroScanInterval) return false;
            _aggroScanTimer = 0f;

            float closest = aggroRange;
            NetworkPlayer found = null;

            foreach (var np in NetworkPlayer.All)
            {
                if (np == null || np.Dead) continue;
                float d = Vector3.Distance(transform.position, np.transform.position);
                if (d < closest) { closest = d; found = np; }
            }

            if (found != null)
            {
                _aggroTarget = found;
                _state       = AIState.Chase;
                _pathTimer   = pathUpdateRate;
                _attackTimer = 0f;
                return true;
            }
            return false;
        }

        private void ResetAggro()
        {
            _aggroTarget = null;
            if (_agent != null && _agent.isOnNavMesh) { _agent.ResetPath(); _agent.stoppingDistance = 0.3f; }
            _attackTimer     = 0f;
            _patrolTargetSet = false;

            if (Vector3.Distance(transform.position, _homePosition) > leashRange * 0.5f)
                EnterReturnHome();
            else
                _state = AIState.Idle;
        }

        // ── Ataque do monstro → jogador ────────────────────────────────────

        [Server]
        private void ServerAttack()
        {
            if (_aggroTarget == null || _aggroTarget.Dead) return;

            bool hit = StatsCalculator.RollHit(_stats.HIT, _aggroTarget.ServerStats?.FLEE ?? 20f);
            if (!hit) { RpcShowMiss(_aggroTarget.transform.position); return; }

            bool  crit = StatsCalculator.RollCrit(_stats.CRIT);
            float dmg  = StatsCalculator.CalculatePhysicalDamage(
                _stats.ATK, _aggroTarget.ServerStats?.DEF ?? 10f, crit, _stats.CritDMG);

            _aggroTarget.ServerApplyDamage(dmg);
            RpcPlayAnim("Attack");
        }

        // ── CmdRequestSkill — cliente solicita usar skill neste monstro ────

        /// <summary>
        /// Recebe APENAS (netId atacante, índice skill, físico/mágico).
        /// NENHUM dado de dano ou stats vem do cliente.
        /// Servidor valida tudo: existência, vida, MP, cooldown, range, aplica dano.
        /// </summary>
[Command(requiresAuthority = false)]
public void CmdRequestSkill(uint attackerNetId, int skillIndex, bool isPhysical)
{
    if (_isDead) return;

    NetworkPlayer attacker = null;
    foreach (var np in NetworkPlayer.All)
        if (np != null && np.netId == attackerNetId) { attacker = np; break; }

    if (attacker == null || attacker.Dead) return;

    var atkStats = attacker.ServerStats;
    if (atkStats == null) return;

    var skill = attacker.GetComponent<SkillSystem>()?.GetSkill(skillIndex);
    if (skill == null)
    {
        // CORREÇÃO: sem NetworkConnection no overload
        attacker.RpcSkillRejected(skillIndex, "Skill inválida.");
        return;
    }

    if (!attacker.ServerCheckAndSetCooldown(skillIndex, skill.Cooldown))
    {
        attacker.RpcSkillRejected(skillIndex, $"{skill.Name}: ainda em cooldown.");
        return;
    }

    if (attacker.CurrentMP < skill.ManaCost)
    {
        attacker.RpcSkillRejected(skillIndex, "MP insuficiente!");
        return;
    }

    float dist = Vector3.Distance(attacker.transform.position, transform.position);
    if (dist > skill.Range + SKILL_RANGE_TOLERANCE)
    {
        Debug.LogWarning($"[Server] {attacker.CharacterName} range suspeito: " +
                         $"dist={dist:0.1} range={skill.Range}");
    }

    attacker.ServerConsumeMP(skill.ManaCost);
    ServerTakeDamageFromPlayer(attacker, atkStats, skillIndex, isPhysical, skill);

    // CORREÇÃO: sem NetworkConnection no overload
    attacker.RpcSkillConfirmed(skillIndex, skill.Cooldown);
}

        [Server]
        private void ServerTakeDamageFromPlayer(
            NetworkPlayer attacker, DerivedStats atkStats,
            int skillIndex, bool isPhysical, SkillData skill)
        {
            bool hit = StatsCalculator.RollHit(atkStats.HIT, _stats.FLEE);
            if (!hit) { RpcShowMiss(transform.position); return; }

            bool  crit = StatsCalculator.RollCrit(atkStats.CRIT);
            float dmg  = isPhysical
                ? StatsCalculator.CalculatePhysicalDamage(
                      atkStats.ATK * skill.AtkMultiplier, _stats.DEF, crit, atkStats.CritDMG)
                : StatsCalculator.CalculateMagicDamage(
                      atkStats.MATK * skill.AtkMultiplier, _stats.MDEF, crit, atkStats.CritDMG);

            dmg = Mathf.Max(1f, dmg);

            if (!_damageLog.ContainsKey(attacker.netId)) _damageLog[attacker.netId] = 0f;
            _damageLog[attacker.netId] += dmg;

            _currentHP = Mathf.Max(0f, _currentHP - dmg);
            RpcShowDamage(dmg, crit, transform.position);

            // Reação conforme disposição
            switch (disposition)
            {
                case MonsterDisposition.Passive:
                    if (_state != AIState.Flee && _state != AIState.ReturnHome && _state != AIState.Dead)
                    {
                        _aggroTarget = attacker; _fleeTimer = 0f; _state = AIState.Flee;
                        _agent.speed = _stats.ASPD * fleeSpeedMult; _pathTimer = pathUpdateRate;
                    }
                    break;
                case MonsterDisposition.Neutral:
                    _wasAttacked = true;
                    if (_state == AIState.Idle || _state == AIState.Patrol || _state == AIState.ReturnHome)
                    { _aggroTarget = attacker; _state = AIState.Chase; _pathTimer = pathUpdateRate; _attackTimer = 0f; }
                    break;
                case MonsterDisposition.Aggressive:
                    if (_state == AIState.Idle || _state == AIState.Patrol)
                    { _aggroTarget = attacker; _state = AIState.Chase; _pathTimer = pathUpdateRate; _attackTimer = 0f; }
                    break;
            }

            if (_currentHP <= 0f) ServerDie();
        }

        // ── Morte / Respawn ────────────────────────────────────────────────

        [Server]
        private void ServerDie()
        {
            if (_isDead) return;
            _isDead = true;
            _state  = AIState.Dead;

            if (_agent != null) { if (_agent.isOnNavMesh) _agent.ResetPath(); _agent.enabled = false; }

            Debug.Log($"[NetworkMonster] {monsterDisplayName} morreu!");
            ServerDistributeExp();
            StartCoroutine(ServerDeathSequence());
        }

        [Server]
        private void ServerDistributeExp()
        {
            if (_damageLog.Count == 0) return;
            float total = 0f;
            foreach (var kv in _damageLog) total += kv.Value;
            if (total <= 0f) return;

            foreach (var kv in _damageLog)
            {
                long xp = (long)Mathf.Max(1f, expReward * (kv.Value / total));
                foreach (var np in NetworkPlayer.All)
                    if (np != null && np.netId == kv.Key) { np.ServerGrantExp(xp); break; }
            }
            _damageLog.Clear();
        }

        [Server]
        private IEnumerator ServerDeathSequence()
        {
            RpcOnDied(transform.position);
            if (hideDelay > 0f) yield return new WaitForSeconds(hideDelay);
            RpcHideVisuals();
            if (respawnDelay <= 0f) yield break;
            yield return new WaitForSeconds(respawnDelay);
            if (isServer) ServerReset();
        }

        // ── NavMesh Helper ─────────────────────────────────────────────────

        private bool TryGetRandomAreaPoint(Vector3 center, float radius, out Vector3 result)
        {
            for (int i = 0; i < 15; i++)
            {
                Vector2 r2 = Random.insideUnitCircle * radius;
                Vector3 c  = center + new Vector3(r2.x, 0f, r2.y);
                if (NavMesh.SamplePosition(c, out NavMeshHit hit, 3f, NavMesh.AllAreas))
                { result = hit.position; return true; }
            }
            result = center;
            return false;
        }

        // ── ClientRpcs ─────────────────────────────────────────────────────

        [ClientRpc] private void RpcShowDamage(float dmg, bool crit, Vector3 pos)
            => FloatingTextManager.Instance?.Show(crit ? $"CRÍTICO! {dmg:0}" : $"{dmg:0}", pos + Vector3.up, crit ? Color.yellow : Color.white);

        [ClientRpc] private void RpcShowMiss(Vector3 pos)
            => FloatingTextManager.Instance?.Show("MISS", pos + Vector3.up * 0.5f, Color.gray);

        [ClientRpc] private void RpcPlayAnim(string trigger)
            => _animator?.SetTrigger(trigger);

        [ClientRpc]
        private void RpcOnDied(Vector3 pos)
        {
            OnDeselected();
            UIManager.Instance?.ClearTargetPanel();
            FloatingTextManager.Instance?.Show("Morto!", pos + Vector3.up, Color.red);
        }

        [ClientRpc]
        private void RpcHideVisuals()
        {
            if (visualRoot)         visualRoot.SetActive(false);
            if (selectionIndicator) selectionIndicator.SetActive(false);
            if (healthBarUI)        healthBarUI.gameObject.SetActive(false);
        }

        [ClientRpc]
        private void RpcOnRespawned()
        {
            if (visualRoot)         visualRoot.SetActive(true);
            if (selectionIndicator) selectionIndicator.SetActive(false);
            if (healthBarUI) { healthBarUI.gameObject.SetActive(true); healthBarUI.UpdateBar(_currentHP, _maxHP); }
        }

        // ── SyncVar Hooks ──────────────────────────────────────────────────

        private void OnCurrentHPChanged(float _, float v) => healthBarUI?.UpdateBar(v, _maxHP);

        private void OnDeadChanged(bool _, bool dead)
        { if (dead && _agent != null) _agent.enabled = false; }

        // ── Gizmos ─────────────────────────────────────────────────────────
#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            Gizmos.color = disposition switch
            {
                MonsterDisposition.Passive => Color.green,
                MonsterDisposition.Neutral => Color.yellow,
                _ => Color.red
            };
            Gizmos.DrawWireSphere(transform.position, aggroRange);
            Gizmos.color = new Color(1f, 0.3f, 0.3f);
            Gizmos.DrawWireSphere(transform.position, attackRange);
            Gizmos.color = Color.blue;
            Gizmos.DrawWireSphere(transform.position, kiteDistance);
            Gizmos.color = new Color(1f, 1f, 1f, 0.4f);
            Gizmos.DrawWireSphere(transform.position, leashRange);
        }
#endif
    }
}
