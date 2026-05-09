using UnityEngine;
using UnityEngine.AI;
using Mirror;
using RPG.Character;
using RPG.UI;
using RPG.Network;
using RPG.Data;

namespace RPG.Combat
{
    /// <summary>
    /// BasicAttackSystem v3
    ///
    /// CORREÇÃO v3 — Mesma família de bugs corrigida no SkillSystem v9.
    ///
    ///   BUG: Player podia sobrepor o monstro ou parar longe demais durante auto-ataque.
    ///
    ///   CAUSA: DEST_FRACTION (0.80) e stoppingDistance (0.5f antes, mas era
    ///   potencialmente maior em certas condições) se somavam ou o destino calculado
    ///   ficava muito próximo do monstro.
    ///
    ///   SOLUÇÃO (alinhada com SkillSystem v9):
    ///     • DEST_FRACTION = 0.80f → player vai para 80% do attackRange (conservador)
    ///     • stoppingDistance = 0.2f durante perseguição (fixo e pequeno)
    ///     • RANGE_CHECK_MARGIN = 1.05f → absorve micro-jitter do NavMesh
    ///     • Quando entra no range: ResetPath + velocity=zero antes de atacar
    ///     • velocity zerrada ao parar para evitar deslizamento residual
    /// </summary>
    [RequireComponent(typeof(PlayerEntity))]
    [RequireComponent(typeof(NetworkIdentity))]
    public class BasicAttackSystem : NetworkBehaviour
    {
        [Header("Configuração de Ataque")]
        [Tooltip("Distância mínima para atacar (m).")]
        [SerializeField] private float attackRange = 2.5f;

        [Tooltip("Segundos entre ataques (usado apenas se useCharacterASPD = false).")]
        [SerializeField] private float attackInterval = 1.2f;

        [Tooltip("Se true, usa 1/ASPD do personagem como intervalo de ataque.")]
        [SerializeField] private bool useCharacterASPD = true;

        [Tooltip("Janela de tempo para reconhecer duplo clique (s).")]
        [SerializeField] private float doubleClickTime = 0.35f;

        [Tooltip("Frequência máxima de envio do CmdMoveTo durante perseguição (s).")]
        [SerializeField] private float moveCommandInterval = 0.15f;

        [Header("Debug")]
        [SerializeField] private bool debugLogs = false;

        // CORREÇÃO v3: destino a 80% do range → player para dentro do range de ataque
        private const float DEST_FRACTION        = 0.80f;
        // Margem de tolerância no check de range
        private const float RANGE_CHECK_MARGIN   = 1.05f;
        // stoppingDistance fixo durante perseguição (não mais múltiplo do range)
        private const float CHASE_STOP_DIST      = 0.2f;

        // ── Componentes ────────────────────────────────────────────────────
        private PlayerEntity            _player;
        private NavMeshAgent            _agent;
        private Animator                _animator;
        private NetworkPlayerController _controller;
        private SkillSystem             _skillSystem;

        // ── Estado de ataque ───────────────────────────────────────────────
        private NetworkMonsterEntity _attackTarget;
        private bool                 _autoAttacking = false;
        private float                _attackTimer   = 0f;
        private float                _lastMoveCmd   = 0f;

        // ── Estado de duplo clique ─────────────────────────────────────────
        private float                _lastClickTime   = -999f;
        private NetworkMonsterEntity _lastClickTarget;

        public bool  IsAutoAttacking => _autoAttacking;
        public float AttackRange     => attackRange;

        // ── Lifecycle ──────────────────────────────────────────────────────

        private void Awake()
        {
            _player      = GetComponent<PlayerEntity>();
            _agent       = GetComponent<NavMeshAgent>();
            _animator    = GetComponentInChildren<Animator>();
            _controller  = GetComponent<NetworkPlayerController>();
            _skillSystem = GetComponent<SkillSystem>();
        }

        private void Update()
        {
            if (!isLocalPlayer) return;
            if (!_player.IsInitialized || _player.IsDead) return;
            if (_autoAttacking) UpdateAutoAttack();
        }

        // ── API pública ────────────────────────────────────────────────────

        public bool TryRegisterClick(NetworkMonsterEntity monster)
        {
            if (IsUnityNull(monster) || monster.IsDead) return false;

            float now           = Time.time;
            bool  isDoubleClick = (now - _lastClickTime) <= doubleClickTime
                                  && _lastClickTarget == monster;

            _lastClickTime   = now;
            _lastClickTarget = monster;

            if (isDoubleClick)
            {
                StartAutoAttack(monster);
                return true;
            }

            return false;
        }

        public void CancelAutoAttack()
        {
            if (!_autoAttacking) return;

            _autoAttacking = false;
            _attackTarget  = null;

            if (_agent != null && _agent.isOnNavMesh)
            {
                _agent.stoppingDistance = 0.5f;
                _agent.ResetPath();
                _agent.velocity = Vector3.zero;
            }

            Log("Auto-ataque cancelado.");
        }

        // ── Início do auto-ataque ──────────────────────────────────────────

