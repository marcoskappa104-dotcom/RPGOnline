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
    /// SkillSystem v2 — CLIENTE SOLICITA, SERVIDOR EXECUTA
    ///
    /// ARQUITETURA DE SEGURANÇA:
    ///
    ///   ANTES (vulnerável):
    ///     Cliente calculava hit + dano → chamava CmdRequestTakeDamage(rawAtk, rawMatk)
    ///     → servidor apenas aplicava o dano recebido sem validação.
    ///     Qualquer cliente podia enviar rawAtk=999999.
    ///
    ///   AGORA (seguro):
    ///     1. Cliente verifica apenas validações LOCAIS:
    ///        - Tem alvo? Alvo está morto? Skill no cooldown? Tem MP?
    ///        - Está no range? Se não, move até entrar.
    ///     2. Ao entrar no range, envia CmdRequestAttack(targetNetId, skillIndex, isPhysical).
    ///     3. O SERVIDOR (NetworkMonsterEntity.CmdRequestAttack) recebe e:
    ///        - Valida se o atacante existe e não está morto.
    ///        - Busca os stats do atacante via NetworkPlayer.ServerStats (calculados no servidor).
    ///        - Executa o cálculo completo de hit/crit/dano.
    ///        - Aplica o dano via _currentHP.
    ///        - Emite RpcShowDamage para feedback visual em todos os clientes.
    ///
    ///   SKILLS DE CURA/BUFF:
    ///     Ainda executadas localmente no cliente (afetam apenas o próprio jogador).
    ///     Em uma versão futura, curas também devem passar pelo servidor.
    ///
    ///   MP:
    ///     Gasto localmente para feedback imediato (sem esperar RTT).
    ///     O servidor não valida MP nesta versão — adição futura recomendada.
    ///     Para validar: SyncVar CurrentMP no NetworkPlayer + CmdSpendMP().
    /// </summary>
    [RequireComponent(typeof(PlayerEntity))]
    public class SkillSystem : NetworkBehaviour
    {
        [Header("Skills (Q=0 W=1 E=2 R=3)")]
        [SerializeField] private List<SkillData> skills = new List<SkillData>();

        [Header("Debug")]
        [SerializeField] private bool debugLogs = true;

        private PlayerEntity _player;
        private Animator     _animator;
        private NavMeshAgent _agent;

        private float[]   _cooldownTimers  = new float[8];
        private Coroutine _pendingCoroutine;
        private bool      _hasPendingAction;

        public event Action<int, float> OnCooldownStarted;
        public event Action<int>        OnSkillFired;
        public bool HasPendingAction => _hasPendingAction;

        private void Awake()
        {
            _player   = GetComponent<PlayerEntity>();
            _animator = GetComponentInChildren<Animator>();
            _agent    = GetComponent<NavMeshAgent>();
        }

        private void Update()
        {
            // Atualiza cooldowns apenas no cliente local
            if (!isLocalPlayer) return;

            for (int i = 0; i < _cooldownTimers.Length; i++)
                if (_cooldownTimers[i] > 0) _cooldownTimers[i] -= Time.deltaTime;
        }

        public int       SkillCount         => skills.Count;
        public SkillData GetSkill(int i)    => (i >= 0 && i < skills.Count) ? skills[i] : null;
        public float     GetCooldown(int i) => (i >= 0 && i < _cooldownTimers.Length)
                                                ? Mathf.Max(0f, _cooldownTimers[i]) : 0f;
        public bool      IsOnCooldown(int i) => GetCooldown(i) > 0f;

        public void TryUseSkill(int index)
        {
            if (!_player.IsInitialized)
            {
                Log("Player não inicializado ainda.");
                return;
            }

            var skill = GetSkill(index);
            if (skill == null)
            {
                Log($"Skill {index} não existe.");
                return;
            }

            var target = _player.CurrentTarget;

            // ── Validações locais (feedback imediato, sem RTT) ─────────────

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

            // Skills de auto-alvo (heal/buff) executadas localmente
            if (skill.Target == SkillTarget.Self || skill.Type == SkillType.Heal)
            {
                ExecuteSelfSkill(index, skill);
                return;
            }

            // Skills de dano/inimigo — verifica range
            float dist = (target != null)
                ? Vector3.Distance(transform.position, target.Position)
                : 0f;

            if (dist > skill.Range)
            {
                Log($"Fora de range ({dist:0.1} > {skill.Range}). Movendo até o alvo.");
                _hasPendingAction = true;
                _pendingCoroutine = StartCoroutine(WalkThenRequestAttack(index, skill, target));
            }
            else
            {
                RequestAttackOnServer(index, skill, target);
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
            if (_agent != null) _agent.stoppingDistance = 0.5f;
        }

        // ── Walk-to-range ─────────────────────────────────────────────────

        private IEnumerator WalkThenRequestAttack(int index, SkillData skill, ITargetable target)
        {
            _agent.stoppingDistance = skill.Range * 0.85f;
            float timeout = 20f;
            int   frames  = 0;

            while (timeout > 0f)
            {
                timeout -= Time.deltaTime;
                frames++;

                if (target == null || target.IsDead)
                {
                    Log("Alvo morreu durante walk-to-range.");
                    _player.ClearTarget();
                    UIManager.Instance?.ClearTargetPanel();
                    break;
                }

                float dist = Vector3.Distance(transform.position, target.Position);

                if (dist <= skill.Range)
                {
                    _agent.stoppingDistance = 0.5f;
                    _hasPendingAction = false;
                    Log($"Chegou no range. Enviando requisição ao servidor.");
                    RequestAttackOnServer(index, skill, target);
                    yield break;
                }

                _player.MoveTo(target.Position);
                yield return null;
            }

            Log("Walk-to-range: timeout ou alvo morreu.");
            _agent.stoppingDistance = 0.5f;
            _hasPendingAction = false;
            _pendingCoroutine = null;
        }

        // ── Requisição ao servidor ────────────────────────────────────────

        /// <summary>
        /// Envia a requisição de ataque ao servidor.
        /// O cliente aplica feedback local imediato (MP, cooldown, animação)
        /// mas o DANO real é calculado e aplicado pelo servidor.
        /// </summary>
        private void RequestAttackOnServer(int index, SkillData skill, ITargetable target)
        {
            // Gasta MP localmente (feedback imediato)
            if (!_player.SpendMP(skill.ManaCost))
            {
                Log("Falha ao gastar MP.");
                return;
            }

            // Inicia cooldown localmente
            _cooldownTimers[index] = skill.Cooldown;
            OnCooldownStarted?.Invoke(index, skill.Cooldown);
            OnSkillFired?.Invoke(index);

            // Para movimento e vira para o alvo
            if (_agent != null) _agent.ResetPath();
            if (target != null)
            {
                Vector3 dir = (target.Position - transform.position);
                dir.y = 0f;
                if (dir.sqrMagnitude > 0.01f)
                    transform.rotation = Quaternion.LookRotation(dir);
            }

            // Animação local
            if (_animator != null && !string.IsNullOrEmpty(skill.AnimTrigger))
                _animator.SetTrigger(skill.AnimTrigger);

            // Resolve o netId do alvo
            // NetworkMonsterEntity implementa ITargetable, então podemos pegar o NetworkBehaviour
            var targetNB = (target as NetworkBehaviour);
            if (targetNB == null)
            {
                Log("Alvo não é um NetworkBehaviour — inválido para ataque online.");
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
            // Encontra o NetworkMonsterEntity pelo netId e chama o Command
            // O Command está no NetworkMonsterEntity — precisamos do objeto alvo
            if (NetworkClient.spawned.TryGetValue(targetNetId, out var identity))
            {
                var monster = identity.GetComponent<RPG.Network.NetworkMonsterEntity>();
                if (monster != null)
                {
                    // netId do atacante (este player)
                    uint attackerNetId = GetComponent<NetworkIdentity>().netId;
                    monster.CmdRequestAttack(attackerNetId, skillIndex, isPhysical);
                    Log($"CmdRequestAttack enviado → monstro netId:{targetNetId} | skill:{skillIndex}");
                }
                else
                {
                    Log("Alvo não é NetworkMonsterEntity.");
                }
            }
            else
            {
                Log($"Alvo netId:{targetNetId} não encontrado nos spawned objects.");
            }
        }

        // ── Skills de self/heal (executadas localmente) ───────────────────

        private void ExecuteSelfSkill(int index, SkillData skill)
        {
            if (!_player.SpendMP(skill.ManaCost)) return;

            _cooldownTimers[index] = skill.Cooldown;
            OnCooldownStarted?.Invoke(index, skill.Cooldown);
            OnSkillFired?.Invoke(index);

            if (_agent != null) _agent.ResetPath();
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
                {
                    Log($"ExecuteSelfSkill '{skill.Name}' → Buff aplicado (placeholder)");
                    UIManager.Instance?.ShowMessage($"{skill.Name} ativado!");
                    break;
                }
            }
        }

        private void Log(string msg)
        {
            if (debugLogs) Debug.Log($"[SkillSystem] {msg}");
        }
    }
}