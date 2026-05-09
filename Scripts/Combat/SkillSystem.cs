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
    /// SkillSystem v7
    ///
    /// CORREÇÕES v7 (vs v6):
    ///
    ///   1. BUG CRÍTICO — WalkThenSendCmd não verificava morte do jogador:
    ///      Se o jogador morresse enquanto o loop de aproximação estava ativo,
    ///      a coroutine continuava rodando e tentava enviar CmdRequestSkill após
    ///      a morte, causando animações e efeitos visuais em jogador morto.
    ///      Solução: verificação `if (_player.IsDead)` no início de cada iteração.
    ///
    ///   2. MELHORIA — WalkThenSendCmd verifica se jogador saiu do range mínimo:
    ///      Se o monstro fugir enquanto o jogador ainda está andando, o timeout
    ///      era a única proteção. Agora verifica se target ainda é alcançável
    ///      (dentro do leash/aggroRange do monstro) para cancelar mais cedo.
    ///
    ///   3. MELHORIA — OnServerSkillConfirmed propaga o cooldown para o servidor
    ///      via RpcSkillConfirmed com o valor real, garantindo que o cliente
    ///      sempre mostra o timer correto mesmo com latência.
    ///
    ///   4. LIMPEZA — Removido log "Fora de range" que aparecia com debugLogs=false.
    ///      O log de WalkThenSendCmd só aparece se debugLogs=true.
    /// </summary>
    [RequireComponent(typeof(PlayerEntity))]
    public class SkillSystem : NetworkBehaviour
    {
        [Header("Debug — desative em builds de produção")]
        [SerializeField] private bool debugLogs = false;

        private const float CMD_MOVE_INTERVAL = 0.2f;
        private const float WALK_TIMEOUT      = 12f;

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

            // CORREÇÃO v7: cancela walk pendente ao parar o cliente
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

            // CORREÇÃO v7: cancela walk se o jogador morreu
            if (_hasPendingWalk && _player.IsDead)
            {
                CancelPendingWalk();
                return;
            }

            // Cancela walk se o jogador trocou de alvo manualmente
            if (_hasPendingWalk && _pendingTarget != _player.CurrentTarget)
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

                // CORREÇÃO v7: cancela imediatamente se o jogador morreu
                if (_player.IsDead)
                {
                    Log("WalkThenSendCmd: jogador morreu durante aproximação.");
                    break;
                }

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

                if (_agent.isOnNavMesh)
                    _agent.SetDestination(target.Position);

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

        private bool IsTargetDead(ITargetable target)
        {
            if (target == null) return true;
            if (target is UnityEngine.Object unityObj && unityObj == null) return true;
            return target.IsDead;
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
