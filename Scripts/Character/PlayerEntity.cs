using UnityEngine;
using UnityEngine.AI;
using RPG.Data;
using RPG.Managers;
using RPG.UI;
using System;
using System.Collections.Generic;

namespace RPG.Character
{
    /// <summary>
    /// PlayerEntity — representação local do personagem no cliente.
    ///
    /// RESPONSABILIDADES:
    ///   - Movimento e animação via NavMeshAgent.
    ///   - HP/MP mantidos localmente, sincronizados via NetworkPlayer (SyncVar hooks).
    ///
    /// REGRAS EM MODO MULTIPLAYER (ManagedByNetwork = true):
    ///   - HP/MP só mudam via SetHPFromNetwork / SetMPFromNetwork.
    ///   - TakeDamage() local é ignorado.
    ///   - SaveToData() é ignorado (servidor salva).
    ///   - Regen é ignorada (servidor controla).
    ///
    /// REGRAS EM MODO OFFLINE (ManagedByNetwork = false):
    ///   - TakeDamage(), Heal(), regen e SaveToData() funcionam normalmente.
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    public class PlayerEntity : MonoBehaviour
    {
        // ── Registro estático ──────────────────────────────────────────────
        public static readonly HashSet<PlayerEntity> All = new HashSet<PlayerEntity>();

        // ── Propriedades públicas ──────────────────────────────────────────
        public CharacterData Data        { get; private set; }
        public DerivedStats  Stats       { get; private set; }
        public BuffBonuses   ActiveBuffs { get; private set; } = new BuffBonuses();

        public float CurrentHP { get; private set; }
        public float CurrentMP { get; private set; }

        public bool IsInitialized    => Data != null && Stats != null;
        public bool ManagedByNetwork { get; set; } = false;

        // ── Eventos ───────────────────────────────────────────────────────
        public event Action<float, float> OnHPChanged;
        public event Action<float, float> OnMPChanged;
        public event Action<bool>         OnDeathChanged;
        public event Action               OnStatsChanged;
        public event Action               OnInitialized;

        // ── Componentes ───────────────────────────────────────────────────
        private NavMeshAgent _agent;
        public  NavMeshAgent Agent => _agent;

        // ── Estado interno ─────────────────────────────────────────────────
        private bool  _isDead;
        private float _regenTimer;
        private const float REGEN_INTERVAL = 5f;

        public bool         IsDead        => _isDead;
        public ITargetable  CurrentTarget { get; private set; }

        // ── Lifecycle ──────────────────────────────────────────────────────
        private void OnEnable()  => All.Add(this);
        private void OnDisable() => All.Remove(this);

        private void Awake()
        {
            _agent = GetComponent<NavMeshAgent>();
        }

        private void Start()
        {
            // Em modo online, inicialização vem via RpcInitializeLocalPlayer.
            if (ManagedByNetwork) return;

            var charData = GameManager.Instance?.SelectedCharacter;
            if (charData != null && !IsInitialized)
                Initialize(charData);
        }

        private void Update()
        {
            if (!IsInitialized || _isDead) return;
            if (!ManagedByNetwork)
                HandleRegen();
        }

        // ── Inicialização ──────────────────────────────────────────────────

        public void Initialize(CharacterData data)
        {
            if (data == null)
            {
                Debug.LogWarning("[PlayerEntity] Initialize chamado com data null — ignorado.");
                return;
            }

            Data = data;
            RefreshStats();

            // HP/MP: usa o valor salvo se válido, caso contrário começa cheio
            CurrentHP = (data.CurrentHP > 0f && data.CurrentHP <= Stats.MaxHP)
                ? data.CurrentHP : Stats.MaxHP;
            CurrentMP = (data.CurrentMP > 0f && data.CurrentMP <= Stats.MaxMP)
                ? data.CurrentMP : Stats.MaxMP;

            _isDead = CurrentHP <= 0f;

            ConfigureAgent();

            Debug.Log($"[PlayerEntity] {data.CharacterName} inicializado | " +
                      $"HP:{CurrentHP:0}/{Stats.MaxHP:0} | MP:{CurrentMP:0}/{Stats.MaxMP:0} | Lv:{data.Level}");

            OnInitialized?.Invoke();
            OnHPChanged?.Invoke(CurrentHP, Stats.MaxHP);
            OnMPChanged?.Invoke(CurrentMP, Stats.MaxMP);
        }

        // ── Stats ──────────────────────────────────────────────────────────

        public void RefreshStats()
        {
            if (Data == null) return;

            // GetDerivedStats NÃO modifica Data (sem side-effects)
            Stats = Data.GetDerivedStats(ActiveBuffs);

            ConfigureAgent();

            // Clamp: HP/MP nunca excedem o novo máximo
            if (IsInitialized)
            {
                CurrentHP = Mathf.Min(CurrentHP, Stats.MaxHP);
                CurrentMP = Mathf.Min(CurrentMP, Stats.MaxMP);
            }

            OnStatsChanged?.Invoke();
            OnHPChanged?.Invoke(CurrentHP, Stats.MaxHP);
            OnMPChanged?.Invoke(CurrentMP, Stats.MaxMP);
        }

        private void ConfigureAgent()
        {
            if (_agent == null || Stats == null) return;
            _agent.speed            = Mathf.Clamp(Stats.MoveSpeed, 2f, 10f);
            _agent.stoppingDistance = 0.5f;
        }

        // ── Sincronização de rede ──────────────────────────────────────────

        /// <summary>
        /// Chamado exclusivamente pelo hook OnHPChanged/OnMaxHPChanged do NetworkPlayer.
        /// Única forma de alterar HP em modo multiplayer.
        /// </summary>
        public void SetHPFromNetwork(float hp, float maxHp)
        {
            if (!IsInitialized) return;

            bool wasDead = _isDead;

            Stats.MaxHP = maxHp;
            CurrentHP   = Mathf.Clamp(hp, 0f, maxHp);

            OnHPChanged?.Invoke(CurrentHP, maxHp);

            bool nowDead = CurrentHP <= 0f;
            if (nowDead != wasDead)
            {
                _isDead = nowDead;
                if (_isDead) _agent?.ResetPath();
                OnDeathChanged?.Invoke(_isDead);
            }
        }

        /// <summary>
        /// Chamado exclusivamente pelo hook OnMPChanged/OnMaxMPChanged do NetworkPlayer.
        /// </summary>
        public void SetMPFromNetwork(float mp, float maxMp)
        {
            if (!IsInitialized) return;
            Stats.MaxMP = maxMp;
            CurrentMP   = Mathf.Clamp(mp, 0f, maxMp);
            OnMPChanged?.Invoke(CurrentMP, maxMp);
        }

        // ── Movimento ──────────────────────────────────────────────────────

        public void MoveTo(Vector3 destination)
        {
            if (_isDead || _agent == null) return;
            _agent.SetDestination(destination);
        }

        public void StopMovement() => _agent?.ResetPath();

        public bool HasReachedDestination()
        {
            if (_agent == null) return true;
            return !_agent.pathPending
                && _agent.remainingDistance <= _agent.stoppingDistance
                && (!_agent.hasPath || _agent.velocity.sqrMagnitude < 0.01f);
        }

        // ── Alvo ───────────────────────────────────────────────────────────

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

        // ── Dano & Cura (MODO OFFLINE apenas) ─────────────────────────────

        public void TakeDamage(float rawAtk, float rawMatk, bool isPhysical)
        {
            if (!IsInitialized || _isDead || ManagedByNetwork) return;

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
                transform.position + Vector3.up * 2f, color);

            if (CurrentHP <= 0f) DieLocal();
        }

