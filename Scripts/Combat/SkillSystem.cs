using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using Mirror;
using RPG.Character;
using RPG.UI;
using RPG.Network;

namespace RPG.Combat
{
    public enum SkillType   { Physical, Magical, Heal, Buff }
    public enum SkillTarget { Enemy, Self, Ally }

    [Serializable]
    public class SkillData
    {
        public string      Name          = "Skill";
        public SkillType   Type          = SkillType.Physical;
        public SkillTarget Target        = SkillTarget.Enemy;
        public float       Cooldown      = 3f;
        public float       ManaCost      = 10f;
        public float       Range         = 4f;
        public float       AtkMultiplier = 1.0f;
        public float       CastTime      = 0f;
        public string      AnimTrigger   = "Attack";
        public Sprite      Icon;
    }

    /// <summary>
    /// SkillSystem v9
    ///
    /// CORREÇÃO CRÍTICA v9 — BUG: Player parava antes do range e não usava a skill.
    ///
    ///   CAUSA RAIZ (identificada na análise do código v8):
    ///
    ///     O SkillSystem v8 usava DOIS valores de fração que se SOMAVAM:
    ///       • STOP_DIST_FRACTION  = 0.75  → stoppingDistance = skill.Range * 0.75
    ///       • DESTINATION_FRACTION = 0.80 → destino a 0.80 * range do alvo
    ///
    ///     O NavMesh move o agente até um ponto a (range * 0.80) do alvo,
    ///     mas pára a stoppingDistance (range * 0.75) ANTES desse ponto.
    ///     Resultado: player parava a (range * 0.80 + range * 0.75) do alvo
    ///     em vez de entrar no range.
    ///
    ///     Além disso, CalculateRangeDestination retornava transform.position
    ///     quando dist <= skillRange * 0.80, o que fazia o agente parar no lugar
    ///     mesmo estando fora do range de uso da skill.
    ///
    ///   SOLUÇÃO APLICADA:
    ///
    ///     a) stoppingDistance zerado durante o walk (0.1f apenas como safety margin).
    ///        Não mais um múltiplo do range — isso eliminava a soma indesejada.
    ///
    ///     b) CalculateRangeDestination agora usa WALK_DEST_FRACTION = 0.85f
    ///        para definir onde o player vai parar (a 85% do range do alvo).
    ///        O check de "está no range?" usa skill.Range completo, garantindo
    ///        que 0.85 * range < range → o player SEMPRE entra no range.
    ///
    ///     c) Adicionado RANGE_CHECK_MARGIN = 1.05f: o player considera que está
    ///        "no range" quando dist <= skill.Range * 1.05. Isso absorve micro-jitter
    ///        do NavMesh sem abrir brechas de exploração (1.05 é conservador).
    ///
    ///     d) WalkThenSendCmd verifica range a cada frame ANTES de recalcular destino.
    ///        Se entrou no range, para imediatamente e envia o Command.
    ///
    ///     e) Loop de caminhar refatorado para ser mais direto:
    ///        cada frame: checar range → se sim, executar; se não, mover.
    ///
    ///   MELHORIAS ADICIONAIS:
    ///     - Verificação de alvo morto/mudado consolidada em IsTargetValid().
    ///     - Log mais informativo com contexto de range e distância.
    ///     - Timeout mantido em 15s (aumentado de 12s para mapas maiores).
    ///     - BasicAttackSystem recebe a mesma correção de frações.
    /// </summary>
    [RequireComponent(typeof(PlayerEntity))]
    public class SkillSystem : NetworkBehaviour
    {
        [Header("Debug — desative em builds de produção")]
        [SerializeField] private bool debugLogs = false;

        private const float CMD_MOVE_INTERVAL = 0.15f;
        private const float WALK_TIMEOUT      = 15f;

        // CORREÇÃO v9: destino a 85% do range → garante que dist ao alvo seja < range
        // Antes: 0.80 de destino + 0.75 de stoppingDistance = player parava longe demais
        private const float WALK_DEST_FRACTION = 0.85f;

        // Margem de tolerância no check de range (absorve jitter do NavMesh)
        // O servidor usa 1.3x, o cliente usa 1.05x (mais conservador)
        private const float RANGE_CHECK_MARGIN = 1.05f;

        // stoppingDistance mínimo durante walk — não mais um múltiplo do range
        private const float WALK_STOP_DIST = 0.2f;

        // ── Componentes ────────────────────────────────────────────────────
        private PlayerEntity            _player;
        private Animator                _animator;
        private NavMeshAgent            _agent;
        private NetworkPlayerController _controller;
        private NetworkInventory        _inventory;

        // ── Cooldown visual ────────────────────────────────────────────────
        private const int MAX_SKILLS = 4;
        private readonly float[] _uiCooldownTimers = new float[MAX_SKILLS];

