using UnityEngine;
using UnityEngine.AI;
using RPG.Data;
using RPG.UI;
using RPG.Managers;

namespace RPG.Character
{
    public enum MonsterState { Idle, Patrol, Chase, Combat, Dead }

    [RequireComponent(typeof(NavMeshAgent))]
    public class MonsterEntity : TargetableEntity
    {
        [Header("Identidade")]
        [SerializeField] private string monsterDisplayName = "Monstro";
        [SerializeField] private int    level              = 1;

        // ── Atributos base — mesmas fórmulas do StatsCalculator ───────────
        [Header("Atributos Base (mesmas fórmulas do player)")]
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
        [SerializeField] private Transform[] patrolPoints;

        [Header("Recompensa")]
        [SerializeField] private long expReward = 50;

        // ── Stats derivados (calculados igual ao player) ──────────────────
        private BaseAttributes _baseAttrs;
        private DerivedStats   _stats;

        // ── Runtime ───────────────────────────────────────────────────────
        private float        _currentHP;
        private float        _maxHP;
        private NavMeshAgent _agent;
        private MonsterState _state       = MonsterState.Idle;
        private PlayerEntity _target;
        private float        _attackTimer;
        private int          _patrolIndex;

        public override float  CurrentHP   => _currentHP;
        public override float  MaxHP       => _maxHP;
        public override bool   IsDead      => _state == MonsterState.Dead;
        public override string DisplayName => monsterDisplayName;

        public event System.Action<MonsterEntity> OnDied;

        // ── Init ──────────────────────────────────────────────────────────
        protected override void Awake()
        {
            base.Awake();
            _agent = GetComponent<NavMeshAgent>();

            // Calcula stats usando as mesmas fórmulas do player
            _baseAttrs = new BaseAttributes
            {
                STR = baseSTR, AGI = baseAGI, VIT = baseVIT,
                DEX = baseDEX, INT = baseINT, LUK = baseLUK
            };
            _stats    = StatsCalculator.Calculate(_baseAttrs, level);
            _maxHP    = _stats.MaxHP;
            _currentHP = _maxHP;

            Debug.Log($"[MonsterEntity] {monsterDisplayName} iniciado | " +
                      $"HP:{_maxHP:0} ATK:{_stats.ATK:0} DEF:{_stats.DEF:0} " +
                      $"AggroRange:{aggroRange} AttackRange:{attackRange}");
        }

        private void Start()
        {
            // Valida NavMesh
            if (!_agent.isOnNavMesh)
                Debug.LogWarning($"[MonsterEntity] {monsterDisplayName} NÃO está no NavMesh! " +
                                 "Verifique se o NavMesh foi baked e se o mob está sobre o terreno.");
        }

        private void Update()
        {
            if (_state == MonsterState.Dead) return;

            switch (_state)
            {
                case MonsterState.Idle:   StateIdle();   break;
                case MonsterState.Patrol: StatePatrol(); break;
                case MonsterState.Chase:  StateChase();  break;
                case MonsterState.Combat: StateCombat(); break;
            }
        }

        // ── Estados ───────────────────────────────────────────────────────

        private void StateIdle()
        {
            if (TryAggro()) return;
            if (patrolPoints != null && patrolPoints.Length > 0)
                SetState(MonsterState.Patrol);
        }

        private void StatePatrol()
        {
            if (TryAggro()) return;
            if (!_agent.isOnNavMesh) return;

            if (!_agent.pathPending && _agent.remainingDistance < 0.5f)
            {
                _patrolIndex = (_patrolIndex + 1) % patrolPoints.Length;
                _agent.SetDestination(patrolPoints[_patrolIndex].position);
            }
        }

        private void StateChase()
        {
            if (_target == null || !_target.IsInitialized || _target.CurrentHP <= 0)
            {
                Debug.Log($"[MonsterEntity] {monsterDisplayName} perdeu o alvo — voltando.");
                ResetAggro(); return;
            }

            float dist = DistToTarget();

            if (dist > aggroRange * 2f)
            {
                Debug.Log($"[MonsterEntity] {monsterDisplayName} alvo muito longe ({dist:0.0}) — reset.");
                ResetAggro(); return;
            }

            if (dist <= attackRange)
            {
                Debug.Log($"[MonsterEntity] {monsterDisplayName} chegou no range ({dist:0.0}) — COMBAT.");
                SetState(MonsterState.Combat); return;
            }

            if (_agent.isOnNavMesh)
            {
                _agent.stoppingDistance = attackRange * 0.85f;
                _agent.SetDestination(_target.transform.position);
            }
        }

        private void StateCombat()
        {
            if (_target == null || !_target.IsInitialized || _target.CurrentHP <= 0)
            { ResetAggro(); return; }

            float dist = DistToTarget();

            if (dist > attackRange * 1.4f)
            { SetState(MonsterState.Chase); return; }

            // Kite — não gruda no player
            if (_agent.isOnNavMesh)
            {
                if (dist < kiteDistance)
                {
                    Vector3 away = (transform.position - _target.transform.position).normalized;
                    _agent.SetDestination(transform.position + away * (kiteDistance + 0.5f));
                }
                else
                {
                    _agent.ResetPath();
                }
            }

            FaceTarget();

            _attackTimer += Time.deltaTime;
            if (_attackTimer >= attackCooldown)
            {
                _attackTimer = 0f;
                PerformAttack();
            }
        }

        // ── Aggro ─────────────────────────────────────────────────────────

