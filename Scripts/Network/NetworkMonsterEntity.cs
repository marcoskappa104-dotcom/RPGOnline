using UnityEngine;
using UnityEngine.AI;
using Mirror;
using RPG.Data;
using RPG.UI;
using RPG.Character;
using System.Collections;

namespace RPG.Network
{
    // ── Disposição — define como o mob reage ao player ────────────────────
    /// <summary>
    /// Passive  → Nunca ataca. Foge quando sofre dano.
    ///            Exemplos: animais, aldeões, criaturas inofensivas.
    ///
    /// Neutral  → Ignora o player. Mas se for atacado, reage e persegue.
    ///            Exemplos: ursos, lobos descansando, mobs de recurso.
    ///
    /// Aggressive → Ataca qualquer player que entre no aggroRange.
    ///              Comportamento padrão (igual ao sistema anterior).
    /// </summary>
    public enum MonsterDisposition { Passive, Neutral, Aggressive }

    /// <summary>
    /// NetworkMonsterEntity v7
    ///
    /// NOVIDADES em relação à v6:
    ///
    ///   1. DISPOSIÇÃO (Passive / Neutral / Aggressive)
    ///      - Passive: foge ao ser atacado, nunca inicia combate.
    ///      - Neutral: ignora o player mas contra-ataca se for agredido.
    ///      - Aggressive: persegue e ataca (comportamento original).
    ///
    ///   2. PATROL POR ÁREA
    ///      - Ao invés de waypoints fixos, o mob escolhe pontos aleatórios
    ///        dentro de _patrolRadius ao redor de _homePosition.
    ///      - patrolRadius é configurado pelo NetworkMonsterSpawner.
    ///      - Waypoints fixos (patrolPoints) continuam funcionando se
    ///        usePatrolPoints = true.
    ///
    ///   3. LEASH (coleira)
    ///      - Se o mob perseguir o player além de leashRange, ele abandona
    ///        o combate e retorna para home (estado ReturnHome).
    ///      - Ao retornar, recupera HP gradualmente.
    ///
    ///   4. ESTADO FLEE (fuga — só Passive)
    ///      - Mob foge na direção oposta ao atacante por fleeDuration segundos.
    ///      - Depois retorna para home.
    ///
    ///   5. ESTADO RETURN HOME
    ///      - Mob retorna à homePosition após leash break ou após fugir.
    ///      - Regenera HP enquanto volta.
    ///
    ///   6. SetSpawnData(Vector3 home, float patrolRadius)
    ///      - Chamado pelo NetworkMonsterSpawner logo após o spawn.
    ///      - Define homePosition e patrolRadius dinamicamente.
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
        [Tooltip("Passive = foge ao ser atacado.\n" +
                 "Neutral = ignora player, contra-ataca se agredido.\n" +
                 "Aggressive = ataca qualquer player no alcance.")]
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
        [Tooltip("Aggressive: raio de detecção do player.\n" +
                 "Neutral: raio de ameaça (menor que o aggro normal).")]
        [SerializeField] private float aggroRange     = 10f;
        [SerializeField] private float attackRange    = 2.5f;
        [Tooltip("Distância mínima que o mob mantém do player durante o combate.")]
        [SerializeField] private float kiteDistance   = 1.8f;
        [SerializeField] private float attackCooldown = 2f;
        [Tooltip("Raio máximo de perseguição. Se o player fugir além disso, o mob volta para casa.")]
        [SerializeField] private float leashRange     = 30f;
        [Tooltip("Velocidade de atualização do caminho (segundos entre recalculos).")]
        [SerializeField] private float pathUpdateRate = 0.2f;

        // ── Config — Patrulha ─────────────────────────────────────────────
        [Header("Patrulha")]
        [Tooltip("TRUE = usa os waypoints do array abaixo.\n" +
                 "FALSE = patrulha aleatória dentro de patrolRadius (configurado pelo Spawner).")]
        [SerializeField] private bool        usePatrolPoints = false;
        [SerializeField] private Transform[] patrolPoints;

        [Tooltip("Tempo de espera (segundos) em cada destino de patrulha.")]
        [SerializeField] private float patrolWaitTime   = 2f;

        [Tooltip("Raio de patrulha aleatória. Sobrescrito pelo NetworkMonsterSpawner.SetSpawnData().")]
        [SerializeField] private float patrolRadius     = 12f;

