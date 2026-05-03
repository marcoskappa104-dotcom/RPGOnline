using UnityEngine;
using UnityEngine.AI;
using Mirror;
using RPG.Data;
using RPG.UI;
using RPG.Character;
using System.Collections;
using System.Collections.Generic;

namespace RPG.Network
{
    public enum MonsterDisposition { Passive, Neutral, Aggressive }

    /// <summary>
    /// NetworkMonsterEntity v8 — SERVIDOR É AUTORIDADE
    ///
    /// CORREÇÕES DESTA VERSÃO:
    ///
    ///   1. TakeDamage() — dano calculado no servidor.
    ///      CmdRequestTakeDamage era chamado com rawAtk já calculado pelo cliente.
    ///      Agora o cliente envia apenas CmdRequestAttack(netId do monstro, índice da skill).
    ///      O servidor busca os stats do atacante via NetworkPlayer.ServerStats e
    ///      calcula hit/crit/dano ele mesmo. Nenhum valor de dano vem do cliente.
    ///
    ///   2. XP — distribuído via NetworkPlayer.ServerGrantExp().
    ///      RpcGrantExp foi removido. O servidor chama ServerGrantExp() diretamente
    ///      no(s) NetworkPlayer(s) que participaram do combate.
    ///      Sistema de damage log: somente quem causou dano recebe XP.
    ///
    ///   3. Participação em combate rastreada por _damageLog (netId → dano total).
    ///      XP distribuído proporcionalmente entre participantes.
    ///
    ///   4. Disposição, patrulha, leash, fuga e retorno para home mantidos da v7.
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    [RequireComponent(typeof(NetworkIdentity))]
    [RequireComponent(typeof(NetworkTransformReliable))]
    public class NetworkMonsterEntity : NetworkBehaviour, ITargetable
    {
        // ── Config — Identidade ───────────────────────────────────────────
        [Header("Identidade")]
        [SerializeField] private string monsterDisplayName = "Monstro";
        [SerializeField] private int    level              = 1;

        // ── Config — Disposição ───────────────────────────────────────────
        [Header("Comportamento")]
        [SerializeField] private MonsterDisposition disposition = MonsterDisposition.Aggressive;

        // ── Config — Atributos ────────────────────────────────────────────
        [Header("Atributos Base")]
        [SerializeField] private int baseSTR = 12;
        [SerializeField] private int baseAGI = 8;
        [SerializeField] private int baseVIT = 10;
        [SerializeField] private int baseDEX = 8;
        [SerializeField] private int baseINT = 5;
        [SerializeField] private int baseLUK = 5;

        // ── Config — Ranges ───────────────────────────────────────────────
        [Header("Ranges de IA")]
        [SerializeField] private float aggroRange     = 10f;
        [SerializeField] private float attackRange    = 2.5f;
        [SerializeField] private float kiteDistance   = 1.8f;
        [SerializeField] private float attackCooldown = 2f;
        [SerializeField] private float leashRange     = 30f;
        [SerializeField] private float pathUpdateRate = 0.2f;

        // ── Config — Patrulha ─────────────────────────────────────────────
        [Header("Patrulha")]
        [SerializeField] private bool        usePatrolPoints = false;
        [SerializeField] private Transform[] patrolPoints;
        [SerializeField] private float       patrolWaitTime  = 2f;
        [SerializeField] private float       patrolRadius    = 12f;

        // ── Config — Fuga ─────────────────────────────────────────────────
        [Header("Fuga (apenas Passive)")]
        [SerializeField] private float fleeDuration  = 6f;
        [SerializeField] private float fleeSpeedMult = 1.5f;

        // ── Config — Morte e Respawn ──────────────────────────────────────
        [Header("Morte e Respawn")]
        [SerializeField] private float hideDelay    = 3f;
        [SerializeField] private float respawnDelay = 15f;

        [Header("Recompensa")]
        [SerializeField] private long expReward = 50;

        // ── Config — Visuals ──────────────────────────────────────────────
        [Header("Visuals")]
        [SerializeField] private GameObject         selectionIndicator;
        [SerializeField] private MonsterHealthBarUI healthBarUI;
        [SerializeField] private GameObject         visualRoot;

        // ── SyncVars ──────────────────────────────────────────────────────
        [SyncVar(hook = nameof(OnCurrentHPChanged))]
        private float _currentHP;

        [SyncVar]
        private float _maxHP;

        [SyncVar(hook = nameof(OnDeadChanged))]
        private bool _isDead;

