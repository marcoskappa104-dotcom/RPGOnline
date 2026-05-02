using UnityEngine;
using UnityEngine.AI;
using RPG.Data;
using RPG.Managers;
using RPG.UI;
using System;

namespace RPG.Character
{
    [RequireComponent(typeof(NavMeshAgent))]
    public class PlayerEntity : MonoBehaviour
    {
        public CharacterData Data        { get; private set; }
        public DerivedStats  Stats       { get; private set; }
        public BuffBonuses   ActiveBuffs { get; private set; } = new BuffBonuses();

        public float CurrentHP { get; private set; }
        public float CurrentMP { get; private set; }
        public bool  IsInitialized    => Data != null && Stats != null;
        public bool  ManagedByNetwork { get; set; } = false;

        // ── Eventos ───────────────────────────────────────────────────────
        public event Action<float, float> OnHPChanged;
        public event Action<float, float> OnMPChanged;
        public event Action<bool>         OnDeathChanged;
        public event Action               OnStatsChanged;

        /// <summary>
        /// Disparado UMA vez quando Initialize() conclui.
        /// UIManager assina isso para atualizar o HUD com os valores reais,
        /// resolvendo o bug de HP/MP mostrando 100/100 ao entrar no jogo.
        /// </summary>
        public event Action OnInitialized;

        private NavMeshAgent _agent;
        public  NavMeshAgent Agent => _agent;

        private bool  _isDead;
        private float _regenTimer;
        private const float REGEN_INTERVAL = 5f;

        public ITargetable CurrentTarget { get; private set; }

        private void Awake()
        {
            _agent = GetComponent<NavMeshAgent>();
        }

        private void Start()
        {
            if (ManagedByNetwork) return;
            var charData = GameManager.Instance?.SelectedCharacter;
            if (charData != null && !IsInitialized)
                Initialize(charData);
        }

        private void Update()
        {
            if (!IsInitialized || _isDead) return;
            HandleRegen();
        }

        // ── Inicialização ─────────────────────────────────────────────────

        public void Initialize(CharacterData data)
        {
            if (data == null)
            {
                Debug.LogWarning("[PlayerEntity] Initialize com data null — ignorado.");
                return;
            }

            Data = data;
            RefreshStats();

            CurrentHP = data.CurrentHP > 0 ? data.CurrentHP : Stats.MaxHP;
            CurrentMP = data.CurrentMP > 0 ? data.CurrentMP : Stats.MaxMP;

            if (_agent != null)
            {
                _agent.speed            = Mathf.Clamp(Stats.ASPD * 0.8f, 2f, 10f);
                _agent.stoppingDistance = 0.5f;
            }

            Debug.Log($"[PlayerEntity] {data.CharacterName} inicializado | " +
                      $"HP:{CurrentHP:0}/{Stats.MaxHP:0} | MP:{CurrentMP:0}/{Stats.MaxMP:0} | Lv:{data.Level}");

            // Notifica que os dados reais estão prontos — UIManager atualiza HUD aqui
            OnInitialized?.Invoke();

            // Força eventos de HP/MP para qualquer UI já vinculada
            OnHPChanged?.Invoke(CurrentHP, Stats.MaxHP);
            OnMPChanged?.Invoke(CurrentMP, Stats.MaxMP);
        }

        // ── Stats ─────────────────────────────────────────────────────────

        public void RefreshStats()
        {
            if (Data == null) return;
            Stats = Data.GetDerivedStats(ActiveBuffs);
            if (_agent != null)
                _agent.speed = Mathf.Clamp(Stats.ASPD * 0.8f, 2f, 10f);
            OnStatsChanged?.Invoke();
        }

        // ── Movimento ─────────────────────────────────────────────────────

        public void MoveTo(Vector3 destination)
        {
            if (_isDead || _agent == null) return;
            _agent.SetDestination(destination);
        }

        public void StopMovement() => _agent?.ResetPath();

        public bool HasReachedDestination()
        {
            if (_agent == null) return false;
            return !_agent.pathPending
                && _agent.remainingDistance <= _agent.stoppingDistance
                && (!_agent.hasPath || _agent.velocity.sqrMagnitude < 0.01f);
        }

        // ── Alvo ──────────────────────────────────────────────────────────

        public void SetTarget(ITargetable target)
        {
            CurrentTarget?.OnDeselected();
            CurrentTarget = target;
            CurrentTarget?.OnSelected();
        }

        public void ClearTarget()
        {
            CurrentTarget?.OnDeselected();
            CurrentTarget = null;
        }

        // ── Dano & Cura ───────────────────────────────────────────────────

