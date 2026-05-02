using UnityEngine;
using UnityEngine.AI;
using Mirror;
using RPG.Data;
using RPG.UI;
using RPG.Character;

namespace RPG.Network
{
    /// <summary>
    /// NetworkMonsterEntity v4 — monstro online definitivo.
    ///
    /// NOVIDADES:
    ///   - Respawn automático: após morrer, o monstro reaparece no mesmo spawn
    ///     point depois de um tempo configurável (respawnDelay)
    ///   - ServerAttack usa ServerApplyDamage [Server] (bug antigo corrigido)
    ///   - Path update throttle para NavMesh eficiente
    ///   - Aggro reativo: agride quem atacou, mesmo em Idle/Patrol
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    [RequireComponent(typeof(NetworkIdentity))]
    [RequireComponent(typeof(NetworkTransformReliable))]
    public class NetworkMonsterEntity : NetworkBehaviour, ITargetable
    {
        // ── Config ────────────────────────────────────────────────────────
        [Header("Identidade")]
        [SerializeField] private string monsterDisplayName = "Monstro";
        [SerializeField] private int    level              = 1;

        [Header("Atributos Base")]
        [SerializeField] private int baseSTR = 12;
        [SerializeField] private int baseAGI = 8;
        [SerializeField] private int baseVIT = 10;
        [SerializeField] private int baseDEX = 8;
        [SerializeField] private int baseINT = 5;
        [SerializeField] private int baseLUK = 5;

        [Header("Comportamento")]
        [SerializeField] private float aggroRange     = 10f;
        [SerializeField] private float attackRange    = 2.5f;
        [SerializeField] private float kiteDistance   = 1.8f;
        [SerializeField] private float attackCooldown = 2f;
        [SerializeField] private float pathUpdateRate = 0.2f;
        [SerializeField] private Transform[] patrolPoints;

        [Header("Respawn")]
        [Tooltip("Segundos até o monstro reaparecer após morrer. 0 = não respawna.")]
        [SerializeField] private float respawnDelay = 15f;

        [Header("Recompensa")]
        [SerializeField] private long expReward = 50;

        [Header("Visuals")]
        [SerializeField] private GameObject         selectionIndicator;
        [SerializeField] private MonsterHealthBarUI healthBarUI;
        [SerializeField] private GameObject         visualRoot; // modelo 3D — esconde na morte

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
        private enum State { Idle, Patrol, Chase, Combat, Dead }
        private State         _state = State.Idle;
        private NavMeshAgent  _agent;
        private Animator      _animator;
        private NetworkPlayer _aggroTarget;
        private float         _attackTimer;
        private float         _pathTimer;
        private int           _patrolIndex;
        private Vector3       _spawnPosition;  // posição original para respawn

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
            _spawnPosition = transform.position;  // grava posição original
            ServerReset();
        }

        public override void OnStartClient()
        {
            if (selectionIndicator) selectionIndicator.SetActive(false);
            healthBarUI?.UpdateBar(_currentHP, _maxHP);
        }

        // ── Respawn do servidor ───────────────────────────────────────────

        /// <summary>Reseta o monstro para o estado inicial (usado no spawn e respawn).</summary>
        [Server]
        private void ServerReset()
        {
            _maxHP     = _stats.MaxHP;
            _currentHP = _maxHP;
            _isDead    = false;
            _state     = State.Idle;
            _aggroTarget = null;
            _attackTimer = 0f;
            _pathTimer   = 0f;

            if (_agent != null)
            {
                _agent.enabled = true;
                _agent.Warp(_spawnPosition);
            }

            RpcOnRespawned();
            Debug.Log($"[NetworkMonster] {monsterDisplayName} (re)spawnado | HP:{_maxHP:0}");
        }

        // ── Update (IA — server only) ─────────────────────────────────────