        // ── ITargetable ───────────────────────────────────────────────────
        public string  DisplayName => monsterDisplayName;
        public float   CurrentHP   => _currentHP;
        public float   MaxHP       => _maxHP;
        public bool    IsDead      => _isDead;
        public Vector3 Position    => transform.position;

        public void OnSelected()   { if (selectionIndicator) selectionIndicator.SetActive(true);  }
        public void OnDeselected() { if (selectionIndicator) selectionIndicator.SetActive(false); }

        // ── Stats ─────────────────────────────────────────────────────────
        private DerivedStats _stats;

        // ── Damage log — rastrea quem causou dano para distribuição de XP ─
        // Chave: netId do NetworkPlayer | Valor: dano total causado
        private Dictionary<uint, float> _damageLog = new Dictionary<uint, float>();

        // ── IA (server only) ──────────────────────────────────────────────
        private enum State { Idle, Patrol, Chase, Combat, Flee, ReturnHome, Dead }

        private State         _state = State.Idle;
        private NavMeshAgent  _agent;
        private Animator      _animator;
        private NetworkPlayer _aggroTarget;

        private float _attackTimer;
        private float _pathTimer;
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

        private const float REGEN_INTERVAL = 3f;
        private const float REGEN_PERCENT  = 0.05f;

        // ── Init ──────────────────────────────────────────────────────────

