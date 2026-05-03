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
    /// SkillSystem — CLIENTE SOLICITA, SERVIDOR EXECUTA.
    ///
    /// ARQUITETURA DE SEGURANÇA:
    ///   1. Cliente valida apenas condições locais (alvo, cooldown, MP, range).
    ///   2. MP e cooldown são gastos localmente para feedback imediato (UX),
    ///      mas o servidor é autoridade no dano e em resultados reais.
    ///   3. CmdRequestAttack envia apenas (netId do alvo, índice da skill, tipo).
    ///      NENHUM valor de dano sai do cliente.
    ///   4. Walk-to-range é cancelado corretamente ao trocar de alvo ou ao morrer.
    ///
    /// FUTURO:
    ///   - Validar MP no servidor via SyncVar CurrentMP no NetworkPlayer.
    ///   - Implementar server-side cooldown para anti-hack completo.
    /// </summary>
    [RequireComponent(typeof(PlayerEntity))]
    public class SkillSystem : NetworkBehaviour
    {
        [Header("Skills (Q=0  W=1  E=2  R=3)")]
        [SerializeField] private List<SkillData> skills = new List<SkillData>();

        [Header("Debug")]
        [SerializeField] private bool debugLogs = true;

        // ── Componentes ───────────────────────────────────────────────────
        private PlayerEntity _player;
        private Animator     _animator;
        private NavMeshAgent _agent;

        // ── Estado de cooldown ─────────────────────────────────────────────
        private const int MAX_SKILLS = 8;
        private float[] _cooldownTimers = new float[MAX_SKILLS];

        // ── Estado de ação pendente ────────────────────────────────────────
        private Coroutine  _pendingCoroutine;
        private bool       _hasPendingAction;
        private ITargetable _pendingTarget;   // guarda o alvo para detectar troca

        // ── Eventos ────────────────────────────────────────────────────────
        public event Action<int, float> OnCooldownStarted;
        public event Action<int>        OnSkillFired;

        public bool HasPendingAction => _hasPendingAction;

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

            for (int i = 0; i < MAX_SKILLS; i++)
                if (_cooldownTimers[i] > 0f)
                    _cooldownTimers[i] -= Time.deltaTime;

            // Cancela ação pendente se o alvo mudou externamente
            if (_hasPendingAction && _pendingTarget != _player.CurrentTarget)
                CancelPendingAction();
        }

        // ── Propriedades ───────────────────────────────────────────────────

        public int       SkillCount          => skills.Count;
        public SkillData GetSkill(int i)     => (i >= 0 && i < skills.Count) ? skills[i] : null;
        public float     GetCooldown(int i)  => (i >= 0 && i < MAX_SKILLS) ? Mathf.Max(0f, _cooldownTimers[i]) : 0f;
        public bool      IsOnCooldown(int i) => GetCooldown(i) > 0f;

        // ── TryUseSkill ────────────────────────────────────────────────────

        public void TryUseSkill(int index)
        {
            if (!_player.IsInitialized)
            {
                Log("Player não inicializado.");
                return;
            }

            if (_player.IsDead)
            {
                Log("Player está morto.");
                return;
            }

            var skill = GetSkill(index);
            if (skill == null)
            {
                Log($"Skill {index} não existe.");
                return;
            }

            var target = _player.CurrentTarget;

            // ── Validações locais ──────────────────────────────────────────

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

            if (IsOnCooldown(index))
            {
                UIManager.Instance?.ShowMessage($"{skill.Name}: {GetCooldown(index):0.0}s");
                return;
            }

            if (_player.CurrentMP < skill.ManaCost)
            {
                UIManager.Instance?.ShowMessage("MP insuficiente!");
                return;
            }

            CancelPendingAction();

            // ── Skills de self / heal — executadas localmente ──────────────
            if (skill.Target == SkillTarget.Self || skill.Type == SkillType.Heal)
            {
                ExecuteSelfSkill(index, skill);
                return;
            }

            // ── Skills de dano — verifica range ────────────────────────────
            float dist = target != null
                ? Vector3.Distance(transform.position, target.Position) : 0f;

            if (dist > skill.Range)
            {
                Log($"Fora de range ({dist:0.1} > {skill.Range}). Movendo até o alvo.");
                _hasPendingAction = true;
                _pendingTarget    = target;
                _pendingCoroutine = StartCoroutine(WalkThenRequestAttack(index, skill, target));
            }
            else
            {
                ExecuteAttackLocally(index, skill, target);
            }
        }

        public void CancelPendingAction()
        {
            if (_pendingCoroutine != null)
            {
                StopCoroutine(_pendingCoroutine);
                _pendingCoroutine = null;
            }
            _hasPendingAction = false;
            _pendingTarget    = null;
            if (_agent != null) _agent.stoppingDistance = 0.5f;
        }

        // ── Walk-to-range ──────────────────────────────────────────────────

        private IEnumerator WalkThenRequestAttack(int index, SkillData skill, ITargetable target)
        {
            _agent.stoppingDistance = skill.Range * 0.85f;
            float timeout = 20f;

            while (timeout > 0f)
            {
                timeout -= Time.deltaTime;

                if (target == null || target.IsDead)
                {
                    Log("Alvo morreu durante walk-to-range.");
                    _player.ClearTarget();
                    UIManager.Instance?.ClearTargetPanel();
                    break;
                }

                // Alvo foi trocado externamente
                if (_player.CurrentTarget != target)
                {
                    Log("Alvo mudou durante walk-to-range — cancelando.");
                    break;
                }

                float dist = Vector3.Distance(transform.position, target.Position);

                if (dist <= skill.Range)
                {
                    _agent.stoppingDistance = 0.5f;
                    _hasPendingAction       = false;
                    _pendingTarget          = null;
                    Log("Chegou no range. Enviando requisição ao servidor.");
                    ExecuteAttackLocally(index, skill, target);
                    yield break;
                }

                _player.MoveTo(target.Position);
                yield return null;
            }

            // Timeout ou cancelamento
            _agent.stoppingDistance = 0.5f;
            _hasPendingAction       = false;
            _pendingTarget          = null;
            _pendingCoroutine       = null;
        }

        // ── Execução local (feedback) + envio ao servidor ──────────────────

        /// <summary>
        /// Aplica feedback local imediato (MP, cooldown, animação)
        /// e envia o Command ao servidor.
        /// </summary>
        private void ExecuteAttackLocally(int index, SkillData skill, ITargetable target)
        {
            // Gasta MP localmente
            if (!_player.SpendMP(skill.ManaCost))
            {
                Log("Falha ao gastar MP.");
                return;
            }

            // Inicia cooldown local
            _cooldownTimers[index] = skill.Cooldown;
            OnCooldownStarted?.Invoke(index, skill.Cooldown);
            OnSkillFired?.Invoke(index);

            // Para movimento e vira para o alvo
            _agent?.ResetPath();
            if (target != null)
            {
                Vector3 dir = target.Position - transform.position;
                dir.y = 0f;
                if (dir.sqrMagnitude > 0.01f)
                    transform.rotation = Quaternion.LookRotation(dir);
            }

            // Animação
            if (_animator != null && !string.IsNullOrEmpty(skill.AnimTrigger))
                _animator.SetTrigger(skill.AnimTrigger);

            // Resolve netId do alvo (deve ser NetworkBehaviour)
            var targetNB = target as NetworkBehaviour;
            if (targetNB == null)
            {
                Log("Alvo não é NetworkBehaviour — inválido para ataque online.");
                return;
            }

            uint targetNetId = targetNB.netId;
            bool isPhysical  = skill.Type == SkillType.Physical;

            if (skill.CastTime > 0f)
                StartCoroutine(CastThenSendCmd(skill, targetNetId, index, isPhysical));
            else
                SendAttackCmd(targetNetId, index, isPhysical);
        }

        private IEnumerator CastThenSendCmd(SkillData skill, uint targetNetId, int skillIndex, bool isPhysical)
        {
            float castSpeed = _player.Stats?.CastSpeed ?? 1f;
            float castTime  = skill.CastTime / Mathf.Max(0.1f, castSpeed * 0.1f);
            Log($"Cast time: {castTime:0.0}s");
            yield return new WaitForSeconds(castTime);
            SendAttackCmd(targetNetId, skillIndex, isPhysical);
        }

        private void SendAttackCmd(uint targetNetId, int skillIndex, bool isPhysical)
        {
            if (!NetworkClient.spawned.TryGetValue(targetNetId, out var identity))
            {
                Log($"Alvo netId:{targetNetId} não encontrado nos spawned objects.");
                return;
            }

            var monster = identity.GetComponent<RPG.Network.NetworkMonsterEntity>();
            if (monster == null)
            {
                Log("Alvo não é NetworkMonsterEntity.");
                return;
            }

            uint attackerNetId = GetComponent<NetworkIdentity>().netId;
            monster.CmdRequestAttack(attackerNetId, skillIndex, isPhysical);
            Log($"CmdRequestAttack enviado → monstro netId:{targetNetId} | skill:{skillIndex}");
        }

        // ── Skills de self/heal ────────────────────────────────────────────

        private void ExecuteSelfSkill(int index, SkillData skill)
        {
            if (!_player.SpendMP(skill.ManaCost)) return;

            _cooldownTimers[index] = skill.Cooldown;
            OnCooldownStarted?.Invoke(index, skill.Cooldown);
            OnSkillFired?.Invoke(index);

            _agent?.ResetPath();
            if (_animator != null && !string.IsNullOrEmpty(skill.AnimTrigger))
                _animator.SetTrigger(skill.AnimTrigger);

            switch (skill.Type)
            {
                case SkillType.Heal:
                {
                    float healAmount = Mathf.Max(10f, (_player.Stats?.MATK ?? 10f) * skill.AtkMultiplier);
                    _player.Heal(healAmount);
                    Log($"ExecuteSelfSkill '{skill.Name}' → Cura {healAmount:0} HP");
                    break;
                }
                case SkillType.Buff:
                    Log($"ExecuteSelfSkill '{skill.Name}' → Buff (placeholder)");
                    UIManager.Instance?.ShowMessage($"{skill.Name} ativado!");
                    break;
            }
        }

        // ── Helpers ────────────────────────────────────────────────────────

        private void Log(string msg)
        {
            if (debugLogs) Debug.Log($"[SkillSystem] {msg}");
        }
    }
}