        // ── Config — Fuga (Passive) ───────────────────────────────────────
        [Header("Fuga (apenas Passive)")]
        [Tooltip("Segundos fugindo antes de voltar para casa.")]
        [SerializeField] private float fleeDuration = 6f;
        [Tooltip("Multiplicador de velocidade durante a fuga.")]
        [SerializeField] private float fleeSpeedMult = 1.5f;

        // ── Config — Morte e Respawn ──────────────────────────────────────
        [Header("Morte e Respawn")]
        [Tooltip("Segundos que o corpo fica visível após morrer.")]
        [SerializeField] private float hideDelay    = 3f;
        [Tooltip("Segundos até renascer. 0 = sem respawn.")]
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
        private bool    _wasAttacked;          // para Neutral: torna agressivo se atacado
        private Vector3 _currentPatrolTarget;  // destino atual de patrulha aleatória
        private bool    _patrolTargetSet;

        // Spawn data (configurado pelo NetworkMonsterSpawner)
        private Vector3 _homePosition;
        private float   _patrolRadiusRuntime;  // sobrescreve patrolRadius do Inspector

        private const float REGEN_INTERVAL = 3f;
        private const float REGEN_PERCENT  = 0.05f; // 5% do MaxHP por tick ao voltar para casa

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

        // ── Spawn Data — chamado pelo NetworkMonsterSpawner ───────────────

        /// <summary>
        /// Configura home e raio de patrulha após o spawn via spawner.
        /// Chamado apenas no servidor.
        /// </summary>
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

            transform.position = _homePosition;

            if (_agent != null)
            {
                _agent.enabled = true;
                _agent.speed   = _stats.ASPD;
                if (_agent.isOnNavMesh) _agent.Warp(_homePosition);
            }

            RpcOnRespawned();
            Debug.Log($"[NetworkMonster] {monsterDisplayName} (re)spawnado | " +
                      $"HP:{_maxHP:0} | Disposição:{disposition} | " +
                      $"Home:{_homePosition} | PatrolR:{_patrolRadiusRuntime}");
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

        // ── Estados de IA ─────────────────────────────────────────────────