        private void Awake()
        {
            _agent    = GetComponent<NavMeshAgent>();
            _animator = GetComponentInChildren<Animator>();

            var attrs = new BaseAttributes
            {
                STR = baseSTR, AGI = baseAGI, VIT = baseVIT,
                DEX = baseDEX, INT = baseINT, LUK = baseLUK
            };
            _stats = StatsCalculator.Calculate(attrs, level);
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

        // ── Spawn Data ────────────────────────────────────────────────────

        [Server]
        public void SetSpawnData(Vector3 homePosition, float newPatrolRadius)
        {
            _homePosition        = homePosition;
            _patrolRadiusRuntime = newPatrolRadius;
            transform.position   = homePosition;
            _patrolTargetSet     = false;
        }

        // ── Reset / Respawn ───────────────────────────────────────────────

        [Server]
        private void ServerReset()
        {
            _maxHP          = _stats.MaxHP;
            _currentHP      = _maxHP;
            _isDead         = false;
            _wasAttacked    = false;
            _state          = State.Idle;
            _aggroTarget    = null;
            _attackTimer    = 0f;
            _pathTimer      = 0f;
            _fleeTimer      = 0f;
            _regenTimer     = 0f;
            _patrolIndex    = 0;
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

            RpcOnRespawned();
        }

        // ── Update ────────────────────────────────────────────────────────

        private void Update()
        {
            if (!isServer || _isDead) return;

            _pathTimer   += Time.deltaTime;
            _attackTimer += Time.deltaTime;

            switch (_state)
            {
                case State.Idle:       ServerIdle();       break;
                case State.Patrol:     ServerPatrol();     break;
                case State.Chase:      ServerChase();      break;
                case State.Combat:     ServerCombat();     break;
                case State.Flee:       ServerFlee();       break;
                case State.ReturnHome: ServerReturnHome(); break;
            }
        }
public void TakeDamage(float rawAtk, float rawMatk, bool isPhysical)
{
    // Compatibilidade com ITargetable
    // Sistema real usa CmdRequestAttack

    if (!isServer) return;

    float dmg = isPhysical ? rawAtk : rawMatk;
    dmg = Mathf.Max(1f, dmg);

    _currentHP = Mathf.Max(0f, _currentHP - dmg);

    RpcShowDamage(dmg, false, transform.position);

    if (_currentHP <= 0f)
        ServerDie();
}
        // ── Estados de IA ─────────────────────────────────────────────────

        private void ServerIdle()
        {
            if (TryAggro()) return;

            bool hasPatrolArea   = _patrolRadiusRuntime > 0.5f && !usePatrolPoints;
            bool hasPatrolPoints = usePatrolPoints && patrolPoints != null && patrolPoints.Length > 0;

            if (hasPatrolArea || hasPatrolPoints)
                _state = State.Patrol;
        }

        private void ServerPatrol()
        {
            if (TryAggro()) return;

            if (usePatrolPoints)
                PatrolWithWaypoints();
            else
                PatrolInArea();
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
                if (_patrolWaitTimer >= patrolWaitTime)
                {
                    _waitingAtPatrolPoint = false;
                    _patrolTargetSet      = false;
                }
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

        private void ServerChase()
        {
            if (_aggroTarget == null || _aggroTarget.Dead) { ResetAggro(); return; }

            float distFromHome = Vector3.Distance(transform.position, _homePosition);
            if (distFromHome > leashRange) { ResetAggro(); EnterReturnHome(); return; }

            float distToTarget = Vector3.Distance(transform.position, _aggroTarget.transform.position);
            if (distToTarget > aggroRange * 2.5f) { ResetAggro(); return; }

            if (distToTarget <= attackRange)
            {
                _attackTimer = 0f;
                _state       = State.Combat;
                _agent.ResetPath();
                return;
            }

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

            float distFromHome = Vector3.Distance(transform.position, _homePosition);
            if (distFromHome > leashRange) { ResetAggro(); EnterReturnHome(); return; }

            float dist = Vector3.Distance(transform.position, _aggroTarget.transform.position);

            if (dist > attackRange * 1.4f) { _state = State.Chase; return; }

            if (_agent.isOnNavMesh)
            {
                if (dist < kiteDistance)
                {
                    Vector3 away = (transform.position - _aggroTarget.transform.position).normalized;
                    _agent.SetDestination(transform.position + away * (kiteDistance + 0.5f));
                }
                else
                    _agent.ResetPath();
            }

            Vector3 dir = (_aggroTarget.transform.position - transform.position);
            dir.y = 0f;
            if (dir.sqrMagnitude > 0.01f)
                transform.rotation = Quaternion.Slerp(
                    transform.rotation, Quaternion.LookRotation(dir), 10f * Time.deltaTime);

            if (_attackTimer >= attackCooldown)
            {
                _attackTimer = 0f;
                ServerAttack();
            }
        }

        private void ServerFlee()
        {
            _fleeTimer += Time.deltaTime;

            if (_fleeTimer >= fleeDuration || !_agent.isOnNavMesh)
            {
                _agent.speed = _stats.ASPD;
                EnterReturnHome();
                return;
            }

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
                _wasAttacked     = false;
                _patrolTargetSet = false;
                _damageLog.Clear(); // limpa log ao voltar para casa
                _state = State.Idle;
            }
        }

        private void EnterReturnHome()
        {
            _state          = State.ReturnHome;
            _aggroTarget    = null;
            _regenTimer     = 0f;
            _agent.stoppingDistance = 0.5f;
        }

        // ── Aggro ─────────────────────────────────────────────────────────

        private bool TryAggro()
        {
            if (disposition == MonsterDisposition.Passive) return false;
            if (disposition == MonsterDisposition.Neutral && !_wasAttacked) return false;

            float         closest = aggroRange;
            NetworkPlayer found   = null;

            foreach (var np in NetworkPlayer.All)
            {
                if (np.Dead) continue;
                float dist = Vector3.Distance(transform.position, np.transform.position);
                if (dist < closest) { closest = dist; found = np; }
            }

            if (found != null)
            {
                _aggroTarget = found;
                _state       = State.Chase;
                _pathTimer   = pathUpdateRate;
                _attackTimer = 0f;
                return true;
            }
            return false;
        }

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

            float distToHome = Vector3.Distance(transform.position, _homePosition);
            if (distToHome > leashRange * 0.5f)
                EnterReturnHome();
            else
                _state = State.Idle;
        }

        // ── Ataque do monstro → jogador ───────────────────────────────────

        [Server]
        private void ServerAttack()
        {
            if (_aggroTarget == null || _aggroTarget.Dead) return;

            // Pega FLEE do jogador via ServerStats (calculado no servidor)
            float playerFlee = _aggroTarget.ServerStats?.FLEE ?? 20f;

            bool hit = StatsCalculator.RollHit(_stats.HIT, playerFlee);
            if (!hit) { RpcShowMiss(_aggroTarget.transform.position); return; }

            bool  crit = StatsCalculator.RollCrit(_stats.CRIT);

            // Pega DEF do jogador via ServerStats
            float playerDEF = _aggroTarget.ServerStats?.DEF ?? 10f;
            float dmg = StatsCalculator.CalculatePhysicalDamage(
                            _stats.ATK, playerDEF, crit, _stats.CritDMG);

            _aggroTarget.ServerApplyDamage(dmg);
            RpcPlayAnim("Attack");
        }

        // ── TakeDamage — requisição vinda do cliente ──────────────────────

