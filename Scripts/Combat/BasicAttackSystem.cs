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
    /// BasicAttackSystem v2
    ///
    /// CORREÇÕES v2 — Mesma família de bugs do SkillSystem v8.
    ///
    ///   BUG CORRIGIDO: Player sobrepunha o monstro durante o auto-ataque.
    ///
    ///   CAUSA: ChaseTarget() chamava `_agent.SetDestination(_attackTarget.Position)`,
    ///   onde o destino ERA a posição exata do monstro. O stoppingDistance de
    ///   (attackRange * 0.85f) funciona como margem de PARADA, não de DESTINO.
    ///   O NavMesh move o agente até o destino e só para quando está a
    ///   stoppingDistance do destino — mas se o destino muda todo frame (monstro
    ///   se move), o stoppingDistance pode não ter efeito correto.
    ///
    ///   SOLUÇÃO: Igual ao SkillSystem — calculamos um ponto intermediário
    ///   que fica dentro do range, na direção player→monstro. O NavMesh
    ///   leva o player exatamente até esse ponto e para naturalmente.
    ///
    ///   stoppingDistance é mantido como fallback de segurança em 0.5f
    ///   (o comportamento natural do agente) já que o destino calculado
    ///   já garante a posição correta.
    ///
    ///   MELHORIA: Adicionado método público GetAttackRange() para que
    ///   o NetworkMonsterEntity possa usar o mesmo range ao validar ataques.
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
        [SerializeField] private float moveCommandInterval = 0.2f;

        [Header("Debug")]
        [SerializeField] private bool debugLogs = false;

        // Fração do attackRange para calcular o ponto de destino intermediário.
        // Player vai até (attackRange * DEST_FRACTION) do monstro e para.
        private const float DEST_FRACTION = 0.80f;

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

        /// <summary>
        /// Chamado pelo NetworkPlayerController ao clicar em um monstro.
        /// Detecta duplo clique para iniciar auto-ataque.
        /// </summary>
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

        /// <summary>Cancela o auto-ataque.</summary>
        public void CancelAutoAttack()
        {
            if (!_autoAttacking) return;

            _autoAttacking = false;
            _attackTarget  = null;

            if (_agent != null && _agent.isOnNavMesh)
            {
                _agent.stoppingDistance = 0.5f;
                _agent.ResetPath();
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
            _attackTimer   = GetAttackInterval(); // aguarda um ciclo antes do 1º ataque

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

            float dist = Vector3.Distance(transform.position, _attackTarget.Position);

            if (dist > attackRange)
            {
                ChaseTarget();
            }
            else
            {
                // CORREÇÃO v2: para o agente completamente quando está no range
                if (_agent != null && _agent.isOnNavMesh && _agent.hasPath)
                {
                    _agent.ResetPath();
                    _agent.stoppingDistance = 0.5f;
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
        /// CORREÇÃO v2 — Perseguição usa destino intermediário, não posição do monstro.
        ///
        /// Calculamos um ponto que fica a (attackRange * DEST_FRACTION) do monstro
        /// na direção player→monstro. O NavMesh para naturalmente nesse ponto.
        /// O stoppingDistance volta para 0.5f padrão (destino intermediário = parada natural).
        /// </summary>
        private void ChaseTarget()
        {
            if (_agent != null && _agent.isOnNavMesh)
            {
                // CORREÇÃO: destino é o ponto no range, não o monstro
                Vector3 destination = CalculateChaseDestination(_attackTarget.Position);
                _agent.stoppingDistance = 0.5f; // parada natural no ponto calculado
                _agent.SetDestination(destination);
            }

            // Throttle de CmdMoveTo para o servidor
            if (Time.time - _lastMoveCmd >= moveCommandInterval)
            {
                _lastMoveCmd = Time.time;
                // Envia destino intermediário ao servidor também
                Vector3 serverDest = CalculateChaseDestination(_attackTarget.Position);
                _controller?.CmdMoveTo(serverDest);
            }
        }

        /// <summary>
        /// Calcula ponto de destino dentro do range do ataque, evitando sobrepor o monstro.
        /// </summary>
        private Vector3 CalculateChaseDestination(Vector3 targetPos)
        {
            Vector3 toTarget = targetPos - transform.position;
            float dist = toTarget.magnitude;

            // Já está no range ou muito perto — não precisa mover
            if (dist <= attackRange * DEST_FRACTION)
                return transform.position;

            // Ponto a (attackRange * DEST_FRACTION) do monstro, na direção do player
            float   stopDist    = attackRange * DEST_FRACTION;
            Vector3 direction   = toTarget.normalized;
            Vector3 destination = targetPos - direction * stopDist;

            // Snap ao NavMesh
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
            _attackTarget.CmdBasicAttack(myNetId);

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

            // Mostra o raio efetivo de destino (onde o player vai parar)
            Gizmos.color = new Color(0f, 1f, 0.5f, 0.25f);
            Gizmos.DrawWireSphere(transform.position, attackRange * DEST_FRACTION);
        }
#endif
    }
}