        // ── Walk-to-range state ────────────────────────────────────────────
        private Coroutine   _walkCoroutine;
        private bool        _hasPendingWalk;
        private ITargetable _pendingTarget;
        private float       _lastCmdMoveTime;

        // ── Eventos para SkillBar UI ───────────────────────────────────────
        public event Action<int, float>  OnCooldownStarted;
        public event Action<int>         OnSkillFired;
        public event Action              OnSkillBarNeedsRefresh;

        public bool HasPendingAction => _hasPendingWalk;

        // ── Lifecycle ──────────────────────────────────────────────────────

        private void Awake()
        {
            _player     = GetComponent<PlayerEntity>();
            _animator   = GetComponentInChildren<Animator>();
            _agent      = GetComponent<NavMeshAgent>();
            _controller = GetComponent<NetworkPlayerController>();
            _inventory  = GetComponent<NetworkInventory>();
        }

        public override void OnStartLocalPlayer()
        {
            if (_inventory != null)
                _inventory.OnGemLoadoutChanged += OnGemLoadoutChanged;
        }

        public override void OnStopClient()
        {
            if (_inventory != null)
                _inventory.OnGemLoadoutChanged -= OnGemLoadoutChanged;

            CancelPendingWalk();
        }

        private void OnGemLoadoutChanged()
        {
            if (!isLocalPlayer) return;
            OnSkillBarNeedsRefresh?.Invoke();
            Log("Loadout de joias atualizado — SkillBar notificada.");
        }

        private void Update()
        {
            if (!isLocalPlayer) return;

            for (int i = 0; i < MAX_SKILLS; i++)
                if (_uiCooldownTimers[i] > 0f)
                    _uiCooldownTimers[i] -= Time.deltaTime;

            if (_hasPendingWalk && _player.IsDead)
            {
                CancelPendingWalk();
                return;
            }

            if (_hasPendingWalk && !IsTargetValid(_pendingTarget))
                CancelPendingWalk();
        }

        // ── Propriedades públicas ──────────────────────────────────────────

        public int SkillCount => MAX_SKILLS;

        public SkillData GetSkill(int index)
        {
            if (index < 0 || index >= MAX_SKILLS) return null;
            if (_inventory == null) return null;
            return _inventory.GetEquippedSkill(index);
        }

        public float GetUICooldown(int i)  => (i >= 0 && i < MAX_SKILLS) ? Mathf.Max(0f, _uiCooldownTimers[i]) : 0f;
        public bool  IsOnUICooldown(int i) => GetUICooldown(i) > 0f;

        // ── TryUseSkill ────────────────────────────────────────────────────

        public void TryUseSkill(int index)
        {
            if (!isLocalPlayer) return;
            if (!_player.IsInitialized || _player.IsDead) return;

            var skill = GetSkill(index);
            if (skill == null)
            {
                UIManager.Instance?.ShowMessage($"Nenhuma Joia equipada no slot {SkillSlotName(index)}!");
                return;
            }

            if (IsOnUICooldown(index))
            {
                UIManager.Instance?.ShowMessage($"{skill.Name}: aguarde {GetUICooldown(index):0.0}s");
                return;
            }

            var target = _player.CurrentTarget;

            if (skill.Target == SkillTarget.Enemy)
            {
                if (target == null)
                {
                    UIManager.Instance?.ShowMessage("Selecione um alvo primeiro!");
                    return;
                }
                if (!IsTargetValid(target))
                {
                    UIManager.Instance?.ShowMessage("Alvo já está morto!");
                    _player.ClearTarget();
                    UIManager.Instance?.ClearTargetPanel();
                    return;
                }
            }

            CancelPendingWalk();

            // Skills de self/heal/buff não precisam de aproximação
            if (skill.Target == SkillTarget.Self || skill.Type == SkillType.Heal || skill.Type == SkillType.Buff)
            {
                SendSelfSkillCmd(index);
                return;
            }

            float dist = target != null ? Vector3.Distance(transform.position, target.Position) : 0f;

            // CORREÇÃO v9: usa RANGE_CHECK_MARGIN para ser levemente permissivo
            if (dist <= skill.Range * RANGE_CHECK_MARGIN)
            {
                // Já no range: para e executa imediatamente
                if (_agent != null && _agent.isOnNavMesh)
                {
                    _agent.ResetPath();
                    _agent.stoppingDistance = 0.5f;
                }
                SendSkillCmd(index, target, skill.Type == SkillType.Physical);
            }
            else
            {
                Log($"Fora de range ({dist:0.1} > {skill.Range:0.1}). Caminhando...");
                _hasPendingWalk  = true;
                _pendingTarget   = target;
                _lastCmdMoveTime = -CMD_MOVE_INTERVAL;
                _walkCoroutine   = StartCoroutine(WalkThenSendCmd(index, skill, target));
            }
        }