        private bool TryAggro()
        {
            // Procura todos os PlayerEntity na cena
            var players = FindObjectsOfType<PlayerEntity>();
            foreach (var p in players)
            {
                if (!p.IsInitialized || p.CurrentHP <= 0) continue;
                float dist = Vector3.Distance(transform.position, p.transform.position);
                if (dist <= aggroRange)
                {
                    Debug.Log($"[MonsterEntity] {monsterDisplayName} agrou {p.Data.CharacterName} (dist={dist:0.0})");
                    _target = p;
                    SetState(MonsterState.Chase);
                    return true;
                }
            }
            return false;
        }

        private void ResetAggro()
        {
            _target = null;
            if (_agent.isOnNavMesh)
            {
                _agent.stoppingDistance = 0.3f;
                _agent.ResetPath();
            }
            SetState(patrolPoints?.Length > 0 ? MonsterState.Patrol : MonsterState.Idle);
        }

        // ── Ataque ────────────────────────────────────────────────────────

        private void PerformAttack()
        {
            if (_target == null || !_target.IsInitialized) return;

            float targetFlee = _target.Stats?.FLEE ?? 20f;
            bool  hit        = StatsCalculator.RollHit(_stats.HIT, targetFlee);

            Debug.Log($"[MonsterEntity] {monsterDisplayName} ataca {_target.Data.CharacterName} | " +
                      $"HIT:{_stats.HIT:0} vs FLEE:{targetFlee:0} → {(hit ? "ACERTOU" : "ERROU")}");

            if (!hit)
            {
                FloatingTextManager.Instance?.Show("MISS", _target.transform.position, Color.gray);
                return;
            }

            bool  crit   = StatsCalculator.RollCrit(_stats.CRIT);
            float rawATK = _stats.ATK;

            Debug.Log($"[MonsterEntity] ATK:{rawATK:0} | Crit:{crit} | Target DEF:{_target.Stats?.DEF:0}");

            // Usa exatamente as mesmas fórmulas do player
            _target.TakeDamage(rawATK, 0f, true);
        }

        // ── TakeDamage (recebe dano do player) ────────────────────────────

        public override void TakeDamage(float rawAtk, float rawMatk, bool isPhysical)
        {
            if (_state == MonsterState.Dead) return;

            bool  crit = StatsCalculator.RollCrit(5f);
            float dmg;

            if (isPhysical)
                dmg = StatsCalculator.CalculatePhysicalDamage(rawAtk, _stats.DEF, crit, _stats.CritDMG);
            else
                dmg = StatsCalculator.CalculateMagicDamage(rawMatk, _stats.MDEF, crit, _stats.CritDMG);

            dmg = Mathf.Max(1f, dmg);
            _currentHP = Mathf.Max(0f, _currentHP - dmg);

            Debug.Log($"[MonsterEntity] {monsterDisplayName} tomou dano | " +
                      $"RawATK:{rawAtk:0} DEF:{_stats.DEF:0} Dmg:{dmg:0} Crit:{crit} | " +
                      $"HP:{_currentHP:0}/{_maxHP:0}");

            // Número flutuante
            Color color = crit ? Color.yellow : Color.white;
            FloatingTextManager.Instance?.Show(
                crit ? $"CRÍTICO! {dmg:0}" : $"{dmg:0}",
                transform.position + Vector3.up,
                color);

            // Atualiza barra de HP
            GetComponentInChildren<MonsterHealthBarUI>()?.UpdateBar(_currentHP, _maxHP);

            // Toma aggro de quem atacou
            if ((_state == MonsterState.Idle || _state == MonsterState.Patrol) && _target == null)
            {
                _target = FindObjectOfType<PlayerEntity>();
                if (_target != null) SetState(MonsterState.Chase);
            }

            if (_currentHP <= 0f) Die();
        }

        // ── Morte ─────────────────────────────────────────────────────────

        private void Die()
        {
            Debug.Log($"[MonsterEntity] {monsterDisplayName} morreu!");
            _state = MonsterState.Dead;
            if (_agent.isOnNavMesh) { _agent.ResetPath(); _agent.enabled = false; }
            OnDeselected();

            var player = FindObjectOfType<PlayerEntity>();
            if (player != null && player.IsInitialized)
            {
                bool leveled = player.Data.AddExperience(expReward);
                FloatingTextManager.Instance?.Show(
                    $"+{expReward} XP", transform.position + Vector3.up * 2f, Color.cyan);
                if (leveled)
                    FloatingTextManager.Instance?.Show(
                        "LEVEL UP!", player.transform.position + Vector3.up * 2.5f, Color.yellow);
                player.RefreshStats();
                SaveManager.Instance?.SaveCharacter(GameManager.Instance?.CurrentAccount, player.Data);
            }

            OnDied?.Invoke(this);
            Destroy(gameObject, 3f);
        }

        // ── Helpers ───────────────────────────────────────────────────────

        private void SetState(MonsterState s)
        {
            _state       = s;
            _attackTimer = 0f;
        }

        private float DistToTarget() =>
            _target != null
                ? Vector3.Distance(transform.position, _target.transform.position)
                : 999f;

        private void FaceTarget()
        {
            if (_target == null) return;
            Vector3 dir = _target.transform.position - transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude > 0.01f)
                transform.rotation = Quaternion.Slerp(
                    transform.rotation, Quaternion.LookRotation(dir), 10f * Time.deltaTime);
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere(transform.position, aggroRange);
            Gizmos.color = Color.red;
            Gizmos.DrawWireSphere(transform.position, attackRange);
            Gizmos.color = Color.blue;
            Gizmos.DrawWireSphere(transform.position, kiteDistance);
        }
    }
}