        public void TakeDamage(float rawAtk, float rawMatk, bool isPhysical)
        {
            if (!IsInitialized || _isDead) return;

            bool hit = StatsCalculator.RollHit(100f, Stats.FLEE);
            if (!hit)
            {
                FloatingTextManager.Instance?.Show("MISS", transform.position + Vector3.up * 2f, Color.gray);
                return;
            }

            bool  crit = StatsCalculator.RollCrit(Stats.CRIT);
            float dmg  = isPhysical
                ? StatsCalculator.CalculatePhysicalDamage(rawAtk, Stats.DEF, crit, Stats.CritDMG)
                : StatsCalculator.CalculateMagicDamage(rawMatk, Stats.MDEF, crit, Stats.CritDMG);

            dmg *= 1f - (Stats.DamageReduction / 100f);
            dmg  = Mathf.Max(1f, dmg);

            CurrentHP = Mathf.Max(0f, CurrentHP - dmg);
            OnHPChanged?.Invoke(CurrentHP, Stats.MaxHP);

            Color color = crit ? Color.yellow : Color.red;
            FloatingTextManager.Instance?.Show(
                crit ? $"CRÍTICO!\n{dmg:0}" : $"{dmg:0}",
                transform.position + Vector3.up * 2f,
                color);

            if (CurrentHP <= 0f) Die();
        }

        public void Heal(float amount)
        {
            if (!IsInitialized || _isDead) return;
            float before = CurrentHP;
            CurrentHP    = Mathf.Min(Stats.MaxHP, CurrentHP + amount);
            float healed = CurrentHP - before;
            OnHPChanged?.Invoke(CurrentHP, Stats.MaxHP);
            if (healed > 0f)
                FloatingTextManager.Instance?.Show(
                    $"+{healed:0}", transform.position + Vector3.up * 2f, Color.green);
        }

        private void HealSilent(float amount)
        {
            if (!IsInitialized || _isDead) return;
            CurrentHP = Mathf.Min(Stats.MaxHP, CurrentHP + amount);
            OnHPChanged?.Invoke(CurrentHP, Stats.MaxHP);
        }

        public void RestoreMP(float amount)
        {
            if (!IsInitialized) return;
            CurrentMP = Mathf.Min(Stats.MaxMP, CurrentMP + amount);
            OnMPChanged?.Invoke(CurrentMP, Stats.MaxMP);
        }

        public bool SpendMP(float amount)
        {
            if (!IsInitialized || CurrentMP < amount) return false;
            CurrentMP -= amount;
            OnMPChanged?.Invoke(CurrentMP, Stats.MaxMP);
            return true;
        }

        public void HealToFull()
        {
            if (!IsInitialized) return;
            CurrentHP = Stats.MaxHP;
            CurrentMP = Stats.MaxMP;
            OnHPChanged?.Invoke(CurrentHP, Stats.MaxHP);
            OnMPChanged?.Invoke(CurrentMP, Stats.MaxMP);
        }

        public void ForceSetHP(float hp, float maxHp)
        {
            if (!IsInitialized) return;
            Stats.MaxHP = maxHp;
            CurrentHP   = Mathf.Clamp(hp, 0f, maxHp);
            OnHPChanged?.Invoke(CurrentHP, maxHp);
            if (CurrentHP <= 0f && !_isDead) Die();
        }

        // ── Morte ─────────────────────────────────────────────────────────

        private void Die()
        {
            if (_isDead) return;
            _isDead = true;
            _agent?.ResetPath();
            OnDeathChanged?.Invoke(true);
            Debug.Log($"[PlayerEntity] {Data?.CharacterName} morreu.");
        }

        public void OnNetworkDeath()
        {
            if (_isDead) return;
            _isDead   = true;
            CurrentHP = 0f;
            _agent?.ResetPath();
            OnHPChanged?.Invoke(0f, Stats?.MaxHP ?? 1f);
            OnDeathChanged?.Invoke(true);
            Debug.Log($"[PlayerEntity] {Data?.CharacterName} morte confirmada pelo servidor.");
        }

        public void Respawn(Vector3 position)
        {
            if (!IsInitialized) return;
            _isDead = false;
            transform.position = position;
            CurrentHP = Stats.MaxHP * 0.5f;
            CurrentMP = Stats.MaxMP * 0.5f;
            OnHPChanged?.Invoke(CurrentHP, Stats.MaxHP);
            OnMPChanged?.Invoke(CurrentMP, Stats.MaxMP);
            OnDeathChanged?.Invoke(false);
        }

        // ── Regen ─────────────────────────────────────────────────────────

        private void HandleRegen()
        {
            _regenTimer += Time.deltaTime;
            if (_regenTimer < REGEN_INTERVAL) return;
            _regenTimer = 0f;

            if (CurrentHP < Stats.MaxHP) HealSilent(Stats.HPRegen);
            if (CurrentMP < Stats.MaxMP)
            {
                CurrentMP = Mathf.Min(Stats.MaxMP, CurrentMP + Stats.MPRegen);
                OnMPChanged?.Invoke(CurrentMP, Stats.MaxMP);
            }
        }

        // ── Save ──────────────────────────────────────────────────────────

        public void SaveToData()
        {
            if (!IsInitialized) return;
            if (GameManager.Instance?.CurrentAccount == null) return;
            Data.CurrentHP = CurrentHP;
            Data.CurrentMP = CurrentMP;
            Data.PosX      = transform.position.x;
            Data.PosY      = transform.position.y;
            Data.PosZ      = transform.position.z;
            SaveManager.Instance?.SaveCharacter(GameManager.Instance.CurrentAccount, Data);
        }

        private void OnApplicationQuit() => SaveToData();
    }
}