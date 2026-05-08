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
    /// BasicAttackSystem v1 — Ataque básico por duplo clique.
    ///
    /// FUNCIONAMENTO:
    ///   - Duplo clique esquerdo sobre um monstro → jogador anda até o alvo e
    ///     ataca automaticamente em loop até cancelar.
    ///   - Cancelamento: clique em outro lugar, clique em outro monstro,
    ///     morte do alvo, ou morte do jogador.
    ///   - Sem custo de MP, sem joia equipada, sem cooldown de skill.
    ///     Usa ATK puro + ASPD do personagem.
    ///
    /// SETUP:
    ///   Adicione este componente no prefab do player (junto com SkillSystem).
    ///   O NetworkPlayerController chama TryRegisterClick() automaticamente.
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

        public bool IsAutoAttacking => _autoAttacking;

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
        /// Registra o clique e detecta duplo clique.
        /// Retorna true se foi duplo clique (auto-ataque iniciado).
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

        /// <summary>
        /// Cancela o auto-ataque. Chamado ao clicar no chão ou em outro alvo.
        /// </summary>
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

            // Compara como UnityEngine.Object para evitar CS0252
            // (ITargetable não implementa == por valor, a comparação precisa ser por referência de objeto Unity)
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
                // Dentro de range: para o agente e tick de ataque
                if (_agent != null && _agent.isOnNavMesh && _agent.hasPath)
                    _agent.ResetPath();

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

        private void ChaseTarget()
        {
            if (_agent != null && _agent.isOnNavMesh)
            {
                _agent.stoppingDistance = attackRange * 0.85f;
                _agent.SetDestination(_attackTarget.Position);
            }

            if (Time.time - _lastMoveCmd >= moveCommandInterval)
            {
                _lastMoveCmd = Time.time;
                _controller?.CmdMoveTo(_attackTarget.Position);
            }
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

        /// <summary>
        /// Verifica se o alvo foi destruído pelo Unity ou está morto no jogo.
        /// Cast explícito para UnityEngine.Object evita o warning CS0252.
        /// </summary>
        private static bool IsTargetGone(NetworkMonsterEntity target)
        {
            return IsUnityNull(target) || target.IsDead;
        }

        /// <summary>
        /// Null-check correto para objetos Unity.
        /// Evita CS0252 (comparação de referência com interface/classe não-Object).
        /// </summary>
        private static bool IsUnityNull(NetworkMonsterEntity target)
        {
            return (UnityEngine.Object)target == null;
        }

        private void Log(string msg)
        {
            if (debugLogs) Debug.Log($"[BasicAttackSystem] {msg}");
        }

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(1f, 0.5f, 0f, 0.4f);
            Gizmos.DrawWireSphere(transform.position, attackRange);
        }
#endif
    }
}