        private void StartAutoAttack(NetworkMonsterEntity monster)
        {
            _skillSystem?.CancelPendingWalk();
            CancelAutoAttack();

            _attackTarget  = monster;
            _autoAttacking = true;
            _attackTimer   = GetAttackInterval();

            _player.SetTarget(monster);
            UIManager.Instance?.UpdateTargetPanel(monster);

            Log($"Auto-ataque iniciado → {monster.DisplayName}");
        }

        // ── Loop de auto-ataque ────────────────────────────────────────────

        private void UpdateAutoAttack()
        {
            if (IsTargetGone(_attackTarget))
            {
                Log("Alvo destruído ou morto — cancelando.");
                CancelAutoAttack();
                _player.ClearTarget();
                UIManager.Instance?.ClearTargetPanel();
                return;
            }

            if (!ReferenceEquals(_player.CurrentTarget as UnityEngine.Object,
                                  _attackTarget as UnityEngine.Object))
            {
                CancelAutoAttack();
                return;
            }

            float dist              = Vector3.Distance(transform.position, _attackTarget.Position);
            float effectiveRange    = attackRange * RANGE_CHECK_MARGIN;

            if (dist > effectiveRange)
            {
                ChaseTarget();
            }
            else
            {
                // CORREÇÃO v3: para o agente com velocity zerrada
                if (_agent != null && _agent.isOnNavMesh)
                {
                    if (_agent.hasPath)
                    {
                        _agent.ResetPath();
                        _agent.velocity         = Vector3.zero;
                        _agent.stoppingDistance = 0.5f;
                    }
                }

                _attackTimer += Time.deltaTime;
                if (_attackTimer >= GetAttackInterval())
                {
                    _attackTimer = 0f;
                    ExecuteBasicAttack();
                }

                // Rotaciona suavemente em direção ao alvo
                Vector3 dir = _attackTarget.Position - transform.position;
                dir.y = 0f;
                if (dir.sqrMagnitude > 0.01f)
                    transform.rotation = Quaternion.Slerp(
                        transform.rotation,
                        Quaternion.LookRotation(dir),
                        10f * Time.deltaTime);
            }
        }

        /// <summary>
        /// CORREÇÃO v3 — Perseguição usa destino intermediário + stoppingDistance fixo.
        ///
        /// Destino = posição a (attackRange * DEST_FRACTION) do monstro.
        /// stoppingDistance = CHASE_STOP_DIST (0.2f fixo) — não um múltiplo do range.
        /// O player para naturalmente quando chega ao destino, que já está dentro do range.
        /// </summary>
        private void ChaseTarget()
        {
            if (_agent != null && _agent.isOnNavMesh)
            {
                Vector3 destination = CalculateChaseDestination(_attackTarget.Position);
                _agent.stoppingDistance = CHASE_STOP_DIST;
                _agent.SetDestination(destination);
            }

            // Throttle de CmdMoveTo para o servidor
            if (Time.time - _lastMoveCmd >= moveCommandInterval)
            {
                _lastMoveCmd = Time.time;
                Vector3 serverDest = CalculateChaseDestination(_attackTarget.Position);
                _controller?.CmdMoveTo(serverDest);
            }
        }

        /// <summary>
        /// Calcula o destino de perseguição dentro do range de ataque.
        /// O player para a (attackRange * DEST_FRACTION) do monstro.
        /// </summary>
        private Vector3 CalculateChaseDestination(Vector3 targetPos)
        {
            Vector3 toTarget = targetPos - transform.position;
            float dist = toTarget.magnitude;

            float safeStopDist = attackRange * DEST_FRACTION;

            if (dist <= safeStopDist * 0.95f)
                return transform.position;

            Vector3 direction   = toTarget.normalized;
            Vector3 destination = targetPos - direction * safeStopDist;

            if (NavMesh.SamplePosition(destination, out NavMeshHit hit, 2f, NavMesh.AllAreas))
                return hit.position;

            return destination;
        }

        // ── Execução do ataque ─────────────────────────────────────────────

        private void ExecuteBasicAttack()
        {
            if (IsTargetGone(_attackTarget)) return;

            _animator?.SetTrigger("Attack");

            uint myNetId = GetComponent<NetworkIdentity>().netId;
            _attackTarget.CmdBasicAttack(myNetId, attackRange);

            Log($"CmdBasicAttack → {_attackTarget.DisplayName}");
        }

        // ── Helpers ────────────────────────────────────────────────────────

        private float GetAttackInterval()
        {
            if (useCharacterASPD && _player.IsInitialized && _player.Stats != null)
                return Mathf.Clamp(1f / Mathf.Max(0.1f, _player.Stats.ASPD), 0.3f, 3f);
            return attackInterval;
        }

        private static bool IsTargetGone(NetworkMonsterEntity target)
            => IsUnityNull(target) || target.IsDead;

        private static bool IsUnityNull(NetworkMonsterEntity target)
            => (UnityEngine.Object)target == null;

        private void Log(string msg)
        {
            if (debugLogs) Debug.Log($"[BasicAttackSystem] {msg}");
        }

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(1f, 0.5f, 0f, 0.4f);
            Gizmos.DrawWireSphere(transform.position, attackRange);

            Gizmos.color = new Color(0f, 1f, 0.5f, 0.25f);
            Gizmos.DrawWireSphere(transform.position, attackRange * DEST_FRACTION);
        }
#endif
    }
}