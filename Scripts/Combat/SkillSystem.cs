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
    /// SkillSystem v2 — CLIENTE APENAS SOLICITA. SERVIDOR DECIDE E EXECUTA TUDO.
    ///
    /// CORREÇÕES v2:
    ///
    ///   1. WalkThenSendCmd agora usa NetworkPlayerController.CmdMoveTo para
    ///      informar o servidor do movimento durante walk-to-range.
    ///      Antes usava _agent.SetDestination direto → servidor não sabia da
    ///      movimentação → dessincronização de posição.
    ///
    ///   2. Cooldown visual inicia APENAS após confirmação do servidor
    ///      (RpcSkillConfirmed). Antes havia um cooldown local otimista que
    ///      podia desincronizar com o servidor.
    ///
    ///   3. Verificação de alvo morto no WalkThenSendCmd mais robusta:
    ///      verifica tanto IsDead quanto se o UnityObject é null.
    ///
    ///   4. Timeout do WalkThenSendCmd reduzido para 12 s (era 20 s).
    ///
    ///   5. Log condicional com category para facilitar debugging.
    /// </summary>
    [RequireComponent(typeof(PlayerEntity))]
    public class SkillSystem : NetworkBehaviour
    {
        [Header("Skills (Q=0  W=1  E=2  R=3)")]
        [SerializeField] private List<SkillData> skills = new List<SkillData>();

        [Header("Debug")]
        [SerializeField] private bool debugLogs = false;

        // ── Componentes ────────────────────────────────────────────────────
        private PlayerEntity             _player;
        private Animator                 _animator;
        private NavMeshAgent             _agent;
        private NetworkPlayerController  _controller;

        // ── Cooldown visual (UI only — não é autoridade de segurança) ──────
        private const int MAX_SKILLS = 8;
        private float[] _uiCooldownTimers = new float[MAX_SKILLS];

        // ── Walk-to-range state ────────────────────────────────────────────
        private Coroutine   _walkCoroutine;
        private bool        _hasPendingWalk;
        private ITargetable _pendingTarget;

        // ── Eventos para SkillBar UI ───────────────────────────────────────
        public event Action<int, float> OnCooldownStarted; // (índice, duração)
        public event Action<int>        OnSkillFired;

        public bool HasPendingAction => _hasPendingWalk;

        // ── Lifecycle ──────────────────────────────────────────────────────

        private void Awake()
        {
            _player     = GetComponent<PlayerEntity>();
            _animator   = GetComponentInChildren<Animator>();
            _agent      = GetComponent<NavMeshAgent>();
            _controller = GetComponent<NetworkPlayerController>();
        }

        private void Update()
        {
            if (!isLocalPlayer) return;

            // Decrementa timers visuais de cooldown
            for (int i = 0; i < MAX_SKILLS; i++)
                if (_uiCooldownTimers[i] > 0f)
                    _uiCooldownTimers[i] -= Time.deltaTime;

            // Cancela walk se alvo mudou externamente
            if (_hasPendingWalk && _pendingTarget != _player.CurrentTarget)
                CancelPendingWalk();
        }

        // ── Propriedades públicas ──────────────────────────────────────────

        public int       SkillCount             => skills.Count;
        public SkillData GetSkill(int i)        => (i >= 0 && i < skills.Count) ? skills[i] : null;
        public float     GetUICooldown(int i)   => (i >= 0 && i < MAX_SKILLS) ? Mathf.Max(0f, _uiCooldownTimers[i]) : 0f;
        public bool      IsOnUICooldown(int i)  => GetUICooldown(i) > 0f;

        // ── TryUseSkill — ponto de entrada do input ────────────────────────

        public void TryUseSkill(int index)
        {
            if (!isLocalPlayer) return;
            if (!_player.IsInitialized || _player.IsDead) return;

            var skill = GetSkill(index);
            if (skill == null) { Log($"Skill {index} não existe."); return; }

            // Verificação de cooldown local (UX apenas — servidor revalida)
            if (IsOnUICooldown(index))
            {
                UIManager.Instance?.ShowMessage($"{skill.Name}: aguarde {GetUICooldown(index):0.0}s");
                return;
            }

            var target = _player.CurrentTarget;

            // Validação de alvo (UX — servidor valida de verdade)
            if (skill.Target == SkillTarget.Enemy)
            {
                if (target == null)
                {
                    UIManager.Instance?.ShowMessage("Selecione um alvo primeiro!");
                    return;
                }
                if (IsTargetDead(target))
                {
                    UIManager.Instance?.ShowMessage("Alvo já está morto!");
                    _player.ClearTarget();
                    UIManager.Instance?.ClearTargetPanel();
                    return;
                }
            }

            CancelPendingWalk();

            // Self/Heal/Buff: envia diretamente ao servidor
            if (skill.Target == SkillTarget.Self || skill.Type == SkillType.Heal || skill.Type == SkillType.Buff)
            {
                SendSelfSkillCmd(index);
                return;
            }

            // Skill de dano: verifica range
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
                SendSkillCmd(index, target, skill.Type == SkillType.Physical);
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
            float timeout = 12f;

            while (timeout > 0f)
            {
                timeout -= Time.deltaTime;

                if (IsTargetDead(target))
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
                    SendSkillCmd(index, target, skill.Type == SkillType.Physical);
                    yield break;
                }

                // CORREÇÃO: usa CmdMoveTo para o servidor saber do movimento
                // Não chama SetDestination direto aqui — passa pelo controller
                if (_agent.isOnNavMesh)
                {
                    _agent.SetDestination(target.Position); // predição local
                    _controller?.CmdMoveTo(target.Position); // confirma no servidor
                }

                yield return null;
            }

            _agent.stoppingDistance = 0.5f;
            _hasPendingWalk = false;
            _pendingTarget  = null;
            _walkCoroutine  = null;
        }

        // ── Envio dos Commands ao servidor ─────────────────────────────────

        private void SendSkillCmd(int skillIndex, ITargetable target, bool isPhysical)
        {
            var skill = GetSkill(skillIndex);

            // Animação local (feedback visual imediato)
            if (_animator != null && skill != null && !string.IsNullOrEmpty(skill.AnimTrigger))
                _animator.SetTrigger(skill.AnimTrigger);

            var targetNB = target as NetworkBehaviour;
            if (targetNB == null)
            {
                Log($"Alvo não é NetworkBehaviour — skill não enviada.");
                return;
            }

            // Rotação visual local em direção ao alvo
            Vector3 dir = target.Position - transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude > 0.01f)
                transform.rotation = Quaternion.LookRotation(dir);

            uint attackerNetId = GetComponent<NetworkIdentity>().netId;

            var monster = targetNB.GetComponent<RPG.Network.NetworkMonsterEntity>();
            if (monster != null)
            {
                monster.CmdRequestSkill(attackerNetId, skillIndex, isPhysical);
                Log($"CmdRequestSkill → monstro {monster.DisplayName} skill:{skillIndex}");
            }
            else
            {
                // Placeholder para PvP futuro
                Log($"Alvo não é monstro (PvP não implementado).");
            }
        }

        private void SendSelfSkillCmd(int skillIndex)
        {
            var netPlayer = GetComponent<RPG.Network.NetworkPlayer>();
            netPlayer?.CmdRequestSelfSkill(skillIndex);
            Log($"CmdRequestSelfSkill skill:{skillIndex}");
        }

        // ── Resultado vindo do servidor ────────────────────────────────────

        /// <summary>
        /// Chamado pelo NetworkPlayer via RpcSkillConfirmed.
        /// Inicia o cooldown VISUAL após confirmação do servidor.
        /// </summary>
        public void OnServerSkillConfirmed(int skillIndex, float cooldownDuration)
        {
            if (skillIndex < 0 || skillIndex >= MAX_SKILLS) return;
            _uiCooldownTimers[skillIndex] = cooldownDuration;
            OnCooldownStarted?.Invoke(skillIndex, cooldownDuration);
            OnSkillFired?.Invoke(skillIndex);
            Log($"Skill {skillIndex} confirmada. Cooldown: {cooldownDuration:0.0}s");
        }

        /// <summary>
        /// Chamado pelo NetworkPlayer via RpcSkillRejected.
        /// </summary>
        public void OnServerSkillRejected(int skillIndex, string reason)
        {
            UIManager.Instance?.ShowMessage(reason);
            Log($"Skill {skillIndex} rejeitada: {reason}");
        }

        // ── Helpers ────────────────────────────────────────────────────────

        private bool IsTargetDead(ITargetable target)
        {
            if (target == null) return true;
            if (target is UnityEngine.Object unityObj && unityObj == null) return true;
            return target.IsDead;
        }

        private void Log(string msg)
        {
            if (debugLogs) Debug.Log($"[SkillSystem] {msg}");
        }
    }
}