        private void ServerIdle()
        {
            if (TryAggro()) return;

            // Transition to patrol if we have a patrol area or points
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

        // Patrulha por waypoints fixos (comportamento original)
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

        // Patrulha aleatória dentro da área (novo comportamento)
        private void PatrolInArea()
        {
            if (!_agent.isOnNavMesh) return;

            // Precisa de um novo destino?
            bool arrived = !_agent.pathPending && _agent.remainingDistance < 0.6f;

            if (_waitingAtPatrolPoint)
            {
                _patrolWaitTimer += Time.deltaTime;
                if (_patrolWaitTimer >= patrolWaitTime)
                {
                    _waitingAtPatrolPoint = false;
                    _patrolTargetSet      = false; // força escolher novo ponto
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

                if (TryGetRandomAreaPoint(_homePosition, _patrolRadiusRuntime,
                                          out Vector3 dest))
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

            // Leash — muito longe de casa?
            float distFromHome = Vector3.Distance(transform.position, _homePosition);
            if (distFromHome > leashRange)
            {
                ResetAggro();
                EnterReturnHome();
                return;
            }

            float distToTarget = Vector3.Distance(transform.position,
                                                   _aggroTarget.transform.position);
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

            // Leash
            float distFromHome = Vector3.Distance(transform.position, _homePosition);
            if (distFromHome > leashRange) { ResetAggro(); EnterReturnHome(); return; }

            float dist = Vector3.Distance(transform.position, _aggroTarget.transform.position);

            if (dist > attackRange * 1.4f) { _state = State.Chase; return; }

            // Kite — não gruda demais
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

            // Vira para o alvo
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
                _agent.speed = _stats.ASPD; // restaura velocidade normal
                EnterReturnHome();
                return;
            }

            // Atualiza destino de fuga periodicamente (para longe do atacante)
            if (_pathTimer >= pathUpdateRate * 2f && _aggroTarget != null)
            {
                _pathTimer = 0f;
                Vector3 fleeDir  = (transform.position - _aggroTarget.transform.position).normalized;
                Vector3 fleePos  = transform.position + fleeDir * (aggroRange * 1.5f);

                if (NavMesh.SamplePosition(fleePos, out NavMeshHit hit, 5f, NavMesh.AllAreas))
                    _agent.SetDestination(hit.position);
            }
        }

        private void ServerReturnHome()
        {
            // Regenera HP ao voltar para casa
            _regenTimer += Time.deltaTime;
            if (_regenTimer >= REGEN_INTERVAL)
            {
                _regenTimer = 0f;
                float regen = _maxHP * REGEN_PERCENT;
                _currentHP  = Mathf.Min(_maxHP, _currentHP + regen);
            }

            if (!_agent.isOnNavMesh) return;

            if (_pathTimer >= pathUpdateRate)
            {
                _pathTimer = 0f;
                _agent.stoppingDistance = 0.5f;
                _agent.SetDestination(_homePosition);
            }

            float distToHome = Vector3.Distance(transform.position, _homePosition);
            if (distToHome < 1.5f)
            {
                _agent.ResetPath();
                _wasAttacked     = false;
                _patrolTargetSet = false;
                _state           = State.Idle;
            }
        }

        private void EnterReturnHome()
        {
            _state              = State.ReturnHome;
            _aggroTarget        = null;
            _regenTimer         = 0f;
            _agent.stoppingDistance = 0.5f;
        }

        // ── Aggro ─────────────────────────────────────────────────────────

        /// <summary>
        /// Tenta agredir um player próximo de acordo com a disposição:
        ///   - Passive:    nunca agride.
        ///   - Neutral:    agride SOMENTE se foi atacado (_wasAttacked).
        ///   - Aggressive: agride qualquer player dentro de aggroRange.
        /// </summary>
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

            // Só volta a patrulhar se estiver perto de casa
            float distToHome = Vector3.Distance(transform.position, _homePosition);
            if (distToHome > leashRange * 0.5f)
                EnterReturnHome();
            else
                _state = State.Idle;
        }

        // ── Ataque ────────────────────────────────────────────────────────

        [Server]
        private void ServerAttack()
        {
            if (_aggroTarget == null || _aggroTarget.Dead) return;

            bool hit = StatsCalculator.RollHit(_stats.HIT, 20f);
            if (!hit) { RpcShowMiss(_aggroTarget.transform.position); return; }

            bool  crit = StatsCalculator.RollCrit(_stats.CRIT);
            float dmg  = StatsCalculator.CalculatePhysicalDamage(
                             _stats.ATK, 10f, crit, _stats.CritDMG);

            _aggroTarget.ServerApplyDamage(dmg);
            RpcPlayAnim("Attack");
        }

        // ── TakeDamage ────────────────────────────────────────────────────

        public void TakeDamage(float rawAtk, float rawMatk, bool isPhysical)
        {
            if (isServer) { ServerTakeDamage(rawAtk, rawMatk, isPhysical); return; }
            CmdRequestTakeDamage(rawAtk, rawMatk, isPhysical);
        }

        [Command(requiresAuthority = false)]
        private void CmdRequestTakeDamage(float rawAtk, float rawMatk, bool isPhysical)
            => ServerTakeDamage(rawAtk, rawMatk, isPhysical);

        [Server]
        private void ServerTakeDamage(float rawAtk, float rawMatk, bool isPhysical)
        {
            if (_isDead) return;

            bool  crit = StatsCalculator.RollCrit(5f);
            float dmg  = isPhysical
                ? StatsCalculator.CalculatePhysicalDamage(rawAtk, _stats.DEF, crit, _stats.CritDMG)
                : StatsCalculator.CalculateMagicDamage(rawMatk, _stats.MDEF, crit, _stats.CritDMG);

            dmg        = Mathf.Max(1f, dmg);
            _currentHP = Mathf.Max(0f, _currentHP - dmg);

            RpcShowDamage(dmg, crit, transform.position);

            // Reage ao dano de acordo com a disposição
            switch (disposition)
            {
                case MonsterDisposition.Passive:
                    // Foge — encontra o agressor mais próximo para fugir
                    if (_state != State.Flee && _state != State.ReturnHome && _state != State.Dead)
                    {
                        FindClosestAttacker();
                        _fleeTimer     = 0f;
                        _state         = State.Flee;
                        _agent.speed   = _stats.ASPD * fleeSpeedMult;
                        _pathTimer     = pathUpdateRate; // força update imediato
                    }
                    break;

                case MonsterDisposition.Neutral:
                    // Marca como atacado e entra em combate se ainda não estava
                    _wasAttacked = true;
                    if (_state == State.Idle || _state == State.Patrol || _state == State.ReturnHome)
                        TryAggro();
                    break;

                case MonsterDisposition.Aggressive:
                    // Já está em combate normalmente; se estava patrulhando, agride
                    if (_state == State.Idle || _state == State.Patrol)
                        TryAggro();
                    break;
            }

            if (_currentHP <= 0f) ServerDie();
        }

        /// <summary>Acha o NetworkPlayer mais próximo como referência para fugir.</summary>
        private void FindClosestAttacker()
        {
            float         closest = float.MaxValue;
            NetworkPlayer found   = null;

            foreach (var np in NetworkPlayer.All)
            {
                if (np.Dead) continue;
                float d = Vector3.Distance(transform.position, np.transform.position);
                if (d < closest) { closest = d; found = np; }
            }

            _aggroTarget = found;
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

            foreach (var np in NetworkPlayer.All)
            {
                float dist = Vector3.Distance(transform.position, np.transform.position);
                if (dist <= aggroRange * 2f)
                    RpcGrantExp(np.netId, expReward);
            }

            StartCoroutine(ServerDeathSequence());
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

        /// <summary>
        /// Escolhe um ponto aleatório no NavMesh dentro do raio especificado
        /// ao redor de center. Faz até 15 tentativas.
        /// </summary>
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

        [ClientRpc]
        private void RpcGrantExp(uint targetNetId, long amount)
        {
            if (NetworkClient.localPlayer == null) return;
            if (NetworkClient.localPlayer.netId != targetNetId) return;

            var charData = RPG.Managers.GameManager.Instance?.SelectedCharacter;
            if (charData == null) return;

            bool leveled = charData.AddExperience(amount);

            FloatingTextManager.Instance?.Show(
                $"+{amount} XP",
                NetworkClient.localPlayer.transform.position + Vector3.up * 2f,
                Color.cyan);

            var playerEntity = NetworkClient.localPlayer.GetComponent<RPG.Character.PlayerEntity>();
            var netPlayer    = NetworkClient.localPlayer.GetComponent<NetworkPlayer>();

            if (playerEntity != null)
            {
                playerEntity.RefreshStats();

                if (leveled)
                {
                    playerEntity.HealToFull();
                    FloatingTextManager.Instance?.Show(
                        "LEVEL UP!",
                        NetworkClient.localPlayer.transform.position + Vector3.up * 2.5f,
                        Color.yellow);
                    netPlayer?.CmdSyncLevel(charData.Level);
                }

                netPlayer?.CmdSyncHP(playerEntity.CurrentHP, playerEntity.Stats.MaxHP);

                var account = RPG.Managers.GameManager.Instance?.CurrentAccount;
                if (account != null)
                    RPG.Managers.SaveManager.Instance?.SaveCharacter(account, charData);
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
            // Aggro range — vermelho/amarelo dependendo da disposição
            Color aggroColor = disposition switch
            {
                MonsterDisposition.Passive    => Color.green,
                MonsterDisposition.Neutral    => Color.yellow,
                MonsterDisposition.Aggressive => Color.red,
                _                             => Color.red
            };

            Gizmos.color = aggroColor;
            Gizmos.DrawWireSphere(transform.position, aggroRange);

            // Attack range — vermelho
            Gizmos.color = new Color(1f, 0.3f, 0.3f);
            Gizmos.DrawWireSphere(transform.position, attackRange);

            // Kite distance — azul
            Gizmos.color = Color.blue;
            Gizmos.DrawWireSphere(transform.position, kiteDistance);

            // Leash range — branco
            Gizmos.color = new Color(1f, 1f, 1f, 0.4f);
            Gizmos.DrawWireSphere(transform.position, leashRange);

            // Home + patrol radius (runtime)
            if (Application.isPlaying)
            {
                Gizmos.color = new Color(1f, 0.8f, 0f, 0.5f);
                Gizmos.DrawWireSphere(_homePosition, _patrolRadiusRuntime);
                Gizmos.color = Color.cyan;
                Gizmos.DrawSphere(_homePosition, 0.3f);

                // Destino atual de patrulha
                if (_patrolTargetSet)
                {
                    Gizmos.color = Color.magenta;
                    Gizmos.DrawSphere(_currentPatrolTarget, 0.25f);
                    Gizmos.DrawLine(transform.position, _currentPatrolTarget);
                }
            }
            else
            {
                // No editor (antes de jogar), mostra patrol radius do Inspector
                Gizmos.color = new Color(1f, 0.8f, 0f, 0.3f);
                Gizmos.DrawWireSphere(transform.position, patrolRadius);
            }
        }
    }
}