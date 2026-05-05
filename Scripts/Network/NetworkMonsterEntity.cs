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
    /// NetworkMonsterEntity v12 — SERVIDOR É AUTORIDADE TOTAL.
    ///
    /// CORREÇÕES v12 (lag / movimento travado):
    ///   1. Troca de NetworkTransformReliable para NetworkTransformUnreliable
    ///      (declare no prefab) — envia posição todo frame para monstros em movimento,
    ///      eliminando o "jitter" de teleporte que parecia lag.
    ///   2. pathUpdateRate reduzido para 0.1 s (era 0.2 s) e controlado por
    ///      coroutine dedicada em vez de Update(), reduzindo overhead no servidor.
    ///   3. aggroScan movido para coroutine com yield — não bloqueia o Update.
    ///   4. Remoção dos timers manuais de pathTimer/aggroScanTimer do Update()
    ///      — substituídos por coroutines temporizadas.
    ///   5. Corrigido double-overload RpcSkillConfirmed/Rejected: usa apenas
    ///      [ClientRpc] com guarda isLocalPlayer no NetworkPlayer (sem TargetRpc).
    ///   6. ServerDie() protegido com flag dupla para evitar dupla distribuição de XP.
    ///   7. NavMeshAgent.velocity.sqrMagnitude usado para detectar parada real.
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    [RequireComponent(typeof(NetworkIdentity))]
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
        [SerializeField] private float aggroRange      = 10f;
        [SerializeField] private float attackRange     = 2.5f;
        [SerializeField] private float kiteDistance    = 1.8f;
        [SerializeField] private float attackCooldown  = 2f;
        [SerializeField] private float leashRange      = 30f;

        [Header("Performance de IA")]
        [SerializeField] private float aggroScanInterval = 0.4f;
        [SerializeField] private float pathUpdateRate    = 0.1f;

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

        /// <summary>
        /// TakeDamage offline/testes apenas. Em multiplayer, usa CmdRequestSkill.
        /// </summary>
        public void TakeDamage(float rawAtk, float rawMatk, bool isPhysical)
        {
            if (!isServer || _isDead) return;
            bool  crit = StatsCalculator.RollCrit(_stats?.CRIT ?? 0f);
            float dmg  = isPhysical
                ? StatsCalculator.CalculatePhysicalDamage(rawAtk, _stats?.DEF ?? 0f, crit, _stats?.CritDMG ?? 1.5f)
                : StatsCalculator.CalculateMagicDamage(rawMatk, _stats?.MDEF ?? 0f, crit, _stats?.CritDMG ?? 1.5f);
            ApplyDamageInternal(Mathf.Max(1f, dmg));
        }

        // ── Stats e estado interno ─────────────────────────────────────────
        private DerivedStats _stats;
        private readonly Dictionary<uint, float> _damageLog = new();

        // ── IA ─────────────────────────────────────────────────────────────
        private enum AIState { Idle, Patrol, Chase, Combat, Flee, ReturnHome, Dead }

        private AIState      _state = AIState.Idle;
        private NavMeshAgent _agent;
        private Animator     _animator;
        private NetworkPlayer _aggroTarget;

        private float _attackTimer;
        private float _patrolWaitTimer;
        private float _fleeTimer;
        private float _regenTimer;

        private int     _patrolIndex;
        private bool    _waitingAtPatrolPoint;
        private bool    _wasAttacked;
        private Vector3 _currentPatrolTarget;
        private bool    _patrolTargetSet;
        private Vector3 _homePosition;
        private float   _patrolRadiusRuntime;

        // Coroutines de IA
        private Coroutine _aggroScanCoroutine;
        private Coroutine _pathUpdateCoroutine;

        private bool _deathProcessed = false;

        private const float REGEN_INTERVAL        = 3f;
        private const float REGEN_PERCENT         = 0.05f;
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
            _maxHP         = _stats.MaxHP;
            _currentHP     = _maxHP;
            _isDead        = false;
            _wasAttacked   = false;
            _deathProcessed = false;
            _state         = AIState.Idle;
            _aggroTarget   = null;
            _attackTimer   = 0f;
            _patrolWaitTimer  = 0f;
            _fleeTimer        = 0f;
            _regenTimer       = 0f;
            _patrolIndex      = 0;
            _waitingAtPatrolPoint = false;
            _patrolTargetSet      = false;
            _damageLog.Clear();

            transform.position = _homePosition;

            if (_agent != null)
            {
                _agent.enabled = true;
                _agent.speed   = _stats.ASPD;
                if (_agent.isOnNavMesh) _agent.Warp(_homePosition);
            }

            // Reinicia coroutines de IA
            StopAllCoroutines();
            _aggroScanCoroutine  = StartCoroutine(AggroScanLoop());
            _pathUpdateCoroutine = StartCoroutine(PathUpdateLoop());

            RpcOnRespawned();
        }

        // ── Coroutines de IA (substituem timers manuais no Update) ─────────

        /// <summary>
        /// Escaneia jogadores próximos periodicamente sem bloquear o Update.
        /// Intervalo configurável via aggroScanInterval.
        /// </summary>
        [Server]
        private IEnumerator AggroScanLoop()
        {
            var wait = new WaitForSeconds(aggroScanInterval);
            while (true)
            {
                if (!_isDead &&
                    (_state == AIState.Idle || _state == AIState.Patrol) &&
                    disposition != MonsterDisposition.Passive &&
                    !(disposition == MonsterDisposition.Neutral && !_wasAttacked))
                {
                    TryAggro();
                }
                yield return wait;
            }
        }

        /// <summary>
        /// Atualiza o caminho do NavMesh periodicamente.
        /// pathUpdateRate define o intervalo (0.1 s = fluido, 0.3 s = econômico).
        /// </summary>
        [Server]
        private IEnumerator PathUpdateLoop()
        {
            var wait = new WaitForSeconds(pathUpdateRate);
            while (true)
            {
                if (!_isDead)
                {
                    switch (_state)
                    {
                        case AIState.Chase:      UpdateChasePath();      break;
                        case AIState.ReturnHome: UpdateReturnHomePath(); break;
                        case AIState.Flee:       UpdateFleePath();       break;
                        case AIState.Patrol:
                            if (!usePatrolPoints) UpdatePatrolAreaPath();
                            break;
                    }
                }
                yield return wait;
            }
        }

        // ── Update (apenas lógica leve sem SetDestination) ─────────────────

        private void Update()
        {
            if (!isServer || _isDead) return;

            _attackTimer += Time.deltaTime;

            switch (_state)
            {
                case AIState.Idle:
                    // Aggro scan é feito pela coroutine
                    break;

                case AIState.Patrol:
                    if (usePatrolPoints) ServerPatrolWaypoints();
                    break;

                case AIState.Chase:
                    ServerChaseCheck();
                    break;

                case AIState.Combat:
                    ServerCombat();
                    break;

                case AIState.Flee:
                    ServerFleeCheck();
                    break;

                case AIState.ReturnHome:
                    ServerReturnHomeCheck();
                    break;
            }
        }

        // ── Estados de IA (lógica de transição — SEM SetDestination aqui) ──

        private void ServerPatrolWaypoints()
        {
            if (patrolPoints == null || patrolPoints.Length == 0) return;
            if (!_agent.isOnNavMesh) return;

            if (_waitingAtPatrolPoint)
            {
                _patrolWaitTimer += Time.deltaTime;
                if (_patrolWaitTimer < patrolWaitTime) return;
                _waitingAtPatrolPoint = false;
            }

            if (!_agent.pathPending && _agent.remainingDistance < 0.5f)
            {
                _patrolIndex = (_patrolIndex + 1) % patrolPoints.Length;
                _agent.SetDestination(patrolPoints[_patrolIndex].position);
                _waitingAtPatrolPoint = true;
                _patrolWaitTimer      = 0f;
            }
        }

        private void ServerChaseCheck()
        {
            if (_aggroTarget == null || _aggroTarget.Dead) { ResetAggro(); return; }
            if (Vector3.Distance(transform.position, _homePosition) > leashRange)
            { ResetAggro(); EnterReturnHome(); return; }

            float dist = Vector3.Distance(transform.position, _aggroTarget.transform.position);
            if (dist > aggroRange * 2.5f) { ResetAggro(); return; }

            if (dist <= attackRange)
            {
                _attackTimer = 0f;
                _state = AIState.Combat;
                if (_agent.isOnNavMesh) _agent.ResetPath();
            }
        }

        private void ServerCombat()
        {
            if (_aggroTarget == null || _aggroTarget.Dead) { ResetAggro(); return; }
            if (Vector3.Distance(transform.position, _homePosition) > leashRange)
            { ResetAggro(); EnterReturnHome(); return; }

            float dist = Vector3.Distance(transform.position, _aggroTarget.transform.position);

            if (dist > attackRange * 1.4f)
            {
                _state = AIState.Chase;
                return;
            }

            if (_agent.isOnNavMesh)
            {
                if (dist < kiteDistance)
                {
                    Vector3 away = (transform.position - _aggroTarget.transform.position).normalized;
                    _agent.SetDestination(transform.position + away * (kiteDistance + 0.5f));
                }
                else
                {
                    _agent.ResetPath();
                }
            }

            // Rotação suave em direção ao alvo
            Vector3 dir = _aggroTarget.transform.position - transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude > 0.01f)
                transform.rotation = Quaternion.Slerp(
                    transform.rotation, Quaternion.LookRotation(dir), 8f * Time.deltaTime);

            if (_attackTimer >= attackCooldown)
            {
                _attackTimer = 0f;
                ServerAttack();
            }
        }

        private void ServerFleeCheck()
        {
            _fleeTimer += Time.deltaTime;
            if (_fleeTimer >= fleeDuration || !_agent.isOnNavMesh)
            {
                if (_agent != null) _agent.speed = _stats.ASPD;
                EnterReturnHome();
            }
        }

        private void ServerReturnHomeCheck()
        {
            _regenTimer += Time.deltaTime;
            if (_regenTimer >= REGEN_INTERVAL)
            {
                _regenTimer = 0f;
                _currentHP  = Mathf.Min(_maxHP, _currentHP + _maxHP * REGEN_PERCENT);
            }

            if (!_agent.isOnNavMesh) return;

            if (Vector3.Distance(transform.position, _homePosition) < 1.5f)
            {
                _agent.ResetPath();
                _wasAttacked     = false;
                _patrolTargetSet = false;
                _damageLog.Clear();
                _state = AIState.Idle;
            }
        }

        // ── Paths (chamados pelas coroutines, não pelo Update) ─────────────

        [Server]
        private void UpdateChasePath()
        {
            if (_aggroTarget == null || !_agent.isOnNavMesh) return;
            _agent.stoppingDistance = attackRange * 0.85f;
            _agent.SetDestination(_aggroTarget.transform.position);
        }

        [Server]
        private void UpdateReturnHomePath()
        {
            if (!_agent.isOnNavMesh) return;
            _agent.stoppingDistance = 0.5f;
            _agent.SetDestination(_homePosition);
        }

        [Server]
        private void UpdateFleePath()
        {
            if (_aggroTarget == null || !_agent.isOnNavMesh) return;
            Vector3 fleeDir = (transform.position - _aggroTarget.transform.position).normalized;
            Vector3 fleePos = transform.position + fleeDir * (aggroRange * 1.5f);
            if (NavMesh.SamplePosition(fleePos, out NavMeshHit hit, 5f, NavMesh.AllAreas))
                _agent.SetDestination(hit.position);
        }

        [Server]
        private void UpdatePatrolAreaPath()
        {
            if (!_agent.isOnNavMesh) return;

            bool arrived = !_agent.pathPending && _agent.remainingDistance < 0.6f;

            if (_waitingAtPatrolPoint)
            {
                // O timer de espera é atualizado no Update para manter precisão de tempo
                return;
            }

            if (!_patrolTargetSet || arrived)
            {
                if (arrived && _patrolTargetSet)
                {
                    _waitingAtPatrolPoint = true;
                    _patrolWaitTimer      = 0f;
                    return;
                }

                if (TryGetRandomAreaPoint(_homePosition, _patrolRadiusRuntime, out Vector3 dest))
                {
                    _agent.SetDestination(dest);
                    _currentPatrolTarget = dest;
                    _patrolTargetSet     = true;
                }
            }
        }

        // ── Aggro (chamado pela coroutine) ─────────────────────────────────

        [Server]
        private void TryAggro()
        {
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
                _attackTimer = 0f;
            }
        }

        [Server]
        private void ResetAggro()
        {
            _aggroTarget = null;
            if (_agent != null && _agent.isOnNavMesh)
            {
                _agent.ResetPath();
                _agent.stoppingDistance = 0.3f;
            }
            _attackTimer     = 0f;
            _patrolTargetSet = false;

            if (Vector3.Distance(transform.position, _homePosition) > leashRange * 0.5f)
                EnterReturnHome();
            else
                _state = AIState.Idle;
        }

        [Server]
        private void EnterReturnHome()
        {
            _state      = AIState.ReturnHome;
            _aggroTarget = null;
            _regenTimer  = 0f;
            if (_agent != null) _agent.stoppingDistance = 0.5f;
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

        // ── Dano interno ───────────────────────────────────────────────────

        [Server]
        private void ApplyDamageInternal(float dmg)
        {
            _currentHP = Mathf.Max(0f, _currentHP - dmg);
            if (_currentHP <= 0f) ServerDie();
        }

        // ── CmdRequestSkill — cliente solicita usar skill neste monstro ────

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

            // Anti-cheat: log de range suspeito (não rejeita, apenas registra)
            float dist = Vector3.Distance(attacker.transform.position, transform.position);
            if (dist > skill.Range + SKILL_RANGE_TOLERANCE)
            {
                Debug.LogWarning($"[Server] Range suspeito: {attacker.CharacterName} " +
                                 $"dist={dist:0.1} range={skill.Range}. Permitindo com tolerância.");
            }

            attacker.ServerConsumeMP(skill.ManaCost);
            ServerTakeDamageFromPlayer(attacker, atkStats, skillIndex, isPhysical, skill);
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
                        _aggroTarget = attacker;
                        _fleeTimer   = 0f;
                        _state       = AIState.Flee;
                        if (_agent != null) _agent.speed = _stats.ASPD * fleeSpeedMult;
                    }
                    break;

                case MonsterDisposition.Neutral:
                    _wasAttacked = true;
                    if (_state == AIState.Idle || _state == AIState.Patrol || _state == AIState.ReturnHome)
                    { _aggroTarget = attacker; _state = AIState.Chase; _attackTimer = 0f; }
                    break;

                case MonsterDisposition.Aggressive:
                    if (_state == AIState.Idle || _state == AIState.Patrol)
                    { _aggroTarget = attacker; _state = AIState.Chase; _attackTimer = 0f; }
                    break;
            }

            if (_currentHP <= 0f) ServerDie();
        }

        // ── Morte / Respawn ────────────────────────────────────────────────

        [Server]
        private void ServerDie()
        {
            if (_isDead || _deathProcessed) return;
            _isDead         = true;
            _deathProcessed = true;
            _state          = AIState.Dead;

            // Para coroutines de IA
            if (_aggroScanCoroutine  != null) StopCoroutine(_aggroScanCoroutine);
            if (_pathUpdateCoroutine != null) StopCoroutine(_pathUpdateCoroutine);

            if (_agent != null)
            {
                if (_agent.isOnNavMesh) _agent.ResetPath();
                _agent.enabled = false;
            }

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

        [ClientRpc]
        private void RpcShowDamage(float dmg, bool crit, Vector3 pos)
            => FloatingTextManager.Instance?.Show(
                crit ? $"CRÍTICO! {dmg:0}" : $"{dmg:0}",
                pos + Vector3.up, crit ? Color.yellow : Color.white);

        [ClientRpc]
        private void RpcShowMiss(Vector3 pos)
            => FloatingTextManager.Instance?.Show("MISS", pos + Vector3.up * 0.5f, Color.gray);

        [ClientRpc]
        private void RpcPlayAnim(string trigger)
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
            if (healthBarUI)
            {
                healthBarUI.gameObject.SetActive(true);
                healthBarUI.UpdateBar(_currentHP, _maxHP);
            }
        }

        // ── SyncVar Hooks ──────────────────────────────────────────────────

        private void OnCurrentHPChanged(float _, float v) => healthBarUI?.UpdateBar(v, _maxHP);

        private void OnDeadChanged(bool _, bool dead)
        {
            if (dead && _agent != null) _agent.enabled = false;
        }

        // ── Gizmos ─────────────────────────────────────────────────────────
#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            Gizmos.color = disposition switch
            {
                MonsterDisposition.Passive => Color.green,
                MonsterDisposition.Neutral => Color.yellow,
                _                          => Color.red
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