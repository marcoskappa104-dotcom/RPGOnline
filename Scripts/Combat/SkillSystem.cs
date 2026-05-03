using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using Mirror;
using RPG.Character;
using RPG.UI;

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
    /// SkillSystem — CLIENTE APENAS SOLICITA. SERVIDOR DECIDE E EXECUTA TUDO.
    ///
    /// ARQUITETURA:
    ///   - Cliente NÃO gasta MP localmente.
    ///   - Cliente NÃO inicia cooldown localmente.
    ///   - Cliente apenas:
    ///       1. Verifica se há alvo (validação de UX, não de segurança).
    ///       2. Envia CmdRequestSkill ao servidor com (netId do alvo, índice da skill).
    ///       3. Aguarda o servidor confirmar via RpcSkillResult (cooldown, MP gasto).
    ///   - Servidor valida: MP disponível, cooldown, range, alvo vivo.
    ///   - Servidor retorna o resultado via TargetRpc.
    ///   - Walk-to-range: cliente se move localmente para UX,
    ///     mas o servidor só aplica dano se o range for válido no momento do Cmd.
    ///
    /// COOLDOWN UI:
    ///   - Cooldown local é SOMENTE visual (feedback de UX).
    ///   - O servidor pode rejeitar um Cmd mesmo sem cooldown local
    ///     (ex: se o jogador estava dessincronizado).
    /// </summary>
    [RequireComponent(typeof(PlayerEntity))]
    public class SkillSystem : NetworkBehaviour
    {
        [Header("Skills (Q=0  W=1  E=2  R=3)")]
        [SerializeField] private List<SkillData> skills = new List<SkillData>();

        [Header("Debug")]
        [SerializeField] private bool debugLogs = true;

        // ── Componentes ────────────────────────────────────────────────────
        private PlayerEntity _player;
        private Animator     _animator;
        private NavMeshAgent _agent;

        // ── Cooldown LOCAL — apenas visual, não é autoridade ───────────────
        private const int MAX_SKILLS = 8;
        private float[] _uiCooldownTimers = new float[MAX_SKILLS];

        // ── Estado de movimento pendente ───────────────────────────────────
        private Coroutine   _walkCoroutine;
        private bool        _hasPendingWalk;
        private ITargetable _pendingTarget;

        // ── Eventos para a SkillBar (UI) ───────────────────────────────────
        public event Action<int, float> OnCooldownStarted; // (índice, duração)
        public event Action<int>        OnSkillFired;

        public bool HasPendingAction => _hasPendingWalk;

        // ── Lifecycle ──────────────────────────────────────────────────────

        private void Awake()
        {
            _player   = GetComponent<PlayerEntity>();
            _animator = GetComponentInChildren<Animator>();
            _agent    = GetComponent<NavMeshAgent>();
        }

        private void Update()
        {
            if (!isLocalPlayer) return;

            // Atualiza timers de cooldown visual
            for (int i = 0; i < MAX_SKILLS; i++)
                if (_uiCooldownTimers[i] > 0f)
                    _uiCooldownTimers[i] -= Time.deltaTime;

            // Cancela walk se alvo mudou externamente
            if (_hasPendingWalk && _pendingTarget != _player.CurrentTarget)
                CancelPendingWalk();
        }

        // ── Propriedades públicas ──────────────────────────────────────────

        public int       SkillCount          => skills.Count;
        public SkillData GetSkill(int i)     => (i >= 0 && i < skills.Count) ? skills[i] : null;
        public float     GetUICooldown(int i) => (i >= 0 && i < MAX_SKILLS) ? Mathf.Max(0f, _uiCooldownTimers[i]) : 0f;
        public bool      IsOnUICooldown(int i) => GetUICooldown(i) > 0f;

        // ── TryUseSkill — ponto de entrada do input ────────────────────────

        public void TryUseSkill(int index)
        {
            if (!isLocalPlayer) return;
            if (!_player.IsInitialized || _player.IsDead) return;

            var skill = GetSkill(index);
            if (skill == null) { Log($"Skill {index} não existe."); return; }

            // Validação de UX (não de segurança — o servidor valida de verdade)
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
                if (target.IsDead)
                {
                    UIManager.Instance?.ShowMessage("Alvo já está morto!");
                    _player.ClearTarget();
                    UIManager.Instance?.ClearTargetPanel();
                    return;
                }
            }

            CancelPendingWalk();

            if (skill.Target == SkillTarget.Self || skill.Type == SkillType.Heal || skill.Type == SkillType.Buff)
            {
                // Self/Heal/Buff: envia diretamente ao servidor
                SendSkillCmd(index, 0, isPhysical: false);
                return;
            }

            // Skill de dano: verifica range para UX (walk-to-range se necessário)
            float dist = target != null ? Vector3.Distance(transform.position, target.Position) : 0f;

            if (dist > skill.Range)
            {
                Log($"Fora de range ({dist:0.1} > {skill.Range}). Caminhando...");
                _hasPendingWalk = true;
                _pendingTarget  = target;
                _walkCoroutine  = StartCoroutine(WalkThenSendCmd(index, skill, target));
            }
            else
            {
                SendSkillCmd(index, (target as NetworkBehaviour)?.netId ?? 0, skill.Type == SkillType.Physical);
            }
        }

        public void CancelPendingWalk()
        {
            if (_walkCoroutine != null) { StopCoroutine(_walkCoroutine); _walkCoroutine = null; }
            _hasPendingWalk = false;
            _pendingTarget  = null;
            if (_agent != null) _agent.stoppingDistance = 0.5f;
        }

        // ── Walk-to-range ──────────────────────────────────────────────────

        private IEnumerator WalkThenSendCmd(int index, SkillData skill, ITargetable target)
        {
            _agent.stoppingDistance = skill.Range * 0.85f;
            float timeout = 20f;

            while (timeout > 0f)
            {
                timeout -= Time.deltaTime;

                if (target == null || target.IsDead)
                {
                    _player.ClearTarget();
                    UIManager.Instance?.ClearTargetPanel();
                    break;
                }

                if (_player.CurrentTarget != target) break;

                float dist = Vector3.Distance(transform.position, target.Position);

                if (dist <= skill.Range)
                {
                    _agent.stoppingDistance = 0.5f;
                    _hasPendingWalk = false;
                    _pendingTarget  = null;

                    var targetNB = target as NetworkBehaviour;
                    SendSkillCmd(index, targetNB?.netId ?? 0, skill.Type == SkillType.Physical);
                    yield break;
                }

                // Movimento local apenas para UX — o servidor vai re-validar o range
                if (_agent.isOnNavMesh)
                    _agent.SetDestination(target.Position);

                yield return null;
            }

            _agent.stoppingDistance = 0.5f;
            _hasPendingWalk = false;
            _pendingTarget  = null;
            _walkCoroutine  = null;
        }

        // ── Envio do Command ao servidor ───────────────────────────────────

        private void SendSkillCmd(int skillIndex, uint targetNetId, bool isPhysical)
        {
            // Animação local (feedback visual imediato — servidor vai confirmar)
            var skill = GetSkill(skillIndex);
            if (_animator != null && skill != null && !string.IsNullOrEmpty(skill.AnimTrigger))
                _animator.SetTrigger(skill.AnimTrigger);

            // Vira para o alvo localmente (visual)
            if (targetNetId != 0 && NetworkClient.spawned.TryGetValue(targetNetId, out var id))
            {
                Vector3 dir = id.transform.position - transform.position;
                dir.y = 0f;
                if (dir.sqrMagnitude > 0.01f)
                    transform.rotation = Quaternion.LookRotation(dir);
            }

            // Envia apenas o pedido — ZERO dados de dano ou stats
            uint attackerNetId = GetComponent<NetworkIdentity>().netId;

            if (!NetworkClient.spawned.TryGetValue(targetNetId, out var targetIdentity) && targetNetId != 0)
            {
                Log($"Alvo netId:{targetNetId} não encontrado.");
                return;
            }

            var monster = targetIdentity?.GetComponent<RPG.Network.NetworkMonsterEntity>();
            if (monster != null)
            {
                monster.CmdRequestSkill(attackerNetId, skillIndex, isPhysical);
                Log($"CmdRequestSkill → monstro netId:{targetNetId} skill:{skillIndex}");
            }
            else if (targetNetId == 0)
            {
                // Self skill (heal/buff) — envia para o próprio NetworkPlayer
                var netPlayer = GetComponent<RPG.Network.NetworkPlayer>();
                netPlayer?.CmdRequestSelfSkill(skillIndex);
                Log($"CmdRequestSelfSkill skill:{skillIndex}");
            }
        }

        // ── Resultado vindo do servidor ────────────────────────────────────

        /// <summary>
        /// Chamado pelo NetworkPlayer via TargetRpc após o servidor processar a skill.
        /// Inicia o cooldown visual e dispara o evento para a SkillBar.
        /// </summary>
        public void OnServerSkillConfirmed(int skillIndex, float cooldownDuration)
        {
            if (skillIndex < 0 || skillIndex >= MAX_SKILLS) return;
            _uiCooldownTimers[skillIndex] = cooldownDuration;
            OnCooldownStarted?.Invoke(skillIndex, cooldownDuration);
            OnSkillFired?.Invoke(skillIndex);
            Log($"Skill {skillIndex} confirmada pelo servidor. Cooldown: {cooldownDuration:0.0}s");
        }

        /// <summary>
        /// Chamado pelo NetworkPlayer via TargetRpc quando o servidor rejeita a skill.
        /// </summary>
        public void OnServerSkillRejected(int skillIndex, string reason)
        {
            UIManager.Instance?.ShowMessage(reason);
            Log($"Skill {skillIndex} rejeitada: {reason}");
        }

        // ── Helpers ────────────────────────────────────────────────────────

        private void Log(string msg)
        {
            if (debugLogs) Debug.Log($"[SkillSystem] {msg}");
        }
    }
}