        public void Heal(float amount)
        {
            if (!IsInitialized || _isDead) return;
            float before = CurrentHP;
            CurrentHP    = Mathf.Min(Stats.MaxHP, CurrentHP + amount);
            float healed = CurrentHP - before;
            OnHPChanged?.Invoke(CurrentHP, Stats.MaxHP);

            if (healed > 0.1f)
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

        // ── Respawn (dados vêm do servidor) ────────────────────────────────

        /// <summary>
        /// Força HP e MP — usado no respawn após servidor confirmar novos valores.
        /// </summary>
        public void ForceSetHP(float hp, float maxHp)
        {
            if (!IsInitialized) return;
            Stats.MaxHP = maxHp;
            CurrentHP   = Mathf.Clamp(hp, 0f, maxHp);
            OnHPChanged?.Invoke(CurrentHP, maxHp);
        }

        public void ForceSetMP(float mp, float maxMp)
        {
            if (!IsInitialized) return;
            Stats.MaxMP = maxMp;
            CurrentMP   = Mathf.Clamp(mp, 0f, maxMp);
            OnMPChanged?.Invoke(CurrentMP, maxMp);
        }

        public void HealToFull()
        {
            if (!IsInitialized) return;
            CurrentHP = Stats.MaxHP;
            CurrentMP = Stats.MaxMP;
            OnHPChanged?.Invoke(CurrentHP, Stats.MaxHP);
            OnMPChanged?.Invoke(CurrentMP, Stats.MaxMP);
        }

        // ── Morte ──────────────────────────────────────────────────────────

        private void DieLocal()
        {
            if (_isDead) return;
            _isDead = true;
            _agent?.ResetPath();
            OnDeathChanged?.Invoke(true);
            Debug.Log($"[PlayerEntity] {Data?.CharacterName} morreu (offline).");
        }

        /// <summary>Chamado pelo RpcPlayerDied do NetworkPlayer.</summary>
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
            _isDead            = false;
            transform.position = position;
            _agent?.Warp(position);
            OnDeathChanged?.Invoke(false);
        }

        // ── Regen (modo offline) ───────────────────────────────────────────

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

        // ── Save (modo offline apenas) ─────────────────────────────────────

        public void SaveToData()
        {
            if (!IsInitialized || ManagedByNetwork) return;
            if (GameManager.Instance?.CurrentAccount == null) return;

            Data.CurrentHP = CurrentHP;
            Data.CurrentMP = CurrentMP;
            Data.PosX      = transform.position.x;
            Data.PosY      = transform.position.y;
            Data.PosZ      = transform.position.z;

            SaveManager.Instance?.SaveCharacter(GameManager.Instance.CurrentAccount, Data);
        }

        private void OnApplicationQuit()
        {
            if (!ManagedByNetwork) SaveToData();
        }
    }
}