        public void CancelPendingWalk()
        {
            if (_walkCoroutine != null)
            {
                StopCoroutine(_walkCoroutine);
                _walkCoroutine = null;
            }
            _hasPendingWalk = false;
            _pendingTarget  = null;

            if (_agent != null && _agent.isOnNavMesh)
            {
                _agent.stoppingDistance = 0.5f;
                _agent.ResetPath();
            }
        }

        // ── Walk-to-range ──────────────────────────────────────────────────

        /// <summary>
        /// CORREÇÃO v9 — Walk que realmente entra no range antes de disparar a skill.
        ///
        /// LÓGICA CORRIGIDA:
        ///   1. stoppingDistance = WALK_STOP_DIST (0.2f) — NÃO um múltiplo do range.
        ///      Isso evita que stoppingDistance e destino se somem.
        ///
        ///   2. Destino = targetPos - dir * (skill.Range * WALK_DEST_FRACTION)
        ///      WALK_DEST_FRACTION = 0.85 → player vai até 85% do range do alvo.
        ///      Como 0.85 < 1.0, o player SEMPRE entra no range antes de parar.
        ///
        ///   3. Check de range usa skill.Range * RANGE_CHECK_MARGIN (1.05).
        ///      Isso absorve micro-oscilações do NavMesh sem abrir exploits.
        ///
        ///   4. Ao detectar que está no range:
        ///      a) ResetPath() imediato para parar o agente
        ///      b) yield return null para garantir que o NavMesh processou
        ///      c) Verifica novamente se o alvo ainda é válido
        ///      d) Dispara SendSkillCmd
        /// </summary>
        private IEnumerator WalkThenSendCmd(int index, SkillData skill, ITargetable target)
        {
            // CORREÇÃO v9: stoppingDistance pequeno — não mais um múltiplo do range
            if (_agent != null && _agent.isOnNavMesh)
            {
                _agent.stoppingDistance = WALK_STOP_DIST;
            }

            float timeout        = WALK_TIMEOUT;
            float effectiveRange = skill.Range * RANGE_CHECK_MARGIN;

            while (timeout > 0f)
            {
                timeout -= Time.deltaTime;

                // Verifica condições de cancelamento
                if (_player.IsDead)
                {
                    Log("WalkThenSendCmd: jogador morreu.");
                    break;
                }

                if (!IsTargetValid(target))
                {
                    _player.ClearTarget();
                    UIManager.Instance?.ClearTargetPanel();
                    Log("WalkThenSendCmd: alvo inválido/morto.");
                    break;
                }

                if (_player.CurrentTarget != target)
                {
                    Log("WalkThenSendCmd: alvo mudou.");
                    break;
                }

                float dist = Vector3.Distance(transform.position, target.Position);

                // CORREÇÃO v9: check de range com margem — entra no range e executa
                if (dist <= effectiveRange)
                {
                    // Para o agente COMPLETAMENTE antes de executar
                    if (_agent != null && _agent.isOnNavMesh)
                    {
                        _agent.ResetPath();
                        _agent.stoppingDistance = 0.5f;
                        _agent.velocity         = Vector3.zero;
                    }

                    _hasPendingWalk = false;
                    _pendingTarget  = null;

                    // Aguarda 1 frame para o NavMesh processar o ResetPath
                    yield return null;

                    // Verifica novamente após o yield
                    if (!_player.IsDead && IsTargetValid(target) && _player.CurrentTarget == target)
                    {
                        Log($"No range ({dist:0.2}/{skill.Range:0.1}). Executando skill {index}.");
                        SendSkillCmd(index, target, skill.Type == SkillType.Physical);
                    }

                    yield break;
                }

                // CORREÇÃO v9: destino a WALK_DEST_FRACTION do range
                // Isso garante que o player vai parar DENTRO do range de uso
                if (_agent != null && _agent.isOnNavMesh)
                {
                    Vector3 destination = CalculateWalkDestination(target.Position, skill.Range);
                    _agent.SetDestination(destination);
                }

                // Envia CmdMoveTo ao servidor com throttle
                if (Time.time - _lastCmdMoveTime >= CMD_MOVE_INTERVAL)
                {
                    _lastCmdMoveTime = Time.time;
                    Vector3 serverDest = CalculateWalkDestination(target.Position, skill.Range);
                    _controller?.CmdMoveTo(serverDest);
                }

                yield return null;
            }

            if (timeout <= 0f)
                Log($"WalkThenSendCmd: timeout após {WALK_TIMEOUT}s para skill {index}.");

            // Restaura estado ao sair por qualquer motivo
            if (_agent != null && _agent.isOnNavMesh)
            {
                _agent.stoppingDistance = 0.5f;
                _agent.ResetPath();
            }

            _hasPendingWalk = false;
            _pendingTarget  = null;
            _walkCoroutine  = null;
        }

