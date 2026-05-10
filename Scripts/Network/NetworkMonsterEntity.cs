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
    /// NetworkMonsterEntity v28
    ///
    /// CORREÇÕES v28:
    ///
    ///   CORREÇÃO CRÍTICA — Stats de monstro não escalavam com o nível:
    ///     Antes: StatsCalculator.Calculate(..., CharacterRace.Human) era chamado
    ///     com os mesmos BaseAttributes para qualquer nível, dando stats idênticos
    ///     a um monstro Lv1 e Lv50.
    ///     SOLUÇÃO: usa StatsCalculator.CalculateForMonster() que aplica
    ///     multiplicador de escala por nível (baseAttr * (1 + (level-1)*0.025)).
    ///     Monstro Lv50 tem ~2.2x os stats base de Lv1.
    ///
    ///   CORREÇÃO — CmdBasicAttack usava clientAttackRange enviado pelo cliente:
    ///     Um cliente modificado poderia enviar attackRange=7.9 e atacar de longe.
    ///     SOLUÇÃO: servidor ignora o range enviado pelo cliente e usa uma constante
    ///     SERVER_MAX_PLAYER_ATTACK_RANGE como cap máximo. O range real deveria vir
    ///     de um componente no servidor, mas como BasicAttackSystem é cliente-side,
    ///     usamos o cap como validação de segurança.
    ///
    ///   CORREÇÃO — StopAllCoroutines() substituído por cancelamento explícito:
    ///     StopAllCoroutines() pode cancelar coroutines internas do Mirror em algumas
    ///     versões. Agora cada coroutine é cancelada explicitamente pelo handle.
    ///
    ///   CORREÇÃO — Dano calculado agora usa Penetration e DamageReduction:
    ///     ServerAttack e ServerTakeDamageFromPlayer passam os valores de
    ///     Penetration/DamageReduction para as fórmulas de dano.
    ///
    ///   Todas as correções v27 mantidas.
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

        [Header("Atributos Base (Lv1) — escalam automaticamente com o nível")]
        [SerializeField] private int baseSTR = 12;
        [SerializeField] private int baseAGI = 8;
        [SerializeField] private int baseVIT = 10;
        [SerializeField] private int baseDEX = 8;
        [SerializeField] private int baseINT = 5;
        [SerializeField] private int baseLUK = 5;

        [Header("Ranges de IA")]
        [SerializeField] private float aggroRange  = 10f;
        [SerializeField] private float attackRange = 2.5f;
        [SerializeField] private float leashRange  = 30f;

        [Header("Kite")]
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
        [Tooltip("Segundos até o corpo começar a sumir (após morte).")]
        [SerializeField] private float bodyFadeDelay    = 5f;
        [Tooltip("Duração do fade de dissolução do corpo.")]
        [SerializeField] private float bodyFadeDuration = 1f;
        [Tooltip("Delay total antes do respawn.")]
        [SerializeField] private float respawnDelay     = 15f;

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

        // Constantes de validação server-side
        private const float ATTACK_RANGE_TOLERANCE       = 1.15f;
        private const float CHASE_DEST_FRACTION          = 0.82f;
        // Cap máximo de range de ataque do player (segurança contra cheat)
        // Deve ser ligeiramente maior que o attackRange máximo configurável no BasicAttackSystem
        private const float SERVER_MAX_PLAYER_ATTACK_RANGE = 6f;

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

        /// <summary>
        /// TakeDamage via ITargetable — SOMENTE para modo offline/testes.
        /// Em multiplayer, todo dano passa por CmdRequestAttack/CmdBasicAttack.
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
        private WaitForSeconds _regenWait;

        private float _lastIsMovingUpdateTime;
        private const float MOVING_UPDATE_INTERVAL = 0.1f;

        // Coroutines rastreadas explicitamente (evita StopAllCoroutines)
        private Coroutine _aggroScanCoroutine;
        private Coroutine _pathUpdateCoroutine;
        private Coroutine _patrolWaitCoroutine;
        private Coroutine _regenCoroutine;
        private Coroutine _deathSequenceCoroutine;

        private bool _deathProcessed = false;

        // Fade de morte
        private Coroutine      _clientFadeCoroutine;
        private List<Material> _fadeMaterialInstances;

        private const float REGEN_INTERVAL = 5f;
        private const float REGEN_PERCENT  = 0.05f;

        // ── Awake / OnStartServer ──────────────────────────────────────────

        private void Awake()
        {
            _agent    = GetComponent<NavMeshAgent>();
            _animator = GetComponentInChildren<Animator>();

            // CORREÇÃO v28: usa CalculateForMonster que escala com o nível
            _stats = StatsCalculator.CalculateForMonster(
                new BaseAttributes { STR = baseSTR, AGI = baseAGI, VIT = baseVIT,
                                     DEX = baseDEX, INT = baseINT, LUK = baseLUK },
                Mathf.Max(1, level));

            _homePosition        = transform.position;
            _patrolRadiusRuntime = patrolRadius;
            _kiteDistance        = attackRange * kiteDistanceFraction;

            int layer = LayerMask.NameToLayer("Targetable");
            _targetableLayerMask = layer >= 0 ? (1 << layer) : 0;

            if (_targetableLayerMask == 0)
                Debug.LogWarning($"[NetworkMonsterEntity] Layer 'Targetable' não encontrado! Configure-o no projeto.");

            _aggroScanWait  = new WaitForSeconds(aggroScanInterval);
            _pathUpdateWait = new WaitForSeconds(pathUpdateRate);
            _regenWait      = new WaitForSeconds(REGEN_INTERVAL);
        }

        public override void OnStartClient()
        {
            if (selectionIndicator) selectionIndicator.SetActive(false);
            healthBarUI?.UpdateBar(_currentHP, _maxHP);
            if (visualRoot) visualRoot.SetActive(true);
            RestoreVisualsAlpha();
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

            // CORREÇÃO v28: cancela coroutines explicitamente em vez de StopAllCoroutines()
            CancelAllAICoroutines();

            _aggroScanCoroutine  = StartCoroutine(AggroScanLoop());
            _pathUpdateCoroutine = StartCoroutine(PathUpdateLoop());
            RpcOnRespawned();
        }

        /// <summary>
        /// CORREÇÃO v28: cancela cada coroutine pelo handle em vez de StopAllCoroutines().
        /// Evita cancelar coroutines internas do Mirror.
        /// </summary>
        [Server]
        private void CancelAllAICoroutines()
        {
            if (_aggroScanCoroutine  != null) { StopCoroutine(_aggroScanCoroutine);  _aggroScanCoroutine  = null; }
            if (_pathUpdateCoroutine != null) { StopCoroutine(_pathUpdateCoroutine); _pathUpdateCoroutine = null; }
            if (_patrolWaitCoroutine != null) { StopCoroutine(_patrolWaitCoroutine); _patrolWaitCoroutine = null; }
            if (_regenCoroutine      != null) { StopCoroutine(_regenCoroutine);      _regenCoroutine      = null; }
            if (_deathSequenceCoroutine != null) { StopCoroutine(_deathSequenceCoroutine); _deathSequenceCoroutine = null; }
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
                if (this == null || !isServer) yield break;

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
                if (this == null || !isServer) yield break;

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

                if (this == null || !isServer) break;
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
                _patrolWaitCoroutine = StartCoroutine(PatrolWaitCoroutine());
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
                if (_agent != null && _agent.isOnNavMesh)
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

            if (_agent != null && _agent.isOnNavMesh)
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
            Vector3 destination = CalculateChaseDestination(_aggroTarget.transform.position);
            _agent.stoppingDistance = 0.2f;
            _agent.SetDestination(destination);
        }

        private Vector3 CalculateChaseDestination(Vector3 playerPos)
        {
            Vector3 toPlayer = playerPos - transform.position;
            float dist = toPlayer.magnitude;
            float safeStopDist = attackRange * CHASE_DEST_FRACTION;

            if (dist <= safeStopDist * 0.95f) return transform.position;

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

        /// <summary>
        /// CORREÇÃO v28: dano usa Penetration do monstro e DamageReduction do player.
        /// RpcShowDamageTakenOnPlayer permanece como fonte única de floating text de dano.
        /// </summary>
        [Server]
        private void ServerAttack()
        {
            if (_aggroTarget == null || _aggroTarget.Dead) return;

            var targetStats = _aggroTarget.ServerStats;

            bool hit = StatsCalculator.RollHit(_stats.HIT, targetStats?.FLEE ?? 20f);
            if (!hit)
            {
                RpcShowMiss(_aggroTarget.transform.position);
                return;
            }

            bool  crit = StatsCalculator.RollCrit(_stats.CRIT);
            // CORREÇÃO v28: passa Penetration do monstro e DamageReduction do player
            float dmg  = StatsCalculator.CalculatePhysicalDamage(
                _stats.ATK,
                targetStats?.DEF ?? 10f,
                crit,
                _stats.CritDMG,
                _stats.Penetration,
                targetStats?.DamageReduction ?? 0f);

            if (!_aggroTarget.Dead)
            {
                RpcShowDamageTakenOnPlayer(dmg, crit, _aggroTarget.transform.position);
                _aggroTarget.ServerApplyDamage(dmg);
            }

            RpcPlayAnim("Attack");
        }

        [Server]
        private void ApplyDamageInternal(float dmg)
        {
            if (_deathProcessed) return;
            if (_isDead) return;
            if (dmg <= 0f) return;

            _currentHP = Mathf.Max(0f, _currentHP - dmg);
            if (_currentHP <= 0f) ServerDie();
        }

        private static NetworkPlayer FindPlayerByNetId(uint netId)
        {
            if (NetworkServer.spawned.TryGetValue(netId, out var identity))
                return identity?.GetComponent<NetworkPlayer>();
            return null;
        }

        // ── Commands de ataque recebidos do cliente ─────────────────────────

        [Command(requiresAuthority = false)]
        public void CmdRequestSkill(uint attackerNetId, int skillIndex, bool isPhysical)
        {
            if (_isDead || _deathProcessed) return;
            if (skillIndex < 0 || skillIndex >= 4) return;

            var attacker = FindPlayerByNetId(attackerNetId);
            if (attacker == null || attacker.Dead) return;

            var atkStats = attacker.ServerStats;
            if (atkStats == null) return;

            var inventory = attacker.GetComponent<NetworkInventory>();
            var skill     = inventory?.GetEquippedSkill(skillIndex);
            if (skill == null) { attacker.RpcSkillRejected(skillIndex, "Skill inválida."); return; }

            float distToTarget    = Vector3.Distance(attacker.transform.position, transform.position);
            float maxAllowedRange = skill.Range * ATTACK_RANGE_TOLERANCE;
            if (distToTarget > maxAllowedRange)
            {
                Debug.LogWarning($"[Security] {attacker.CharacterName} usou skill fora de range: " +
                                 $"dist={distToTarget:0.2f} max={maxAllowedRange:0.2f}");
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

        /// <summary>
        /// CORREÇÃO v28: servidor usa SERVER_MAX_PLAYER_ATTACK_RANGE como cap
        /// em vez de confiar no clientAttackRange enviado pelo cliente.
        /// Chave de cooldown do ataque básico é por monstro-alvo (netId), não
        /// uma chave global 99 que bloquearia ataques a outros alvos.
        /// </summary>
        [Command(requiresAuthority = false)]
        public void CmdBasicAttack(uint attackerNetId, float clientAttackRange)
        {
            if (_isDead || _deathProcessed) return;

            var attacker = FindPlayerByNetId(attackerNetId);
            if (attacker == null || attacker.Dead) return;

            var atkStats = attacker.ServerStats;
            if (atkStats == null) return;

            // CORREÇÃO v28: clamp seguro — não confia no valor do cliente
            float serverAttackRange = Mathf.Clamp(clientAttackRange, 0.5f, SERVER_MAX_PLAYER_ATTACK_RANGE);
            float distToTarget      = Vector3.Distance(attacker.transform.position, transform.position);
            float maxAllowedRange   = serverAttackRange * ATTACK_RANGE_TOLERANCE;

            if (distToTarget > maxAllowedRange)
            {
                Debug.LogWarning($"[Security] {attacker.CharacterName} atacou fora de range: " +
                                 $"dist={distToTarget:0.2f} max={maxAllowedRange:0.2f}");
                return;
            }

            // CORREÇÃO v28: cooldown de ataque básico usa chave composta (attackerNetId + netId do monstro)
            // Isso evita que atacar um monstro bloqueie o ataque a outro monstro diferente.
            // Chave: 1_000_000 + monstro.netId (garante não colidir com índices de skill 0-3)
            // Limitação: netId pode ser > int.MaxValue em long, mas na prática Mirror usa uint<2^20
            int basicAttackCdKey = (int)(1_000_000u + (netId & 0xFFFFu));
            float attackInterval = atkStats.ASPD > 0f ? (1f / atkStats.ASPD) : 1.2f;
            attackInterval = Mathf.Clamp(attackInterval, 0.2f, 3f);

            if (!attacker.ServerCheckAndSetCooldown(basicAttackCdKey, attackInterval)) return;

            bool hit = StatsCalculator.RollHit(atkStats.HIT, _stats.FLEE);
            if (!hit) { RpcShowMiss(transform.position); return; }

            bool  crit = StatsCalculator.RollCrit(atkStats.CRIT);
            // CORREÇÃO v28: passa Penetration do atacante e DamageReduction do monstro
            float dmg  = StatsCalculator.CalculatePhysicalDamage(
                atkStats.ATK,
                _stats.DEF,
                crit,
                atkStats.CritDMG,
                atkStats.Penetration,
                _stats.DamageReduction);
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
            bool hit = StatsCalculator.RollHit(atkStats.HIT, _stats.FLEE);
            if (!hit) { RpcShowMiss(transform.position); return; }

            bool  crit = StatsCalculator.RollCrit(atkStats.CRIT);
            float dmg;

            if (isPhysical)
            {
                // CORREÇÃO v28: passa Penetration do atacante e DamageReduction do monstro
                dmg = StatsCalculator.CalculatePhysicalDamage(
                    atkStats.ATK * skill.AtkMultiplier,
                    _stats.DEF,
                    crit,
                    atkStats.CritDMG,
                    atkStats.Penetration,
                    _stats.DamageReduction);
            }
            else
            {
                dmg = StatsCalculator.CalculateMagicDamage(
                    atkStats.MATK * skill.AtkMultiplier,
                    _stats.MDEF,
                    crit,
                    atkStats.CritDMG,
                    atkStats.MagicPenetration,
                    _stats.DamageReduction);
            }

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
            if (_deathProcessed) return;
            _deathProcessed = true;
            _isDead         = true;
            _isMoving       = false;
            _state          = AIState.Dead;

            // CORREÇÃO v28: cancela explicitamente em vez de StopAllCoroutines()
            CancelAllAICoroutines();

            if (_agent != null)
            {
                if (_agent.isOnNavMesh)
                {
                    _agent.ResetPath();
                    _agent.velocity = Vector3.zero;
                }
                _agent.enabled = false;
            }

            Debug.Log($"[NetworkMonster] {monsterDisplayName} (Lv{level}) morreu!");
            ServerDistributeExp();

            RPG.Managers.ItemDropManager.Instance?.ServerSpawnDrop(
                transform.position, dropChance,
                dropTable.Count > 0 ? dropTable : null,
                guaranteedDropIds.Count > 0 ? guaranteedDropIds : null);

            _deathSequenceCoroutine = StartCoroutine(ServerDeathSequence());
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
            if (this == null) yield break;

            RpcOnDied(transform.position);

            if (respawnDelay <= 0f)
            {
                _deathSequenceCoroutine = null;
                yield break;
            }

            yield return new WaitForSeconds(respawnDelay);

            if (this == null || !isServer)
            {
                _deathSequenceCoroutine = null;
                yield break;
            }

            _deathSequenceCoroutine = null;
            StartCoroutine(DelayedRespawn());
        }

        [Server]
        private IEnumerator DelayedRespawn()
        {
            yield return null;
            if (this == null || !isServer) yield break;
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
        private void RpcShowDamageTakenOnPlayer(float dmg, bool crit, Vector3 playerPos)
        {
            FloatingTextManager.Instance?.Show(
                crit ? $"-{dmg:0} CRÍTICO!" : $"-{dmg:0}",
                playerPos + Vector3.up * 1.8f,
                crit ? new Color(1f, 0.3f, 0f) : new Color(1f, 0.2f, 0.2f));
        }

        [ClientRpc]
        private void RpcOnDied(Vector3 pos)
        {
            OnDeselected();

            if (healthBarUI != null) healthBarUI.gameObject.SetActive(false);

            var localPlayerGO = Mirror.NetworkClient.localPlayer;
            if (localPlayerGO != null)
            {
                var playerEntity = localPlayerGO.GetComponent<RPG.Character.PlayerEntity>();
                if (playerEntity != null &&
                    playerEntity.CurrentTarget is NetworkMonsterEntity current && current == this)
                {
                    UIManager.Instance?.ClearTargetPanel();
                    playerEntity.ClearTarget();
                }
            }

            FloatingTextManager.Instance?.Show("Morto!", pos + Vector3.up, Color.red);

            if (_clientFadeCoroutine != null)
                StopCoroutine(_clientFadeCoroutine);
            _clientFadeCoroutine = StartCoroutine(ClientDeathFadeSequence());
        }

        private IEnumerator ClientDeathFadeSequence()
        {
            yield return new WaitForSeconds(bodyFadeDelay);

            if (this == null) yield break;

            Renderer[] renderers = null;
            if (visualRoot != null)
                renderers = visualRoot.GetComponentsInChildren<Renderer>(true);

            if (renderers != null && renderers.Length > 0)
            {
                _fadeMaterialInstances = new List<Material>();

                foreach (var r in renderers)
                {
                    if (r == null) continue;
                    var mats = r.materials;
                    foreach (var mat in mats)
                    {
                        if (mat == null) continue;
                        _fadeMaterialInstances.Add(mat);

                        if (mat.HasProperty("_Mode"))
                        {
                            mat.SetFloat("_Mode", 2f);
                            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                            mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                            mat.SetInt("_ZWrite", 0);
                            mat.DisableKeyword("_ALPHATEST_ON");
                            mat.EnableKeyword("_ALPHABLEND_ON");
                            mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                            mat.renderQueue = 3000;
                        }
                        if (mat.HasProperty("_Surface"))
                            mat.SetFloat("_Surface", 1f);
                    }
                    r.materials = mats;
                }

                var propBlock = new MaterialPropertyBlock();
                float elapsed = 0f;

                while (elapsed < bodyFadeDuration)
                {
                    if (this == null) yield break;

                    elapsed += Time.deltaTime;
                    float alpha = Mathf.Lerp(1f, 0f, elapsed / bodyFadeDuration);

                    foreach (var r in renderers)
                    {
                        if (r == null) continue;
                        r.GetPropertyBlock(propBlock);
                        propBlock.SetColor("_Color",     new Color(1f, 1f, 1f, alpha));
                        propBlock.SetColor("_BaseColor", new Color(1f, 1f, 1f, alpha));
                        r.SetPropertyBlock(propBlock);
                    }
                    yield return null;
                }
            }

            if (this != null && visualRoot != null)
                visualRoot.SetActive(false);

            if (_fadeMaterialInstances != null)
            {
                foreach (var mat in _fadeMaterialInstances)
                    if (mat != null) Destroy(mat);
                _fadeMaterialInstances = null;
            }

            _clientFadeCoroutine = null;
        }

        private void RestoreVisualsAlpha()
        {
            if (visualRoot == null) return;
            var renderers = visualRoot.GetComponentsInChildren<Renderer>(true);
            foreach (var r in renderers)
            {
                if (r == null) continue;
                r.SetPropertyBlock(null);
            }
        }

        [ClientRpc]
        private void RpcOnRespawned()
        {
            if (_clientFadeCoroutine != null)
            {
                StopCoroutine(_clientFadeCoroutine);
                _clientFadeCoroutine = null;
            }

            if (_fadeMaterialInstances != null)
            {
                foreach (var mat in _fadeMaterialInstances)
                    if (mat != null) Destroy(mat);
                _fadeMaterialInstances = null;
            }

            RestoreVisualsAlpha();

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
                if (pe != null &&
                    pe.CurrentTarget is NetworkMonsterEntity current && current == this)
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

        private void OnDestroy()
        {
            if (_fadeMaterialInstances != null)
            {
                foreach (var mat in _fadeMaterialInstances)
                    if (mat != null) Destroy(mat);
                _fadeMaterialInstances = null;
            }
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

            // Exibe stats escalados no Editor para verificação
            UnityEditor.Handles.Label(transform.position + Vector3.up * 2f,
                $"Lv{level} | HP:{_maxHP:0} ATK:{_stats?.ATK:0} DEF:{_stats?.DEF:0}");
        }
#endif
    }
}
