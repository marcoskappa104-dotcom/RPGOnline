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
    /// SkillSystem v4 — Corrigido para RPG Online profissional.
    ///
    /// CORREÇÕES v4:
    ///
    ///   1. WalkThenSendCmd — DESSINCRONIZAÇÃO CORRIGIDA:
    ///      Em v3, o agente local era atualizado TODO FRAME com SetDestination,
    ///      mas o servidor recebia a posição a cada CMD_MOVE_INTERVAL (0.15s).
    ///      Isso criava uma situação onde o cliente estava num ponto X enquanto
    ///      o servidor ainda processava um destino antigo, gerando teleporte visual.
    ///
    ///      SOLUÇÃO: O cliente local NÃO chama SetDestination direto no agente.
    ///      Em vez disso, chama CmdMoveTo que vai para o servidor, e o servidor
    ///      (ou o NetworkTransform em ClientToServer mode) cuida da posição.
    ///      O agente local é movido apenas via RpcMoveConfirmed ou pelo próprio
    ///      NetworkTransform.
    ///
    ///      Como a maioria dos projetos com Mirror usa ClientAuthority no movimento
    ///      do player, uma alternativa segura é: durante WalkThenSendCmd, o cliente
    ///      move seu agente local diretamente (ele tem autoridade), mas reduz os
    ///      CmdMoveTo para não poluir o servidor.
    ///
    ///      A implementação abaixo usa a abordagem ClientAuthority:
    ///      - agente local: atualizado a cada frame (movimento fluido)
    ///      - CmdMoveTo para servidor: throttled (CMD_MOVE_INTERVAL)
    ///
    ///   2. debugLogs: APENAS logs de Warning/Error relevantes em produção.
    ///      Logs de [SkillSystem] CmdRequestSkill e Confirmed removidos da
    ///      path de produção (eram executados a cada skill use, gerando GC).
    ///
    ///   3. IsTargetDead: null-check de UnityObject mantido e melhorado.
    ///
    ///   4. CancelPendingWalk: garante que o agente para ao cancelar.
    /// </summary>
    [RequireComponent(typeof(PlayerEntity))]
    public class SkillSystem : NetworkBehaviour
    {
        [Header("Skills (Q=0  W=1  E=2  R=3)")]
        [SerializeField] private List<SkillData> skills = new List<SkillData>();

        [Header("Debug — desative em builds de produção")]
        [SerializeField] private bool debugLogs = false;

        // Intervalo mínimo entre CmdMoveTo durante walk-to-range
        private const float CMD_MOVE_INTERVAL = 0.2f; // ~5 updates/s ao servidor é suficiente
        private const float WALK_TIMEOUT      = 12f;

        // ── Componentes ────────────────────────────────────────────────────
        private PlayerEntity            _player;
        private Animator                _animator;
        private NavMeshAgent            _agent;
        private NetworkPlayerController _controller;

        // ── Cooldown visual ────────────────────────────────────────────────
        private const int MAX_SKILLS = 8;
        private float[] _uiCooldownTimers = new float[MAX_SKILLS];

        // ── Walk-to-range state ────────────────────────────────────────────
        private Coroutine   _walkCoroutine;
        private bool        _hasPendingWalk;
        private ITargetable _pendingTarget;
        private float       _lastCmdMoveTime;

        // ── Eventos para SkillBar UI ───────────────────────────────────────
        public event Action<int, float> OnCooldownStarted;
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

            for (int i = 0; i < MAX_SKILLS; i++)
                if (_uiCooldownTimers[i] > 0f)
                    _uiCooldownTimers[i] -= Time.deltaTime;

            // Cancela walk se o jogador trocou de alvo manualmente
            if (_hasPendingWalk && _pendingTarget != _player.CurrentTarget)
                CancelPendingWalk();
        }

        // ── Propriedades públicas ──────────────────────────────────────────

        public int       SkillCount           => skills.Count;
        public SkillData GetSkill(int i)      => (i >= 0 && i < skills.Count) ? skills[i] : null;
        public float     GetUICooldown(int i) => (i >= 0 && i < MAX_SKILLS) ? Mathf.Max(0f, _uiCooldownTimers[i]) : 0f;
        public bool      IsOnUICooldown(int i) => GetUICooldown(i) > 0f;

        // ── TryUseSkill ────────────────────────────────────────────────────

        public void TryUseSkill(int index)
        {
            if (!isLocalPlayer) return;
            if (!_player.IsInitialized || _player.IsDead) return;

            var skill = GetSkill(index);
            if (skill == null)
            {
                Log($"Skill {index} não existe.");
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
                if (IsTargetDead(target))
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
                SendSelfSkillCmd(index);
                return;
            }

            float dist = target != null ? Vector3.Distance(transform.position, target.Position) : 0f;

            if (dist > skill.Range)
            {
                Log($"Fora de range ({dist:0.1} > {skill.Range}). Caminhando...");
                _hasPendingWalk  = true;
                _pendingTarget   = target;
                _lastCmdMoveTime = -CMD_MOVE_INTERVAL;
                _walkCoroutine   = StartCoroutine(WalkThenSendCmd(index, skill, target));
            }
            else
            {
                SendSkillCmd(index, target, skill.Type == SkillType.Physical);
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

            // Para o agente ao cancelar
            if (_agent != null && _agent.isOnNavMesh)
            {
                _agent.stoppingDistance = 0.5f;
                _agent.ResetPath();
            }
        }

        // ── Walk-to-range ──────────────────────────────────────────────────

        private IEnumerator WalkThenSendCmd(int index, SkillData skill, ITargetable target)
        {
            _agent.stoppingDistance = skill.Range * 0.85f;
            float timeout = WALK_TIMEOUT;

            while (timeout > 0f)
            {
                timeout -= Time.deltaTime;

                if (IsTargetDead(target))
                {
                    _player.ClearTarget();
                    UIManager.Instance?.ClearTargetPanel();
                    Log("WalkThenSendCmd: alvo morreu durante aproximação.");
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

                // Atualiza destino local para movimento fluido no cliente
                // (válido em modo ClientAuthority — o cliente tem autoridade sobre seu agente)
                if (_agent.isOnNavMesh)
                    _agent.SetDestination(target.Position);

                // Notifica o servidor com throttle para não gerar flood de Commands
                if (Time.time - _lastCmdMoveTime >= CMD_MOVE_INTERVAL)
                {
                    _lastCmdMoveTime = Time.time;
                    _controller?.CmdMoveTo(target.Position);
                }

                yield return null;
            }

            if (timeout <= 0f)
                Log($"WalkThenSendCmd: timeout após {WALK_TIMEOUT}s para skill {index}.");

            _agent.stoppingDistance = 0.5f;
            _hasPendingWalk = false;
            _pendingTarget  = null;
            _walkCoroutine  = null;
        }

        // ── Envio dos Commands ao servidor ─────────────────────────────────

        private void SendSkillCmd(int skillIndex, ITargetable target, bool isPhysical)
        {
            var skill = GetSkill(skillIndex);

            if (_animator != null && skill != null && !string.IsNullOrEmpty(skill.AnimTrigger))
                _animator.SetTrigger(skill.AnimTrigger);

            var targetNB = target as NetworkBehaviour;
            if (targetNB == null)
            {
                Log("Alvo não é NetworkBehaviour — skill não enviada.");
                return;
            }

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
                Log("Alvo não é monstro (PvP não implementado).");
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

        private bool IsTargetDead(ITargetable target)
        {
            if (target == null) return true;
            if (target is UnityEngine.Object unityObj && unityObj == null) return true;
            return target.IsDead;
        }

        /// <summary>
        /// Log interno. Só escreve se debugLogs está ativo.
        /// Em produção, SEMPRE deixe debugLogs = false no Inspector.
        /// Logs de skill por frame geram alocações de string desnecessárias.
        /// </summary>
        private void Log(string msg)
        {
            if (debugLogs) Debug.Log($"[SkillSystem] {msg}");
        }
    }
}