        /// <summary>
        /// CORREÇÃO v9 — Calcula destino de caminhada dentro do range da skill.
        ///
        /// O destino fica a (skill.Range * WALK_DEST_FRACTION) do alvo.
        /// Como WALK_DEST_FRACTION = 0.85 e o check de range usa 1.0 (ou 1.05),
        /// o player SEMPRE entra no range antes de chegar ao destino calculado.
        ///
        /// Diferença do v8:
        ///   v8: destino a 0.80 do range + stoppingDistance a 0.75 do range = para longe demais
        ///   v9: destino a 0.85 do range + stoppingDistance = 0.2f (fixo, pequeno)
        ///       → player para bem dentro do range de uso
        /// </summary>
        private Vector3 CalculateWalkDestination(Vector3 targetPos, float skillRange)
        {
            Vector3 toTarget = targetPos - transform.position;
            float dist = toTarget.magnitude;

            float safeStopDist = skillRange * WALK_DEST_FRACTION;

            // Se já está bem próximo do destino ideal, não mover
            if (dist <= safeStopDist * 0.95f)
                return transform.position;

            Vector3 direction   = toTarget.normalized;
            Vector3 destination = targetPos - direction * safeStopDist;

            if (NavMesh.SamplePosition(destination, out NavMeshHit hit, 3f, NavMesh.AllAreas))
                return hit.position;

            return destination;
        }

        // ── Envio dos Commands ao servidor ─────────────────────────────────

        private void SendSkillCmd(int skillIndex, ITargetable target, bool isPhysical)
        {
            var skill = GetSkill(skillIndex);

            // Para o agente ao executar
            if (_agent != null && _agent.isOnNavMesh)
            {
                _agent.ResetPath();
                _agent.stoppingDistance = 0.5f;
                _agent.velocity         = Vector3.zero;
            }

            if (_animator != null && skill != null && !string.IsNullOrEmpty(skill.AnimTrigger))
                _animator.SetTrigger(skill.AnimTrigger);

            var targetNB = target as NetworkBehaviour;
            if (targetNB == null)
            {
                Log("Alvo não é NetworkBehaviour — skill não enviada.");
                return;
            }

            // Rotaciona em direção ao alvo
            Vector3 dir = target.Position - transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude > 0.01f)
                transform.rotation = Quaternion.LookRotation(dir);

            uint attackerNetId = GetComponent<NetworkIdentity>().netId;

            var monster = targetNB.GetComponent<NetworkMonsterEntity>();
            if (monster != null)
            {
                monster.CmdRequestSkill(attackerNetId, skillIndex, isPhysical);
                Log($"CmdRequestSkill → {monster.DisplayName} skill:{skillIndex}");
            }
            else
            {
                if (debugLogs)
                    UIManager.Instance?.ShowMessage("PvP ainda não implementado.");
            }
        }

        private void SendSelfSkillCmd(int skillIndex)
        {
            var netPlayer = GetComponent<RPG.Network.NetworkPlayer>();
            netPlayer?.CmdRequestSelfSkill(skillIndex);
            Log($"CmdRequestSelfSkill skill:{skillIndex}");
        }

        // ── Resultado vindo do servidor ────────────────────────────────────

        public void OnServerSkillConfirmed(int skillIndex, float cooldownDuration)
        {
            if (skillIndex < 0 || skillIndex >= MAX_SKILLS) return;
            _uiCooldownTimers[skillIndex] = cooldownDuration;
            OnCooldownStarted?.Invoke(skillIndex, cooldownDuration);
            OnSkillFired?.Invoke(skillIndex);
            Log($"Skill {skillIndex} confirmada. Cooldown: {cooldownDuration:0.0}s");
        }

        public void OnServerSkillRejected(int skillIndex, string reason)
        {
            UIManager.Instance?.ShowMessage(reason);
            Log($"Skill {skillIndex} rejeitada: {reason}");
        }

        // ── Helpers ────────────────────────────────────────────────────────

        /// <summary>
        /// Verifica se o alvo ainda é válido (não nulo, não morto, objeto Unity vivo).
        /// Centraliza a lógica que antes estava duplicada em vários lugares.
        /// </summary>
        private static bool IsTargetValid(ITargetable target)
        {
            if (target == null) return false;
            if (target is UnityEngine.Object unityObj && unityObj == null) return false;
            return !target.IsDead;
        }

        private static string SkillSlotName(int index) => index switch
        {
            0 => "Q", 1 => "W", 2 => "E", 3 => "R", _ => index.ToString()
        };

        private void Log(string msg)
        {
            if (debugLogs) Debug.Log($"[SkillSystem] {msg}");
        }
    }
}