        private void Update()
        {
            if (!isServer || _isDead) return;

            _attackTimer += Time.deltaTime;
            _pathTimer   += Time.deltaTime;

            switch (_state)
            {
                case State.Idle:   ServerIdle();   break;
                case State.Patrol: ServerPatrol(); break;
                case State.Chase:  ServerChase();  break;
                case State.Combat: ServerCombat(); break;
            }
        }

        // ── Estados ───────────────────────────────────────────────────────

        private void ServerIdle()
        {
            if (TryAggro()) return;
            if (patrolPoints?.Length > 0) _state = State.Patrol;
        }

        private void ServerPatrol()
        {
            if (TryAggro()) return;
            if (!_agent.isOnNavMesh) return;

            if (_pathTimer >= pathUpdateRate)
            {
                _pathTimer = 0f;
                if (!_agent.pathPending && _agent.remainingDistance < 0.5f)
                {
                    _patrolIndex = (_patrolIndex + 1) % patrolPoints.Length;
                    _agent.SetDestination(patrolPoints[_patrolIndex].position);
                }
            }
        }

        private void ServerChase()
        {
            if (_aggroTarget == null || _aggroTarget.Dead) { ResetAggro(); return; }

            float dist = Vector3.Distance(transform.position, _aggroTarget.transform.position);
            if (dist > aggroRange * 2f) { ResetAggro(); return; }

            if (dist <= attackRange) { _state = State.Combat; _agent.ResetPath(); return; }

            if (_pathTimer >= pathUpdateRate && _agent.isOnNavMesh)
            {
                _pathTimer = 0f;
                _agent.stoppingDistance = attackRange * 0.85f;
                _agent.SetDestination(_aggroTarget.transform.position);
            }
        }

        private void ServerCombat()
        {
            if (_aggroTarget == null || _aggroTarget.Dead) { ResetAggro(); return; }

            float dist = Vector3.Distance(transform.position, _aggroTarget.transform.position);
            if (dist > attackRange * 1.4f) { _state = State.Chase; return; }

            if (_agent.isOnNavMesh)
            {
                if (dist < kiteDistance)
                {
                    Vector3 away = (transform.position - _aggroTarget.transform.position).normalized;
                    _agent.SetDestination(transform.position + away * (kiteDistance + 0.5f));
                }
                else _agent.ResetPath();
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

        // ── Aggro ─────────────────────────────────────────────────────────

        private bool TryAggro()
        {
            float         closest = aggroRange;
            NetworkPlayer found   = null;

            foreach (var np in FindObjectsOfType<NetworkPlayer>())
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
                Debug.Log($"[NetworkMonster] {monsterDisplayName} agrou {found.CharacterName}");
                return true;
            }
            return false;
        }

        private void ResetAggro()
        {
            _aggroTarget = null;
            if (_agent.isOnNavMesh) { _agent.ResetPath(); _agent.stoppingDistance = 0.3f; }
            _state       = patrolPoints?.Length > 0 ? State.Patrol : State.Idle;
            _attackTimer = 0f;
        }

        // ── Ataque ────────────────────────────────────────────────────────

        /// <summary>
        /// ServerApplyDamage é [Server] — pode ser chamado diretamente pelo servidor.
        /// NUNCA use CmdTakeDamage aqui (Commands só funcionam de cliente→servidor).
        /// </summary>
        [Server]
        private void ServerAttack()
        {
            if (_aggroTarget == null || _aggroTarget.Dead) return;

            bool hit = StatsCalculator.RollHit(_stats.HIT, 20f);
            if (!hit) { RpcShowMiss(_aggroTarget.transform.position); return; }

            bool  crit = StatsCalculator.RollCrit(_stats.CRIT);
            float dmg  = StatsCalculator.CalculatePhysicalDamage(_stats.ATK, 10f, crit, _stats.CritDMG);

            Debug.Log($"[NetworkMonster] {monsterDisplayName} → {_aggroTarget.CharacterName} | Dmg:{dmg:0} Crit:{crit}");

            _aggroTarget.ServerApplyDamage(dmg);
            RpcPlayAnim("Attack");
        }

        // ── TakeDamage (ITargetable — chamado pelo SkillSystem) ───────────

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

            Debug.Log($"[NetworkMonster] {monsterDisplayName} tomou {dmg:0} | HP:{_currentHP:0}/{_maxHP:0}");

            RpcShowDamage(dmg, crit, transform.position);

            // Aggro reativo
            if (_state == State.Idle || _state == State.Patrol) TryAggro();

            if (_currentHP <= 0f) ServerDie();
        }