        /// <summary>
        /// Chamado pelo SkillSystem do cliente via Command.
        /// Recebe apenas o índice da skill e o netId do atacante.
        /// O servidor busca os stats do atacante e calcula tudo.
        /// NENHUM valor de dano vem do cliente.
        /// </summary>
        [Command(requiresAuthority = false)]
        public void CmdRequestAttack(uint attackerNetId, int skillIndex, bool isPhysical)
        {
            // Encontra o NetworkPlayer atacante pelo netId
            NetworkPlayer attacker = null;
            foreach (var np in NetworkPlayer.All)
            {
                if (np.netId == attackerNetId) { attacker = np; break; }
            }

            if (attacker == null || attacker.Dead) return;
            if (_isDead) return;

            // Busca stats do atacante calculados no servidor
            var atkStats = attacker.ServerStats;
            if (atkStats == null) return;

            ServerTakeDamageFromPlayer(attacker, atkStats, skillIndex, isPhysical);
        }

        [Server]
        private void ServerTakeDamageFromPlayer(
            NetworkPlayer attacker, DerivedStats atkStats,
            int skillIndex, bool isPhysical)
        {
            // Hit roll: HIT do atacante vs FLEE do monstro
            bool hit = StatsCalculator.RollHit(atkStats.HIT, _stats.FLEE);
            if (!hit)
            {
                RpcShowMiss(transform.position);
                return;
            }

            bool  crit = StatsCalculator.RollCrit(atkStats.CRIT);
            float dmg  = isPhysical
                ? StatsCalculator.CalculatePhysicalDamage(atkStats.ATK, _stats.DEF, crit, atkStats.CritDMG)
                : StatsCalculator.CalculateMagicDamage(atkStats.MATK, _stats.MDEF, crit, atkStats.CritDMG);

            dmg = Mathf.Max(1f, dmg);

            // Registra dano no log para distribuição de XP
            if (!_damageLog.ContainsKey(attacker.netId))
                _damageLog[attacker.netId] = 0f;
            _damageLog[attacker.netId] += dmg;

            _currentHP = Mathf.Max(0f, _currentHP - dmg);

            RpcShowDamage(dmg, crit, transform.position);

            // Reage ao dano conforme disposição
            switch (disposition)
            {
                case MonsterDisposition.Passive:
                    if (_state != State.Flee && _state != State.ReturnHome && _state != State.Dead)
                    {
                        _aggroTarget   = attacker;
                        _fleeTimer     = 0f;
                        _state         = State.Flee;
                        _agent.speed   = _stats.ASPD * fleeSpeedMult;
                        _pathTimer     = pathUpdateRate;
                    }
                    break;

                case MonsterDisposition.Neutral:
                    _wasAttacked = true;
                    if (_state == State.Idle || _state == State.Patrol || _state == State.ReturnHome)
                    {
                        _aggroTarget = attacker;
                        _state       = State.Chase;
                        _pathTimer   = pathUpdateRate;
                        _attackTimer = 0f;
                    }
                    break;

                case MonsterDisposition.Aggressive:
                    if (_state == State.Idle || _state == State.Patrol)
                    {
                        _aggroTarget = attacker;
                        _state       = State.Chase;
                        _pathTimer   = pathUpdateRate;
                        _attackTimer = 0f;
                    }
                    break;
            }

            if (_currentHP <= 0f) ServerDie();
        }

        // ── Morte e Respawn ───────────────────────────────────────────────

        [Server]
        private void ServerDie()
        {
            _isDead = true;
            _state  = State.Dead;

            if (_agent != null && _agent.isOnNavMesh)
            {
                _agent.ResetPath();
                _agent.enabled = false;
            }

            Debug.Log($"[NetworkMonster] {monsterDisplayName} morreu!");

            // Distribui XP apenas para quem participou do combate
            ServerDistributeExp();

            StartCoroutine(ServerDeathSequence());
        }

        /// <summary>
        /// Distribui XP proporcionalmente entre todos os jogadores que causaram dano.
        /// Cada um recebe (sua_parcela_de_dano / dano_total) * expReward.
        /// Mínimo de 1 XP por participação.
        /// </summary>
        [Server]
        private void ServerDistributeExp()
        {
            if (_damageLog.Count == 0) return;

            float totalDamage = 0f;
            foreach (var kv in _damageLog)
                totalDamage += kv.Value;

            if (totalDamage <= 0f) return;

            foreach (var kv in _damageLog)
            {
                uint attackerNetId = kv.Key;
                float proportion   = kv.Value / totalDamage;
                long  xpShare      = Mathf.Max(1, (int)(expReward * proportion));

                // Encontra o NetworkPlayer e concede XP no servidor
                foreach (var np in NetworkPlayer.All)
                {
                    if (np.netId == attackerNetId)
                    {
                        np.ServerGrantExp(xpShare);
                        break;
                    }
                }
            }

            _damageLog.Clear();
        }

