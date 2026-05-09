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
    /// NetworkMonsterEntity v22
    ///
    /// CORREÇÕES v22:
    ///
    ///   1. ATTACK_RANGE_TOLERANCE ajustado de 1.3f para 1.35f:
    ///      Com SkillSystem v9 usando RANGE_CHECK_MARGIN = 1.05f no cliente,
    ///      o servidor precisa aceitar uma tolerância levemente maior para evitar
    ///      rejeições falsas por latência. 1.35x é seguro e cobre jitter de rede
    ///      sem abrir exploits significativos em LAN/WAN local.
    ///
    ///   2. CHASE_DEST_FRACTION ajustado de 0.80f para 0.82f:
    ///      Alinhado com a mudança do BasicAttackSystem v3 (0.80) e SkillSystem v9 (0.85).
    ///      0.82 garante que o monstro para DENTRO do range de ataque.
    ///
    ///   3. ServerChaseCheck: quando entra em combate, _agent.velocity = Vector3.zero
    ///      para parar o deslizamento residual do NavMesh.
    ///
    ///   4. ServerCombat: ao parar para atacar, velocity zerada para evitar
    ///      que o monstro "deslize" pelo player após parar.
    ///
    ///   5. RegenLoop: REGEN_INTERVAL aumentado de 3s para 5s — alinhado com
    ///      ServerRegenLoop do NetworkPlayer (que usa 5s). Consistência entre monstro e player.
    ///
    ///   CORREÇÕES v21 mantidas:
    ///     - CmdBasicAttack usa ApplyDamageInternal (guard contra duplo kill)
    ///     - kiteDistance como fração do attackRange
    ///     - LayerMask e WaitForSeconds cacheados no Awake
    ///     - Destino de chase intermediário para não sobrepor player
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    [RequireComponent(typeof(NetworkIdentity))]
    public class NetworkMonsterEntity : NetworkBehaviour, ITargetable
    {
        [Header("Identidade")]
        [SerializeField] private string monsterDisplayName = "Monstro";
        [SerializeField] private int    level              = 1;

        [Header("Comportamento")]
        [SerializeField] private MonsterDisposition disposition = MonsterDisposition.Aggressive;

        [Header("Atributos Base")]
        [SerializeField] private int baseSTR = 12;
        [SerializeField] private int baseAGI = 8;
        [SerializeField] private int baseVIT = 10;
        [SerializeField] private int baseDEX = 8;
        [SerializeField] private int baseINT = 5;
        [SerializeField] private int baseLUK = 5;

        [Header("Ranges de IA")]
        [SerializeField] private float aggroRange     = 10f;
        [SerializeField] private float attackRange    = 2.5f;
        [SerializeField] private float leashRange     = 30f;

        [Header("Kite")]
        [Tooltip("Fração do attackRange. O monstro recua se o player entrar nessa distância.")]
        [SerializeField] private float kiteDistanceFraction = 0.50f;

        [Header("Performance de IA")]
        [SerializeField] private float aggroScanInterval = 0.5f;
        [SerializeField] private float pathUpdateRate    = 0.15f;

        [Header("Patrulha")]
        [SerializeField] private bool        usePatrolPoints = false;
        [SerializeField] private Transform[] patrolPoints;
        [SerializeField] private float       patrolWaitTime  = 2f;
        [SerializeField] private float       patrolRadius    = 12f;

        [Header("Fuga (apenas Passive)")]
        [SerializeField] private float fleeDuration  = 6f;
        [SerializeField] private float fleeSpeedMult = 1.3f;

        [Header("Morte e Respawn")]
        [SerializeField] private float hideDelay    = 3f;
        [SerializeField] private float respawnDelay = 15f;

        [Header("Recompensa")]
        [SerializeField] private long expReward = 50;

        [Range(0f, 100f)]
        [SerializeField] private float dropChance = 50f;
        [SerializeField] private List<RPG.Data.ItemData> dropTable        = new List<RPG.Data.ItemData>();
        [SerializeField] private List<string>             guaranteedDropIds = new List<string>();

        [Header("Visuals")]
        [SerializeField] private GameObject         selectionIndicator;
        [SerializeField] private MonsterHealthBarUI healthBarUI;
        [SerializeField] private GameObject         visualRoot;

        // CORREÇÃO v22: tolerância levemente aumentada para cobrir latência de rede
        // sem comprometer segurança anti-cheat
        private const float ATTACK_RANGE_TOLERANCE = 1.35f;

        // CORREÇÃO v22: 0.82f → monstro para dentro do range, alinhado com cliente
        private const float CHASE_DEST_FRACTION = 0.82f;

        // ── SyncVars ───────────────────────────────────────────────────────
        [SyncVar(hook = nameof(OnCurrentHPChanged))] private float _currentHP;
        [SyncVar]                                     private float _maxHP;
        [SyncVar(hook = nameof(OnDeadChanged))]       private bool  _isDead;
        [SyncVar(hook = nameof(OnIsMovingChanged))]   private bool  _isMoving;

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
            ApplyDamageInternal(Mathf.Max(1f, dmg));
        }

        // ── Estado interno ─────────────────────────────────────────────────
        private DerivedStats _stats;
        private readonly Dictionary<uint, float> _damageLog = new();

        private float _kiteDistance;

        private enum AIState { Idle, Patrol, Chase, Combat, Flee, ReturnHome, Dead }
        private AIState       _state = AIState.Idle;
        private NavMeshAgent  _agent;
        private Animator      _animator;
        private NetworkPlayer _aggroTarget;
        private bool          _wasAttacked;

        private float _attackAccumulator;
        private float _fleeTimer;
        private int   _patrolIndex;
        private bool  _patrolWaiting;
        private Vector3 _currentPatrolTarget;
        private bool    _patrolTargetSet;
        private Vector3 _homePosition;
        private float   _patrolRadiusRuntime;
        private bool    _serverResetDone = false;

        private int            _targetableLayerMask;
        private WaitForSeconds _aggroScanWait;
        private WaitForSeconds _pathUpdateWait;
        // CORREÇÃO v22: alinhado com NetworkPlayer ServerRegenLoop (5s)
        private WaitForSeconds _regenWait;

        private float _lastIsMovingUpdateTime;
        private const float MOVING_UPDATE_INTERVAL = 0.1f;

        private Coroutine _aggroScanCoroutine;
        private Coroutine _pathUpdateCoroutine;
        private Coroutine _patrolWaitCoroutine;
        private Coroutine _regenCoroutine;
        private bool      _deathProcessed = false;

        // CORREÇÃO v22: 5s — alinhado com ServerRegenLoop do player
        private const float REGEN_INTERVAL = 5f;
        private const float REGEN_PERCENT  = 0.05f;

        // ── Awake / OnStartServer ──────────────────────────────────────────

        private void Awake()
        {
            _agent    = GetComponent<NavMeshAgent>();
            _animator = GetComponentInChildren<Animator>();

            _stats = StatsCalculator.Calculate(
                new BaseAttributes { STR = baseSTR, AGI = baseAGI, VIT = baseVIT,
                                     DEX = baseDEX, INT = baseINT, LUK = baseLUK },
                level, CharacterRace.Human);

            _homePosition        = transform.position;
            _patrolRadiusRuntime = patrolRadius;
            _kiteDistance        = attackRange * kiteDistanceFraction;

            int layer = LayerMask.NameToLayer("Targetable");
            _targetableLayerMask = layer >= 0 ? (1 << layer) : 0;

            _aggroScanWait  = new WaitForSeconds(aggroScanInterval);
            _pathUpdateWait = new WaitForSeconds(pathUpdateRate);
            _regenWait      = new WaitForSeconds(REGEN_INTERVAL);
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
            StartCoroutine(ServerResetNextFrame());
        }

        [Server]
        private IEnumerator ServerResetNextFrame()
        {
            yield return null;
            if (!_serverResetDone) ServerReset();
        }

        [Server]
        private void ServerReset()
        {
            _serverResetDone   = true;
            _maxHP             = _stats.MaxHP;
            _currentHP         = _maxHP;
            _isDead            = false;
            _isMoving          = false;
            _deathProcessed    = false;
            _wasAttacked       = false;
            _state             = AIState.Patrol;
            _aggroTarget       = null;
            _attackAccumulator = 0f;
            _fleeTimer         = 0f;
            _patrolIndex       = 0;
            _patrolWaiting     = false;
            _patrolTargetSet   = false;
            _damageLog.Clear();

            _kiteDistance = attackRange * kiteDistanceFraction;

            if (_agent != null)
            {
                _agent.enabled          = true;
                _agent.speed            = _stats.MoveSpeed;
                _agent.angularSpeed     = 360f;
                _agent.acceleration     = 12f;
                _agent.stoppingDistance = 0.5f;
                _agent.velocity         = Vector3.zero;
                if (_agent.isOnNavMesh) _agent.Warp(_homePosition);
                else                    transform.position = _homePosition;
            }
            else { transform.position = _homePosition; }

            StopAllCoroutines();
            _patrolWaitCoroutine = null;
            _regenCoroutine      = null;
            _aggroScanCoroutine  = StartCoroutine(AggroScanLoop());
            _pathUpdateCoroutine = StartCoroutine(PathUpdateLoop());
            RpcOnRespawned();
        }

        // ── Update ─────────────────────────────────────────────────────────

        private void Update()
        {
            if (!isServer) return;
            if (!_serverResetDone) { ServerReset(); return; }
            if (_isDead) return;

            _attackAccumulator += Time.deltaTime;

            if (Time.time - _lastIsMovingUpdateTime >= MOVING_UPDATE_INTERVAL)
            {
                _lastIsMovingUpdateTime = Time.time;
                bool moving = _agent != null && _agent.velocity.sqrMagnitude > 0.05f;
                if (moving != _isMoving) _isMoving = moving;
            }

            switch (_state)
            {
                case AIState.Idle:       break;
                case AIState.Patrol:     if (usePatrolPoints) ServerPatrolWaypoints(); break;
                case AIState.Chase:      ServerChaseCheck();      break;
                case AIState.Combat:     ServerCombat();          break;
                case AIState.Flee:       ServerFleeCheck();       break;
                case AIState.ReturnHome: ServerReturnHomeCheck(); break;
            }
        }

        // ── Coroutines de IA ───────────────────────────────────────────────

        [Server]
        private IEnumerator AggroScanLoop()
        {
            while (true)
            {
                if (!_isDead &&
                    (_state == AIState.Idle || _state == AIState.Patrol))
                {
                    if (disposition == MonsterDisposition.Aggressive)
                        TryAggro();
                    else if (disposition == MonsterDisposition.Neutral && _wasAttacked)
                        TryAggro();
                }

                yield return _aggroScanWait;
            }
        }

        [Server]
        private IEnumerator PathUpdateLoop()
        {
            yield return null;
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
                yield return _pathUpdateWait;
            }
        }

        [Server]
        private IEnumerator PatrolWaitCoroutine()
        {
            _patrolWaiting = true;
            yield return new WaitForSeconds(patrolWaitTime);
            _patrolWaiting       = false;
            _patrolTargetSet     = false;
            _patrolWaitCoroutine = null;
        }

        [Server]
        private IEnumerator RegenLoop()
        {
            while (_state == AIState.ReturnHome)
            {
                yield return _regenWait;
                if (_state != AIState.ReturnHome) break;
                _currentHP = Mathf.Min(_maxHP, _currentHP + _maxHP * REGEN_PERCENT);
            }
            _regenCoroutine = null;
        }

        // ── Estados de IA ──────────────────────────────────────────────────

        private void ServerPatrolWaypoints()
        {
            if (patrolPoints == null || patrolPoints.Length == 0) return;
            if (!_agent.isOnNavMesh || _patrolWaiting) return;
            if (!_agent.pathPending && _agent.remainingDistance < 0.5f)
            {
                _patrolIndex = (_patrolIndex + 1) % patrolPoints.Length;
                _agent.SetDestination(patrolPoints[_patrolIndex].position);
                _patrolWaiting = true;
                StartCoroutine(PatrolWaitCoroutine());
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
                float ai = (_stats.ASPD > 0f) ? (1f / _stats.ASPD) : 1f;
                _attackAccumulator = ai * 0.5f;
                _state = AIState.Combat;
                // CORREÇÃO v22: para completamente ao entrar em combate
                if (_agent.isOnNavMesh)
                {
                    _agent.ResetPath();
                    _agent.stoppingDistance = 0.5f;
                    _agent.velocity         = Vector3.zero;
                }
            }
        }

        private void ServerCombat()
        {
            if (_aggroTarget == null || _aggroTarget.Dead) { ResetAggro(); return; }
            if (Vector3.Distance(transform.position, _homePosition) > leashRange)
            { ResetAggro(); EnterReturnHome(); return; }

            float dist = Vector3.Distance(transform.position, _aggroTarget.transform.position);

            if (dist > attackRange * 1.4f) { _state = AIState.Chase; return; }

            if (_agent.isOnNavMesh)
            {
                if (dist < _kiteDistance)
                {
                    Vector3 away = (transform.position - _aggroTarget.transform.position).normalized;
                    Vector3 kiteTarget = transform.position + away * (_kiteDistance + 0.5f);
                    _agent.stoppingDistance = 0.5f;
                    _agent.SetDestination(kiteTarget);
                }
                else
                {
                    // CORREÇÃO v22: velocity zerrada ao parar para atacar
                    if (_agent.hasPath)
                    {
                        _agent.ResetPath();
                        _agent.stoppingDistance = 0.5f;
                        _agent.velocity         = Vector3.zero;
                    }
                }
            }

            Vector3 dir = _aggroTarget.transform.position - transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude > 0.01f)
                transform.rotation = Quaternion.Slerp(
                    transform.rotation, Quaternion.LookRotation(dir), 8f * Time.deltaTime);

            float aiCombat = (_stats.ASPD > 0f) ? (1f / _stats.ASPD) : 1f;
            if (_attackAccumulator >= aiCombat)
            {
                _attackAccumulator -= aiCombat;
                ServerAttack();
            }
        }

        private void ServerFleeCheck()
        {
            _fleeTimer += Time.deltaTime;
            if (_fleeTimer >= fleeDuration || !_agent.isOnNavMesh)
            {
                if (_agent != null) _agent.speed = _stats.MoveSpeed;
                _fleeTimer = 0f;
                EnterReturnHome();
            }
        }

        private void ServerReturnHomeCheck()
        {
            if (!_agent.isOnNavMesh) return;
            if (Vector3.Distance(transform.position, _homePosition) < 1.5f)
            {
                _agent.ResetPath();
                _wasAttacked     = false;
                _patrolWaiting   = false;
                _patrolTargetSet = false;
                _damageLog.Clear();
                if (_regenCoroutine != null) { StopCoroutine(_regenCoroutine); _regenCoroutine = null; }
                _state = AIState.Patrol;
            }
        }

        // ── Paths ──────────────────────────────────────────────────────────

        [Server]
        private void UpdateChasePath()
        {
            if (_aggroTarget == null || !_agent.isOnNavMesh) return;

            // CORREÇÃO v22: destino intermediário com fração atualizada
            Vector3 destination = CalculateChaseDestination(_aggroTarget.transform.position);
            _agent.stoppingDistance = 0.2f; // fixo e pequeno — não múltiplo do range
            _agent.SetDestination(destination);
        }

        /// <summary>
        /// CORREÇÃO v22 — Calcula ponto de destino dentro do range de ataque.
        /// CHASE_DEST_FRACTION = 0.82f → para a 82% do range do player.
        /// stoppingDistance = 0.2f (fixo) → sem soma indesejada de frações.
        /// </summary>
        private Vector3 CalculateChaseDestination(Vector3 playerPos)
        {
            Vector3 toPlayer = playerPos - transform.position;
            float dist = toPlayer.magnitude;

            float safeStopDist = attackRange * CHASE_DEST_FRACTION;

            if (dist <= safeStopDist * 0.95f)
                return transform.position;

            Vector3 direction   = toPlayer.normalized;
            Vector3 destination = playerPos - direction * safeStopDist;

            if (NavMesh.SamplePosition(destination, out NavMeshHit hit, 2f, NavMesh.AllAreas))
                return hit.position;

            return destination;
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
            if (!_agent.isOnNavMesh || _patrolWaiting) return;
            bool arrived = !_agent.pathPending && _agent.remainingDistance < 0.6f;
            if (_patrolTargetSet && arrived)
            {
                if (_patrolWaitCoroutine == null)
                    _patrolWaitCoroutine = StartCoroutine(PatrolWaitCoroutine());
                return;
            }
            if (!_patrolTargetSet)
            {
                if (TryGetRandomAreaPoint(_homePosition, _patrolRadiusRuntime, out Vector3 dest))
                {
                    _agent.SetDestination(dest);
                    _currentPatrolTarget = dest;
                    _patrolTargetSet     = true;
                }
            }
        }

        // ── Aggro ──────────────────────────────────────────────────────────

        [Server]
        private void TryAggro()
        {
            if (_targetableLayerMask == 0) return;

            var cols = Physics.OverlapSphere(transform.position, aggroRange, _targetableLayerMask);

            NetworkPlayer found   = null;
            float         closest = aggroRange;

            foreach (var col in cols)
            {
                var np = col.GetComponent<NetworkPlayer>();
                if (np == null || np.Dead) continue;
                float d = Vector3.Distance(transform.position, np.transform.position);
                if (d < closest) { closest = d; found = np; }
            }

            if (found != null)
            {
                _aggroTarget       = found;
                _state             = AIState.Chase;
                float ai           = (_stats.ASPD > 0f) ? (1f / _stats.ASPD) : 1f;
                _attackAccumulator = ai * 0.3f;
                CancelPatrolWait();
            }
        }

        [Server]
        private void ResetAggro()
        {
            _aggroTarget = null;
            if (_agent != null && _agent.isOnNavMesh)
            {
                _agent.ResetPath();
                _agent.stoppingDistance = 0.5f;
                _agent.velocity         = Vector3.zero;
            }
            float ai = (_stats.ASPD > 0f) ? (1f / _stats.ASPD) : 1f;
            _attackAccumulator = ai * 0.3f;
            _patrolTargetSet   = false;

            if (Vector3.Distance(transform.position, _homePosition) > leashRange * 0.5f)
                EnterReturnHome();
            else { _patrolWaiting = false; _state = AIState.Patrol; }
        }

        [Server]
        private void EnterReturnHome()
        {
            _state       = AIState.ReturnHome;
            _aggroTarget = null;
            CancelPatrolWait();
            if (_agent != null)
            {
                _agent.stoppingDistance = 0.5f;
                _agent.velocity         = Vector3.zero;
            }
            if (_regenCoroutine != null) StopCoroutine(_regenCoroutine);
            _regenCoroutine = StartCoroutine(RegenLoop());
        }

        private void CancelPatrolWait()
        {
            if (_patrolWaitCoroutine != null)
            {
                StopCoroutine(_patrolWaitCoroutine);
                _patrolWaitCoroutine = null;
            }
            _patrolWaiting = false;
        }

        // ── Ataque do monstro ──────────────────────────────────────────────

        [Server]
        private void ServerAttack()
        {
            if (_aggroTarget == null || _aggroTarget.Dead) return;
            bool  hit  = StatsCalculator.RollHit(_stats.HIT, _aggroTarget.ServerStats?.FLEE ?? 20f);
            if (!hit) { RpcShowMiss(_aggroTarget.transform.position); return; }
            bool  crit = StatsCalculator.RollCrit(_stats.CRIT);
            float dmg  = StatsCalculator.CalculatePhysicalDamage(
                _stats.ATK, _aggroTarget.ServerStats?.DEF ?? 10f, crit, _stats.CritDMG);
            if (!_aggroTarget.Dead) _aggroTarget.ServerApplyDamage(dmg);
            RpcPlayAnim("Attack");
        }

        [Server]
        private void ApplyDamageInternal(float dmg)
        {
            if (_isDead || _deathProcessed) return;
            if (dmg <= 0f) return;

            _currentHP = Mathf.Max(0f, _currentHP - dmg);
            if (_currentHP <= 0f) ServerDie();
        }

        private static NetworkPlayer FindPlayerByNetId(uint netId)
        {
            foreach (var np in NetworkPlayer.All)
                if (np != null && np.netId == netId) return np;
            return null;
        }

        // ── CmdRequestSkill ────────────────────────────────────────────────

        [Command(requiresAuthority = false)]
        public void CmdRequestSkill(uint attackerNetId, int skillIndex, bool isPhysical)
        {
            if (_isDead || _deathProcessed) return;

            var attacker = FindPlayerByNetId(attackerNetId);
            if (attacker == null || attacker.Dead) return;

            var atkStats = attacker.ServerStats;
            if (atkStats == null) return;

            var skill = attacker.GetComponent<SkillSystem>()?.GetSkill(skillIndex);
            if (skill == null) { attacker.RpcSkillRejected(skillIndex, "Skill inválida."); return; }

            float distToTarget    = Vector3.Distance(attacker.transform.position, transform.position);
            float maxAllowedRange = skill.Range * ATTACK_RANGE_TOLERANCE;
            if (distToTarget > maxAllowedRange)
            {
                Debug.LogWarning($"[Security] {attacker.CharacterName} usou skill fora de range: " +
                                 $"dist={distToTarget:0.2f} max={maxAllowedRange:0.2f} range={skill.Range:0.1f}");
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

            attacker.ServerConsumeMP(skill.ManaCost);
            ServerTakeDamageFromPlayer(attacker, atkStats, skillIndex, isPhysical, skill);
            attacker.RpcSkillConfirmed(skillIndex, skill.Cooldown);
        }

        // ── CmdBasicAttack ─────────────────────────────────────────────────

        [Command(requiresAuthority = false)]
        public void CmdBasicAttack(uint attackerNetId)
        {
            if (_isDead || _deathProcessed) return;

            var attacker = FindPlayerByNetId(attackerNetId);
            if (attacker == null || attacker.Dead) return;

            var atkStats = attacker.ServerStats;
            if (atkStats == null) return;

            float distToTarget    = Vector3.Distance(attacker.transform.position, transform.position);
            float maxAllowedRange = attackRange * ATTACK_RANGE_TOLERANCE;
            if (distToTarget > maxAllowedRange)
            {
                Debug.LogWarning($"[Security] {attacker.CharacterName} atacou fora de range: " +
                                 $"dist={distToTarget:0.2f} max={maxAllowedRange:0.2f}");
                return;
            }

            float attackInterval = atkStats.ASPD > 0f ? (1f / atkStats.ASPD) : 1.2f;
            attackInterval = Mathf.Clamp(attackInterval, 0.25f, 3f);

            const int BASIC_ATTACK_CD_KEY = 99;
            if (!attacker.ServerCheckAndSetCooldown(BASIC_ATTACK_CD_KEY, attackInterval)) return;

            bool  hit  = StatsCalculator.RollHit(atkStats.HIT, _stats.FLEE);
            if (!hit) { RpcShowMiss(transform.position); return; }

            bool  crit = StatsCalculator.RollCrit(atkStats.CRIT);
            float dmg  = StatsCalculator.CalculatePhysicalDamage(
                atkStats.ATK, _stats.DEF, crit, atkStats.CritDMG);
            dmg = Mathf.Max(1f, dmg);

            if (!_damageLog.ContainsKey(attacker.netId)) _damageLog[attacker.netId] = 0f;
            _damageLog[attacker.netId] += dmg;

            RpcShowDamage(dmg, crit, transform.position);
            ApplyAggroReaction(attacker);
            ApplyDamageInternal(dmg);
        }

        [Server]
        private void ApplyAggroReaction(NetworkPlayer attacker)
        {
            switch (disposition)
            {
                case MonsterDisposition.Passive:
                    if (_state != AIState.Flee && _state != AIState.ReturnHome && _state != AIState.Dead)
                    {
                        _aggroTarget = attacker;
                        _fleeTimer   = 0f;
                        _state       = AIState.Flee;
                        if (_agent != null) _agent.speed = _stats.MoveSpeed * fleeSpeedMult;
                    }
                    break;

                case MonsterDisposition.Neutral:
                    _wasAttacked = true;
                    if (_state == AIState.Idle || _state == AIState.Patrol || _state == AIState.ReturnHome)
                    {
                        CancelPatrolWait();
                        _aggroTarget       = attacker;
                        _state             = AIState.Chase;
                        float ai           = (_stats.ASPD > 0f) ? (1f / _stats.ASPD) : 1f;
                        _attackAccumulator = ai * 0.3f;
                    }
                    break;

                case MonsterDisposition.Aggressive:
                    if (_state == AIState.Idle || _state == AIState.Patrol)
                    {
                        CancelPatrolWait();
                        _aggroTarget       = attacker;
                        _state             = AIState.Chase;
                        float ai           = (_stats.ASPD > 0f) ? (1f / _stats.ASPD) : 1f;
                        _attackAccumulator = ai * 0.3f;
                    }
                    break;
            }
        }

        [Server]
        private void ServerTakeDamageFromPlayer(
            NetworkPlayer attacker, DerivedStats atkStats,
            int skillIndex, bool isPhysical, SkillData skill)
        {
            bool  hit  = StatsCalculator.RollHit(atkStats.HIT, _stats.FLEE);
            if (!hit) { RpcShowMiss(transform.position); return; }

            bool  crit = StatsCalculator.RollCrit(atkStats.CRIT);
            float dmg  = isPhysical
                ? StatsCalculator.CalculatePhysicalDamage(atkStats.ATK * skill.AtkMultiplier, _stats.DEF,  crit, atkStats.CritDMG)
                : StatsCalculator.CalculateMagicDamage   (atkStats.MATK * skill.AtkMultiplier, _stats.MDEF, crit, atkStats.CritDMG);

            dmg = Mathf.Max(1f, dmg);

            if (!_damageLog.ContainsKey(attacker.netId)) _damageLog[attacker.netId] = 0f;
            _damageLog[attacker.netId] += dmg;

            RpcShowDamage(dmg, crit, transform.position);
            ApplyAggroReaction(attacker);
            ApplyDamageInternal(dmg);
        }

        // ── Morte / Respawn ────────────────────────────────────────────────

        [Server]
        private void ServerDie()
        {
            if (_isDead || _deathProcessed) return;
            _isDead         = true;
            _isMoving       = false;
            _deathProcessed = true;
            _state          = AIState.Dead;

            StopAllCoroutines();
            _aggroScanCoroutine = _pathUpdateCoroutine = _patrolWaitCoroutine = _regenCoroutine = null;

            if (_agent != null)
            {
                if (_agent.isOnNavMesh)
                {
                    _agent.ResetPath();
                    _agent.velocity = Vector3.zero;
                }
                _agent.enabled = false;
            }

            Debug.Log($"[NetworkMonster] {monsterDisplayName} morreu!");
            ServerDistributeExp();

            RPG.Managers.ItemDropManager.Instance?.ServerSpawnDrop(
                transform.position, dropChance,
                dropTable.Count > 0 ? dropTable : null,
                guaranteedDropIds.Count > 0 ? guaranteedDropIds : null);

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
                var  np = FindPlayerByNetId(kv.Key);
                if (np != null) np.ServerGrantExp(xp);
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
            if (isServer) StartCoroutine(DelayedRespawn());
        }

        [Server]
        private IEnumerator DelayedRespawn()
        {
            yield return null;
            _serverResetDone = false;
            ServerReset();
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
            result = center; return false;
        }

        // ── ClientRpcs ─────────────────────────────────────────────────────

        [ClientRpc] private void RpcShowDamage(float dmg, bool crit, Vector3 pos)
            => FloatingTextManager.Instance?.Show(
                crit ? $"CRÍTICO! {dmg:0}" : $"{dmg:0}",
                pos + Vector3.up, crit ? Color.yellow : Color.white);

        [ClientRpc] private void RpcShowMiss(Vector3 pos)
            => FloatingTextManager.Instance?.Show("MISS", pos + Vector3.up * 0.5f, Color.gray);

        [ClientRpc] private void RpcPlayAnim(string trigger)
            => _animator?.SetTrigger(trigger);

        [ClientRpc]
        private void RpcOnDied(Vector3 pos)
        {
            OnDeselected();

            var localPlayerGO = Mirror.NetworkClient.localPlayer;
            if (localPlayerGO != null)
            {
                var playerEntity = localPlayerGO.GetComponent<RPG.Character.PlayerEntity>();
                if (playerEntity != null && playerEntity.CurrentTarget is NetworkMonsterEntity current && current == this)
                {
                    UIManager.Instance?.ClearTargetPanel();
                    playerEntity.ClearTarget();
                }
            }

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

        private void OnCurrentHPChanged(float _, float v)
        {
            healthBarUI?.UpdateBar(v, _maxHP);
            var localPlayerGO = Mirror.NetworkClient.localPlayer;
            if (localPlayerGO != null)
            {
                var pe = localPlayerGO.GetComponent<RPG.Character.PlayerEntity>();
                if (pe != null && pe.CurrentTarget is NetworkMonsterEntity current && current == this)
                    UIManager.Instance?.RefreshTargetPanel(this);
            }
        }

        private void OnDeadChanged(bool _, bool dead)
        {
            if (dead && _agent != null) _agent.enabled = false;
        }

        private void OnIsMovingChanged(bool _, bool moving)
        {
            _animator?.SetBool("IsMoving", moving);
        }

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

            Gizmos.color = new Color(0f, 1f, 0.5f, 0.5f);
            Gizmos.DrawWireSphere(transform.position, attackRange * CHASE_DEST_FRACTION);

            Gizmos.color = Color.blue;
            Gizmos.DrawWireSphere(transform.position, attackRange * kiteDistanceFraction);

            Gizmos.color = new Color(1f, 1f, 1f, 0.4f);
            Gizmos.DrawWireSphere(transform.position, leashRange);
        }
#endif
    }
}