        // ── Morte e Respawn ───────────────────────────────────────────────

        [Server]
        private void ServerDie()
        {
            _isDead = true;
            _state  = State.Dead;

            if (_agent.isOnNavMesh) { _agent.ResetPath(); _agent.enabled = false; }

            Debug.Log($"[NetworkMonster] {monsterDisplayName} morreu!");

            // Concede XP a jogadores próximos
            foreach (var np in FindObjectsOfType<NetworkPlayer>())
            {
                float dist = Vector3.Distance(transform.position, np.transform.position);
                if (dist <= aggroRange * 2f)
                    RpcGrantExp(np.netId, expReward);
            }

            RpcOnDied(transform.position);

            // Agenda respawn (se configurado)
            if (respawnDelay > 0f)
                Invoke(nameof(ServerReset), respawnDelay);
        }

        // ── ClientRpcs ────────────────────────────────────────────────────

        [ClientRpc]
        private void RpcShowDamage(float dmg, bool crit, Vector3 pos)
        {
            Color c = crit ? Color.yellow : Color.white;
            FloatingTextManager.Instance?.Show(crit ? $"CRÍTICO! {dmg:0}" : $"{dmg:0}", pos + Vector3.up, c);
        }

        [ClientRpc]
        private void RpcShowMiss(Vector3 pos)
            => FloatingTextManager.Instance?.Show("MISS", pos, Color.gray);

        [ClientRpc]
        private void RpcPlayAnim(string trigger) => _animator?.SetTrigger(trigger);

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

            var playerEntity = NetworkClient.localPlayer.GetComponent<PlayerEntity>();
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
                }

                var netPlayer = NetworkClient.localPlayer.GetComponent<NetworkPlayer>();
                netPlayer?.CmdSyncHP(playerEntity.CurrentHP, playerEntity.Stats.MaxHP);

                var account = RPG.Managers.GameManager.Instance?.CurrentAccount;
                if (account != null)
                    RPG.Managers.SaveManager.Instance?.SaveCharacter(account, charData);
            }
        }

        [ClientRpc]
        private void RpcOnDied(Vector3 pos)
        {
            OnDeselected();
            if (visualRoot != null) visualRoot.SetActive(false);
            UIManager.Instance?.ClearTargetPanel();
            FloatingTextManager.Instance?.Show("Morto!", pos + Vector3.up, Color.red);
        }

        [ClientRpc]
        private void RpcOnRespawned()
        {
            if (visualRoot != null) visualRoot.SetActive(true);
            if (selectionIndicator) selectionIndicator.SetActive(false);
            healthBarUI?.UpdateBar(_currentHP, _maxHP);
        }

        // ── SyncVar Hooks ─────────────────────────────────────────────────

        private void OnCurrentHPChanged(float _, float newVal)
            => healthBarUI?.UpdateBar(newVal, _maxHP);

        private void OnDeadChanged(bool _, bool nowDead)
        {
            if (nowDead && _agent != null) _agent.enabled = false;
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.yellow; Gizmos.DrawWireSphere(transform.position, aggroRange);
            Gizmos.color = Color.red;    Gizmos.DrawWireSphere(transform.position, attackRange);
            Gizmos.color = Color.blue;   Gizmos.DrawWireSphere(transform.position, kiteDistance);
        }
    }
}