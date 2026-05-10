using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using Mirror;
using RPG.Character;
using RPG.UI;
using RPG.Network;
using RPG.Data;

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
        /// <summary>
        /// Tempo base de cast em segundos. 0 = instantâneo.
        /// O tempo efetivo é reduzido pelo CastSpeed do personagem:
        ///   effectiveCastTime = CastTime / (1 + CastSpeed/100)
        /// </summary>
        public float       CastTime      = 0f;
        public string      AnimTrigger   = "Attack";
        public Sprite      Icon;
    }

    /// <summary>
    /// SkillSystem v10
    ///
    /// CORREÇÕES v10:
    ///
    ///   NOVO — CastTime agora é reduzido pelo CastSpeed do personagem:
    ///     Se uma skill tem CastTime=2.0s e o player tem CastSpeed=50,
    ///     effectiveCastTime = 2.0 / (1 + 50/100) = 2.0/1.5 = 1.33s.
    ///     O personagem fica parado durante o cast (canal), então o
    ///     WalkThenSendCmd aguarda o cast antes de continuar.
    ///
    ///   NOVO — CastBar UI notificada via evento OnCastStarted/OnCastProgress/OnCastFinished.
    ///     A UIManager pode se inscrever para exibir uma barra de cast.
    ///
    ///   Todas as correções v9 mantidas.
    /// </summary>
    [RequireComponent(typeof(PlayerEntity))]
    public class SkillSystem : NetworkBehaviour
    {
        [Header("Debug — desative em builds de produção")]
        [SerializeField] private bool debugLogs = false;

        private const float CMD_MOVE_INTERVAL = 0.15f;
        private const float WALK_TIMEOUT      = 15f;
        private const float WALK_DEST_FRACTION = 0.85f;
        private const float RANGE_CHECK_MARGIN = 1.05f;
        private const float WALK_STOP_DIST     = 0.2f;

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
        private Coroutine   _castCoroutine;
        private bool        _hasPendingWalk;
        private bool        _isCasting;
        private ITargetable _pendingTarget;
        private float       _lastCmdMoveTime;

        // ── Eventos para a UI ──────────────────────────────────────────────
        public event Action<int, float>  OnCooldownStarted;
        public event Action<int>         OnSkillFired;
        public event Action              OnSkillBarNeedsRefresh;
        /// <summary>Disparado quando um cast começa. Parâmetros: skillName, castDuration.</summary>
        public event Action<string, float> OnCastStarted;
        /// <summary>Disparado a cada frame durante o cast. Parâmetro: progresso 0-1.</summary>
        public event Action<float>         OnCastProgress;
        /// <summary>Disparado quando o cast termina (sucesso ou cancelamento).</summary>
        public event Action                OnCastFinished;

        public bool HasPendingAction => _hasPendingWalk || _isCasting;
        public bool IsCasting        => _isCasting;

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
            CancelCast();
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

            if ((_hasPendingWalk || _isCasting) && _player.IsDead)
            {
                CancelPendingWalk();
                CancelCast();
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
            if (_isCasting) return; // não interrompe cast em andamento

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

            // Skills de self/heal/buff não precisam de alvo e ignoram cast time de aproximação
            if (skill.Target == SkillTarget.Self || skill.Type == SkillType.Heal || skill.Type == SkillType.Buff)
            {
                StartCastAndSend(index, skill, null, isSelf: true);
                return;
            }

            float dist = target != null ? Vector3.Distance(transform.position, target.Position) : 0f;

            if (dist <= skill.Range * RANGE_CHECK_MARGIN)
            {
                // Já no range: para e começa o cast
                if (_agent != null && _agent.isOnNavMesh)
                {
                    _agent.ResetPath();
                    _agent.stoppingDistance = 0.5f;
                }
                StartCastAndSend(index, skill, target, isSelf: false);
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

        /// <summary>
        /// Inicia o canal de cast (se CastTime > 0) e envia o Command ao servidor.
        /// Se CastTime = 0, envia imediatamente.
        /// CastSpeed do personagem reduz o CastTime efetivo.
        /// </summary>
        private void StartCastAndSend(int index, SkillData skill, ITargetable target, bool isSelf)
        {
            // Calcula cast time efetivo considerando CastSpeed do personagem
            float effectiveCastTime = 0f;
            if (skill.CastTime > 0f && _player.Stats != null)
            {
                effectiveCastTime = StatsCalculator.CalculateEffectiveCastTime(
                    skill.CastTime, _player.Stats.CastSpeed);
            }

            if (effectiveCastTime <= 0.05f)
            {
                // Cast instantâneo
                if (isSelf)
                    SendSelfSkillCmd(index);
                else
                    SendSkillCmd(index, target, skill.Type == SkillType.Physical);
            }
            else
            {
                // Cast com tempo
                if (_castCoroutine != null) StopCoroutine(_castCoroutine);
                _castCoroutine = StartCoroutine(CastSequence(index, skill, target, isSelf, effectiveCastTime));
            }
        }

        /// <summary>
        /// Coroutine de canal de cast. Exibe progresso via eventos de UI.
        /// Se o player se mover ou o alvo morrer, o cast é cancelado.
        /// </summary>
        private IEnumerator CastSequence(int index, SkillData skill, ITargetable target, bool isSelf, float castTime)
        {
            _isCasting = true;
            OnCastStarted?.Invoke(skill.Name, castTime);

            // Para o agente durante o cast
            if (_agent != null && _agent.isOnNavMesh)
            {
                _agent.ResetPath();
                _agent.velocity = Vector3.zero;
            }

            if (!string.IsNullOrEmpty(skill.AnimTrigger))
                _animator?.SetTrigger("CastStart");

            float elapsed = 0f;
            while (elapsed < castTime)
            {
                elapsed += Time.deltaTime;

                // Cancela cast se morreu ou alvo inválido
                if (_player.IsDead)
                {
                    Log("Cast cancelado: player morreu.");
                    break;
                }
                if (!isSelf && !IsTargetValid(target))
                {
                    Log("Cast cancelado: alvo morreu.");
                    UIManager.Instance?.ShowMessage("Alvo inválido — cast cancelado.");
                    break;
                }

                OnCastProgress?.Invoke(elapsed / castTime);
                yield return null;
            }

            bool success = elapsed >= castTime && !_player.IsDead;
            success = success && (isSelf || IsTargetValid(target));

            _isCasting     = false;
            _castCoroutine = null;
            OnCastFinished?.Invoke();

            if (success)
            {
                if (isSelf)
                    SendSelfSkillCmd(index);
                else
                    SendSkillCmd(index, target, skill.Type == SkillType.Physical);
            }
        }

        public void CancelCast()
        {
            if (_castCoroutine != null)
            {
                StopCoroutine(_castCoroutine);
                _castCoroutine = null;
            }
            if (_isCasting)
            {
                _isCasting = false;
                OnCastFinished?.Invoke();
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

        private IEnumerator WalkThenSendCmd(int index, SkillData skill, ITargetable target)
        {
            if (_agent != null && _agent.isOnNavMesh)
                _agent.stoppingDistance = WALK_STOP_DIST;

            float timeout        = WALK_TIMEOUT;
            float effectiveRange = skill.Range * RANGE_CHECK_MARGIN;

            while (timeout > 0f)
            {
                timeout -= Time.deltaTime;

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

                if (dist <= effectiveRange)
                {
                    if (_agent != null && _agent.isOnNavMesh)
                    {
                        _agent.ResetPath();
                        _agent.stoppingDistance = 0.5f;
                        _agent.velocity         = Vector3.zero;
                    }

                    _hasPendingWalk = false;
                    _pendingTarget  = null;

                    yield return null;

                    if (!_player.IsDead && IsTargetValid(target) && _player.CurrentTarget == target)
                    {
                        Log($"No range ({dist:0.2}/{skill.Range:0.1}). Executando skill {index}.");
                        StartCastAndSend(index, skill, target, isSelf: false);
                    }

                    yield break;
                }

                if (_agent != null && _agent.isOnNavMesh)
                {
                    Vector3 destination = CalculateWalkDestination(target.Position, skill.Range);
                    _agent.SetDestination(destination);
                }

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

            if (_agent != null && _agent.isOnNavMesh)
            {
                _agent.stoppingDistance = 0.5f;
                _agent.ResetPath();
            }

            _hasPendingWalk = false;
            _pendingTarget  = null;
            _walkCoroutine  = null;
        }

        private Vector3 CalculateWalkDestination(Vector3 targetPos, float skillRange)
        {
            Vector3 toTarget = targetPos - transform.position;
            float dist = toTarget.magnitude;

            float safeStopDist = skillRange * WALK_DEST_FRACTION;

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
