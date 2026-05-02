using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
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

    [RequireComponent(typeof(PlayerEntity))]
    public class SkillSystem : MonoBehaviour
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
            for (int i = 0; i < _cooldownTimers.Length; i++)
                if (_cooldownTimers[i] > 0) _cooldownTimers[i] -= Time.deltaTime;
        }

        public int       SkillCount      => skills.Count;
        public SkillData GetSkill(int i) => (i >= 0 && i < skills.Count) ? skills[i] : null;
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
                Log($"Skill {index} não existe. Configure no Inspector do SkillSystem.");
                return;
            }

            var target = _player.CurrentTarget;

            Log($"TryUseSkill({index}) '{skill.Name}' | Type:{skill.Type} Target:{skill.Target} | " +
                $"Alvo:{target?.DisplayName ?? "nenhum"} | " +
                $"CD:{GetCooldown(index):0.0} | MP:{_player.CurrentMP:0}/{_player.Stats?.MaxMP:0}");

            // ── Validações por tipo de alvo ────────────────────────────────

            if (skill.Target == SkillTarget.Enemy)
            {
                if (target == null)
                {
                    Log("Sem alvo selecionado.");
                    UIManager.Instance?.ShowMessage("Selecione um alvo primeiro!");
                    return;
                }
                if (target.IsDead)
                {
                    Log("Alvo está morto.");
                    UIManager.Instance?.ShowMessage("Alvo já está morto!");
                    _player.ClearTarget();
                    UIManager.Instance?.ClearTargetPanel();
                    return;
                }
            }

            if (IsOnCooldown(index))
            {
                Log($"Skill em cooldown: {GetCooldown(index):0.0}s restantes.");
                UIManager.Instance?.ShowMessage($"{skill.Name}: {GetCooldown(index):0.0}s");
                return;
            }

            if (_player.CurrentMP < skill.ManaCost)
            {
                Log($"MP insuficiente. Tem {_player.CurrentMP:0}, precisa {skill.ManaCost}.");
                UIManager.Instance?.ShowMessage("MP insuficiente!");
                return;
            }

            // ── Cancela ação anterior ─────────────────────────────────────
            CancelPendingAction();

            // Skills de Heal/Self não precisam de alvo nem de range check
            if (skill.Target == SkillTarget.Self || skill.Type == SkillType.Heal)
            {
                Log("Skill de cura/self — executando diretamente.");
                ExecuteSkill(index, skill, null);
                return;
            }

            // ── Verifica distância para skills de inimigo ─────────────────
            float dist = (target != null)
                ? Vector3.Distance(transform.position, target.Position)
                : 0f;

            Log($"Distância ao alvo: {dist:0.0} | Range da skill: {skill.Range}");

            if (dist > skill.Range)
            {
                Log("Fora de range. Iniciando walk-to-range.");
                _hasPendingAction = true;
                _pendingCoroutine = StartCoroutine(WalkThenFire(index, skill, target));
            }
            else
            {
                Log("Dentro do range. Executando skill diretamente.");
                ExecuteSkill(index, skill, target);
            }
        }

        public void CancelPendingAction()
        {
            if (_pendingCoroutine != null)
            {
                StopCoroutine(_pendingCoroutine);
                _pendingCoroutine = null;
                Log("Ação pendente cancelada.");
            }
            _hasPendingAction = false;
            if (_agent != null) _agent.stoppingDistance = 0.5f;
        }

        // ── Walk-to-range ─────────────────────────────────────────────────

        private IEnumerator WalkThenFire(int index, SkillData skill, ITargetable target)
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

                if (frames % 60 == 0)
                    Log($"Walk-to-range: dist={dist:0.0} range={skill.Range} timeout={timeout:0.0}");

                if (dist <= skill.Range)
                {
                    _agent.stoppingDistance = 0.5f;
                    _hasPendingAction = false;
                    Log($"Chegou no range! dist={dist:0.0} Executando skill.");
                    ExecuteSkill(index, skill, target);
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

        // ── Execução ──────────────────────────────────────────────────────

        private void ExecuteSkill(int index, SkillData skill, ITargetable target)
        {
            if (!_player.SpendMP(skill.ManaCost))
            {
                Log("Falha ao gastar MP.");
                return;
            }

            _cooldownTimers[index] = skill.Cooldown;
            OnCooldownStarted?.Invoke(index, skill.Cooldown);
            OnSkillFired?.Invoke(index);

            if (_agent != null) _agent.ResetPath();

            // Vira para o alvo (se tiver)
            if (target != null)
            {
                Vector3 dir = (target.Position - transform.position);
                dir.y = 0f;
                if (dir.sqrMagnitude > 0.01f)
                    transform.rotation = Quaternion.LookRotation(dir);
            }

            if (_animator != null && !string.IsNullOrEmpty(skill.AnimTrigger))
                _animator.SetTrigger(skill.AnimTrigger);

            if (skill.CastTime > 0f)
                StartCoroutine(CastAndFire(skill, target));
            else
                FireSkill(skill, target);
        }

        private IEnumerator CastAndFire(SkillData skill, ITargetable target)
        {
            float castSpeed = _player.Stats?.CastSpeed ?? 1f;
            float t = skill.CastTime / Mathf.Max(0.1f, castSpeed * 0.1f);
            Log($"Cast time: {t:0.0}s");
            yield return new WaitForSeconds(t);

            // Para curas/self sempre dispara; para dano verifica se alvo ainda é válido
            if (skill.Type == SkillType.Heal || skill.Target == SkillTarget.Self)
                FireSkill(skill, null);
            else if (target != null && !target.IsDead)
                FireSkill(skill, target);
        }

        // ── FireSkill — aqui cada tipo é tratado corretamente ─────────────

        private void FireSkill(SkillData skill, ITargetable target)
        {
            if (_player.Stats == null) { Log("FireSkill: Stats null."); return; }

            switch (skill.Type)
            {
                // ── CURA ──────────────────────────────────────────────────
                case SkillType.Heal:
                {
                    // Usa MATK como base da cura (INT-based); mínimo de 10
                    float healAmount = Mathf.Max(10f, _player.Stats.MATK * skill.AtkMultiplier);
                    _player.Heal(healAmount);
                    Log($"FireSkill '{skill.Name}' → Cura {healAmount:0} HP");
                    break;
                }

                // ── BUFF ──────────────────────────────────────────────────
                case SkillType.Buff:
                {
                    // Placeholder: expanda conforme adicionar buffs reais
                    Log($"FireSkill '{skill.Name}' → Buff aplicado (sem efeito por enquanto)");
                    UIManager.Instance?.ShowMessage($"{skill.Name} ativado!");
                    break;
                }

                // ── DANO FÍSICO / MÁGICO ──────────────────────────────────
                case SkillType.Physical:
                case SkillType.Magical:
                {
                    if (target == null || target.IsDead)
                    {
                        Log("FireSkill: alvo inválido para skill de dano.");
                        return;
                    }

                    float rawATK  = _player.Stats.ATK  * skill.AtkMultiplier;
                    float rawMATK = _player.Stats.MATK * skill.AtkMultiplier;
                    bool  isPhys  = skill.Type == SkillType.Physical;

                    Log($"FireSkill '{skill.Name}' → {target.DisplayName} | " +
                        $"RawATK:{rawATK:0} RawMATK:{rawMATK:0} Físico:{isPhys} Mult:{skill.AtkMultiplier} | " +
                        $"HP antes:{target.CurrentHP:0}");

                    target.TakeDamage(rawATK, rawMATK, isPhys);

                    Log($"HP depois:{target.CurrentHP:0}");
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