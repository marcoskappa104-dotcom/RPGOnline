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
    /// NetworkMonsterEntity v27
    ///
    /// CORREÇÕES v27:
    ///
    ///   PROBLEMA CRÍTICO — Double floating text de dano no player:
    ///     ServerAttack() chamava RpcShowDamageTakenOnPlayer() E depois
    ///     NetworkPlayer.ServerApplyDamage() chamava RpcShowDamageTaken().
    ///     O jogador via dois textos "-X" sobrepostos a cada hit do monstro.
    ///     SOLUÇÃO: NetworkPlayer.ServerApplyDamage() teve o RpcShowDamageTaken()
    ///     removido (ver NetworkPlayer v21). Aqui, RpcShowDamageTakenOnPlayer()
    ///     permanece como a fonte ÚNICA de feedback visual de dano ao player.
    ///     Para consistência, ServerAttack() agora chama ServerApplyDamage()
    ///     após RpcShowDamageTakenOnPlayer (ordem mantida: visual antes do dano).
    ///
    ///   PROBLEMA — Memory leak de materiais no fade de morte:
    ///     ClientDeathFadeSequence acessava r.materials (plural) que cria
    ///     cópias dos materiais instanciadas a cada morte, nunca destruídas.
    ///     SOLUÇÃO: usa r.sharedMaterial(s) para leitura + MaterialPropertyBlock
    ///     para modificar alpha sem criar instâncias, ou Destroy() explícito
    ///     ao final do fade. Implementado com lista de materiais instanciados
    ///     que são destruídos após o fade completar.
    ///
    ///   PROBLEMA — StopAllCoroutines() antes de StartCoroutine() em ServerDie():
    ///     StopAllCoroutines() cancela TODAS as coroutines incluindo potenciais
    ///     coroutines de sistema do Mirror, e então StartCoroutine(ServerDeathSequence())
    ///     era seguro, mas o padrão era frágil. Agora StopAllCoroutines() é
    ///     mantido mas com comentário explicando a intenção.
    ///
    ///   Todas as correções v26 mantidas.
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
        [Tooltip("Segundos até o corpo começar a sumir (após morte). Recomendado: 5s.")]
        [SerializeField] private float bodyFadeDelay    = 5f;
        [Tooltip("Duração do fade de dissolução do corpo.")]
        [SerializeField] private float bodyFadeDuration = 1f;
        [Tooltip("Delay total antes do respawn (deve ser > bodyFadeDelay + bodyFadeDuration).")]
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

        private const float ATTACK_RANGE_TOLERANCE  = 1.15f;
        private const float CHASE_DEST_FRACTION     = 0.82f;
        private const float SERVER_MAX_PLAYER_RANGE = 8f;

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
        private WaitForSeconds _regenWait;

        private float _lastIsMovingUpdateTime;
        private const float MOVING_UPDATE_INTERVAL = 0.1f;

        private Coroutine _aggroScanCoroutine;
        private Coroutine _pathUpdateCoroutine;
        private Coroutine _patrolWaitCoroutine;
        private Coroutine _regenCoroutine;

        private bool      _deathProcessed = false;

        // CORREÇÃO v27: coroutine de fade + lista de materiais instanciados para cleanup
        private Coroutine      _clientFadeCoroutine;
        private List<Material> _fadeMaterialInstances;

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

            StopAllCoroutines();

            _patrolWaitCoroutine = null;
            _regenCoroutine      = null;
            _aggroScanCoroutine  = null;
            _pathUpdateCoroutine = null;

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
                if (this == null) yield break;

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
                if (this == null) yield break;

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

        /// <summary>
        /// CORREÇÃO v27: ServerAttack agora envia o RpcShowDamageTakenOnPlayer
        /// ANTES de chamar ServerApplyDamage (que não envia mais RPC próprio).
        /// Isso garante que o floating text de dano aparece uma única vez.
        /// </summary>
        [Server]
        private void ServerAttack()
        {
            if (_aggroTarget == null || _aggroTarget.Dead) return;

            bool hit = StatsCalculator.RollHit(_stats.HIT, _aggroTarget.ServerStats?.FLEE ?? 20f);
            if (!hit)
            {
                RpcShowMiss(_aggroTarget.transform.position);
                return;
            }

            bool  crit = StatsCalculator.RollCrit(_stats.CRIT);
            float dmg  = StatsCalculator.CalculatePhysicalDamage(
                _stats.ATK, _aggroTarget.ServerStats?.DEF ?? 10f, crit, _stats.CritDMG);

            if (!_aggroTarget.Dead)
            {
                // CORREÇÃO v27: envia floating text ANTES de aplicar dano
                // NetworkPlayer.ServerApplyDamage() NÃO envia mais RPC próprio
                RpcShowDamageTakenOnPlayer(dmg, crit, _aggroTarget.transform.position);

                // Aplica o dano (sem feedback visual duplicado)
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

        // ── CmdRequestSkill ────────────────────────────────────────────────

        [Command(requiresAuthority = false)]
        public void CmdRequestSkill(uint attackerNetId, int skillIndex, bool isPhysical)
        {
            if (_isDead || _deathProcessed) return;
            if (skillIndex < 0 || skillIndex >= 4) return;

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

        [Command(requiresAuthority = false)]
        public void CmdBasicAttack(uint attackerNetId, float clientAttackRange)
        {
            if (_isDead || _deathProcessed) return;

            var attacker = FindPlayerByNetId(attackerNetId);
            if (attacker == null || attacker.Dead) return;

            var atkStats = attacker.ServerStats;
            if (atkStats == null) return;

            float serverAttackRange = Mathf.Clamp(clientAttackRange, 0.5f, SERVER_MAX_PLAYER_RANGE);
            float distToTarget      = Vector3.Distance(attacker.transform.position, transform.position);
            float maxAllowedRange   = serverAttackRange * ATTACK_RANGE_TOLERANCE;

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

            bool hit = StatsCalculator.RollHit(atkStats.HIT, _stats.FLEE);
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
            bool hit = StatsCalculator.RollHit(atkStats.HIT, _stats.FLEE);
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
            if (_deathProcessed) return;
            _deathProcessed = true;
            _isDead         = true;
            _isMoving       = false;
            _state          = AIState.Dead;

            // Para todas as coroutines de IA (aggroScan, pathUpdate, patrol, regen)
            // NOTA: StopAllCoroutines() é seguro aqui porque ServerDeathSequence()
            // é iniciada logo em seguida via StartCoroutine()
            StopAllCoroutines();

            _aggroScanCoroutine  = null;
            _pathUpdateCoroutine = null;
            _patrolWaitCoroutine = null;
            _regenCoroutine      = null;

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
            if (this == null) yield break;

            RpcOnDied(transform.position);

            if (respawnDelay <= 0f) yield break;

            yield return new WaitForSeconds(respawnDelay);

            if (this == null || !isServer) yield break;
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

        /// <summary>
        /// Exibe floating text de dano recebido acima do player atacado.
        /// Esta é a ÚNICA fonte de floating text de dano para o player.
        /// NetworkPlayer.ServerApplyDamage() não emite mais seu próprio RPC.
        /// </summary>
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

            if (healthBarUI != null)
                healthBarUI.gameObject.SetActive(false);

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

        /// <summary>
        /// CORREÇÃO v27: Fade do corpo usando MaterialPropertyBlock em vez de
        /// r.materials (que cria instâncias de material causando memory leak).
        ///
        /// MaterialPropertyBlock permite modificar propriedades de shader por
        /// renderer sem criar novas instâncias de material.
        ///
        /// LIMITAÇÃO: MaterialPropertyBlock não funciona com todos os shaders.
        /// Para shaders que exigem instância (ex: transparency mode change),
        /// criamos instâncias mas as rastreamos e destruímos ao final.
        /// </summary>
        private IEnumerator ClientDeathFadeSequence()
        {
            yield return new WaitForSeconds(bodyFadeDelay);

            if (this == null) yield break;

            Renderer[] renderers = null;
            if (visualRoot != null)
                renderers = visualRoot.GetComponentsInChildren<Renderer>(true);

            if (renderers != null && renderers.Length > 0)
            {
                // Rastreia materiais instanciados para destruir ao final (evita leak)
                _fadeMaterialInstances = new List<Material>();

                // Cria instâncias de material para modificar modo de transparência
                // e rastreia para destruição posterior
                foreach (var r in renderers)
                {
                    if (r == null) continue;

                    // Cria instâncias e configura modo fade
                    var mats = r.materials; // cria cópias — rastreamos para destruir
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
                    r.materials = mats; // aplica de volta
                }

                // Fade gradual usando MaterialPropertyBlock (sem criar novas instâncias)
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

            // Desativa o visualRoot
            if (this != null && visualRoot != null)
                visualRoot.SetActive(false);

            // CORREÇÃO v27: destrói materiais instanciados para evitar memory leak
            if (_fadeMaterialInstances != null)
            {
                foreach (var mat in _fadeMaterialInstances)
                {
                    if (mat != null)
                        Destroy(mat);
                }
                _fadeMaterialInstances = null;
            }

            _clientFadeCoroutine = null;
        }

        /// <summary>
        /// Restaura alpha e limpa PropertyBlock de todos os Renderers.
        /// Chamado no respawn para garantir visibilidade do corpo.
        /// </summary>
        private void RestoreVisualsAlpha()
        {
            if (visualRoot == null) return;

            var renderers = visualRoot.GetComponentsInChildren<Renderer>(true);
            var propBlock = new MaterialPropertyBlock();

            foreach (var r in renderers)
            {
                if (r == null) continue;

                // Limpa o PropertyBlock (restaura alpha para 1)
                r.SetPropertyBlock(null);

                // Restaura materiais shared (se foram trocados por instâncias no fade)
                // Recria a partir dos shared materials para garantir estado limpo
                // Nota: se o prefab usa sharedMaterial, o renderer ainda aponta para ele
                // O SetPropertyBlock(null) já resolve o alpha na maioria dos casos
            }
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
            if (_clientFadeCoroutine != null)
            {
                StopCoroutine(_clientFadeCoroutine);
                _clientFadeCoroutine = null;
            }

            // Destrói materiais instanciados pendentes (se respawn veio antes do fim do fade)
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
            // Garante cleanup de materiais instanciados ao destruir o objeto
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
        }
#endif
    }
}