        [Server]
        private IEnumerator ServerDeathSequence()
        {
            RpcOnDied(transform.position);

            if (hideDelay > 0f)
                yield return new WaitForSeconds(hideDelay);

            RpcHideVisuals();

            if (respawnDelay <= 0f) yield break;

            yield return new WaitForSeconds(respawnDelay);
            if (isServer) ServerReset();
        }

        // ── NavMesh Helper ────────────────────────────────────────────────

        private bool TryGetRandomAreaPoint(Vector3 center, float radius, out Vector3 result)
        {
            for (int i = 0; i < 15; i++)
            {
                Vector2 rand2D    = Random.insideUnitCircle * radius;
                Vector3 candidate = center + new Vector3(rand2D.x, 0f, rand2D.y);

                if (NavMesh.SamplePosition(candidate, out NavMeshHit hit, 3f, NavMesh.AllAreas))
                {
                    result = hit.position;
                    return true;
                }
            }

            result = center;
            return false;
        }

        // ── ClientRpcs ────────────────────────────────────────────────────

        [ClientRpc]
        private void RpcShowDamage(float dmg, bool crit, Vector3 pos)
        {
            Color c = crit ? Color.yellow : Color.white;
            FloatingTextManager.Instance?.Show(
                crit ? $"CRÍTICO! {dmg:0}" : $"{dmg:0}", pos + Vector3.up, c);
        }

        [ClientRpc]
        private void RpcShowMiss(Vector3 pos)
            => FloatingTextManager.Instance?.Show("MISS", pos, Color.gray);

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
            if (visualRoot         != null) visualRoot.SetActive(false);
            if (selectionIndicator != null) selectionIndicator.SetActive(false);
            if (healthBarUI        != null) healthBarUI.gameObject.SetActive(false);
        }

        [ClientRpc]
        private void RpcOnRespawned()
        {
            if (visualRoot         != null) visualRoot.SetActive(true);
            if (selectionIndicator != null) selectionIndicator.SetActive(false);
            if (healthBarUI != null)
            {
                healthBarUI.gameObject.SetActive(true);
                healthBarUI.UpdateBar(_currentHP, _maxHP);
            }
        }

        // ── SyncVar Hooks ─────────────────────────────────────────────────

        private void OnCurrentHPChanged(float _, float newVal)
            => healthBarUI?.UpdateBar(newVal, _maxHP);

        private void OnDeadChanged(bool _, bool nowDead)
        {
            if (nowDead && _agent != null) _agent.enabled = false;
        }

        // ── Gizmos ────────────────────────────────────────────────────────

        private void OnDrawGizmosSelected()
        {
            Color aggroColor = disposition switch
            {
                MonsterDisposition.Passive    => Color.green,
                MonsterDisposition.Neutral    => Color.yellow,
                MonsterDisposition.Aggressive => Color.red,
                _                             => Color.red
            };

            Gizmos.color = aggroColor;
            Gizmos.DrawWireSphere(transform.position, aggroRange);

            Gizmos.color = new Color(1f, 0.3f, 0.3f);
            Gizmos.DrawWireSphere(transform.position, attackRange);

            Gizmos.color = Color.blue;
            Gizmos.DrawWireSphere(transform.position, kiteDistance);

            Gizmos.color = new Color(1f, 1f, 1f, 0.4f);
            Gizmos.DrawWireSphere(transform.position, leashRange);

            if (Application.isPlaying)
            {
                Gizmos.color = new Color(1f, 0.8f, 0f, 0.5f);
                Gizmos.DrawWireSphere(_homePosition, _patrolRadiusRuntime);
                Gizmos.color = Color.cyan;
                Gizmos.DrawSphere(_homePosition, 0.3f);

                if (_patrolTargetSet)
                {
                    Gizmos.color = Color.magenta;
                    Gizmos.DrawSphere(_currentPatrolTarget, 0.25f);
                    Gizmos.DrawLine(transform.position, _currentPatrolTarget);
                }
            }
            else
            {
                Gizmos.color = new Color(1f, 0.8f, 0f, 0.3f);
                Gizmos.DrawWireSphere(transform.position, patrolRadius);
            }
        }
    }
}