using UnityEngine;
using UnityEngine.AI;
using Mirror;
using RPG.Data;
using RPG.UI;
using RPG.Managers;
using RPG.Character;
using RPG.Combat;
using System.Collections;
using System.Collections.Generic;
using System;

namespace RPG.Network
{
    /// <summary>
    /// NetworkPlayer v23
    ///
    /// CORREÇÃO v23 — BUG CRÍTICO: Ordem dos SyncVars MaxHP/MaxMP vs CurrentHP/CurrentMP.
    ///
    ///   ROOT CAUSE:
    ///     SyncVars são processados pelo cliente na ordem em que são declarados no script.
    ///     Nas versões anteriores, CurrentHP era declarado ANTES de MaxHP. Quando um
    ///     level-up alterava ambos simultaneamente, o cliente recebia o batch e processava:
    ///       1. CurrentHP = MaxHP_novo  → OnNetHPChanged dispara com MaxHP = valor_ANTIGO
    ///       2. MaxHP = MaxHP_novo      → OnNetMaxHPChanged dispara (tarde demais)
    ///     Resultado: SetHPFromServer era chamado com maxHp errado → barra de HP mostrava
    ///     >100% por um frame, causando spike visual perceptível.
    ///
    ///   SOLUÇÃO:
    ///     MaxHP e MaxMP agora são declarados ANTES de CurrentHP e CurrentMP.
    ///     Ao processar o batch do level-up, Mirror agora garante a ordem correta:
    ///       1. MaxHP = MaxHP_novo      → OnNetMaxHPChanged atualiza o valor
    ///       2. CurrentHP = MaxHP_novo  → OnNetHPChanged lê MaxHP já correto
    ///     Barra de HP nunca mais exibe valor inconsistente.
    ///
    ///   NOTA IMPORTANTE PARA MIGRAÇÃO:
    ///     A ordem dos SyncVars altera o protocolo de serialização do Mirror.
    ///     Ao atualizar, delete qualquer build client/server antiga e recompile tudo.
    ///     Saves do banco de dados (SQLite) NÃO são afetados — apenas conexões ativas.
    ///
    ///   Todas as correções v22 mantidas.
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    [RequireComponent(typeof(NetworkIdentity))]
    [RequireComponent(typeof(NetworkInventory))]
    public class NetworkPlayer : NetworkBehaviour, ITargetable
    {
        public static readonly HashSet<NetworkPlayer> All = new HashSet<NetworkPlayer>();

        private const float MAX_HP_CAP               = 500_000f;
        private const float MAX_MP_CAP               = 200_000f;
        private const float SAVE_INTERVAL            = 60f;
        private const float REGEN_INTERVAL           = 5f;
        private const float ALLOCATE_MIN_INTERVAL    = 0.3f;
        private const float REGEN_COMBAT_SUPPRESSION = 8f;
        private const int   MAX_FREE_POINTS          = CharacterData.MAX_LEVEL * 5;
        private const float REGEN_DISPLAY_THRESHOLD  = 1f;

        // ── PlayerInitData — inclui resistências de equipamento ────────────
        public struct PlayerInitData
        {
            public string CharName;
            public int    Race;
            public int    Level;
            public long   Exp;
            public long   ExpToNext;
            public int    FreePoints;
            public int    AllocSTR, AllocAGI, AllocVIT, AllocDEX, AllocINT, AllocLUK;
            public int    BaseSTR,  BaseAGI,  BaseVIT,  BaseDEX,  BaseINT,  BaseLUK;
            public float  CurHP, CurMP;
            public float  EquipATK, EquipDEF, EquipMATK, EquipMDEF;
            public float  EquipResistFire, EquipResistIce, EquipResistPoison, EquipResistLightning;
            public float  EquipHPBonus, EquipMPBonus;
            public int    EquipSTR, EquipAGI, EquipVIT, EquipDEX, EquipINT, EquipLUK;
        }

        // ── SyncVars ───────────────────────────────────────────────────────
        // CORREÇÃO v23: MaxHP e MaxMP declarados ANTES de CurrentHP/CurrentMP.
        // Mirror processa SyncVars na ordem de declaração. Assim, ao receber
        // um batch de level-up, MaxHP é atualizado ANTES que OnNetHPChanged dispare,
        // eliminando o spike visual de HP >100% por um frame.
        [SyncVar(hook = nameof(OnNetNameChanged))]       public string CharacterName         = "...";
        [SyncVar]                                         public string RaceStr               = "Human";
        [SyncVar(hook = nameof(OnNetLevelChanged))]      public int    Level                 = 1;

        // ← MaxHP/MaxMP PRIMEIRO (processados antes dos Current no mesmo batch)
        [SyncVar(hook = nameof(OnNetMaxHPChanged))]      public float  MaxHP                 = 1f;
        [SyncVar(hook = nameof(OnNetHPChanged))]         public float  CurrentHP             = 0f;
        [SyncVar(hook = nameof(OnNetMaxMPChanged))]      public float  MaxMP                 = 1f;
        [SyncVar(hook = nameof(OnNetMPChanged))]         public float  CurrentMP             = 0f;

        [SyncVar(hook = nameof(OnNetMovingChanged))]     public bool   IsMoving              = false;
        [SyncVar(hook = nameof(OnNetExpChanged))]        public long   Experience            = 0;
        [SyncVar(hook = nameof(OnNetExpToNextChanged))]  public long   ExperienceToNextLevel = 100;
        [SyncVar(hook = nameof(OnNetFreePointsChanged))] public int    FreeAttributePoints   = 0;

        [SyncVar(hook = nameof(OnStatsVersionChanged))] public int StatsVersion = 0;

        // CORREÇÃO v22: hooks individuais por SyncVar — setam flag _allocDirty em vez de refresh direto
        [SyncVar(hook = nameof(OnAllocSTRChanged))] public int AllocatedSTR = 0;
        [SyncVar(hook = nameof(OnAllocAGIChanged))] public int AllocatedAGI = 0;
        [SyncVar(hook = nameof(OnAllocVITChanged))] public int AllocatedVIT = 0;
        [SyncVar(hook = nameof(OnAllocDEXChanged))] public int AllocatedDEX = 0;
        [SyncVar(hook = nameof(OnAllocINTChanged))] public int AllocatedINT = 0;
        [SyncVar(hook = nameof(OnAllocLUKChanged))] public int AllocatedLUK = 0;

        [SyncVar] public int BaseSTR = 10;
        [SyncVar] public int BaseAGI = 10;
        [SyncVar] public int BaseVIT = 10;
        [SyncVar] public int BaseDEX = 10;
        [SyncVar] public int BaseINT = 10;
        [SyncVar] public int BaseLUK = 10;

        // ── ITargetable ────────────────────────────────────────────────────
        string  ITargetable.DisplayName => CharacterName;
        float   ITargetable.CurrentHP   => CurrentHP;
        float   ITargetable.MaxHP       => MaxHP;
        bool    ITargetable.IsDead      => Dead;
        Vector3 ITargetable.Position    => transform.position;

        public void OnSelected()   { if (_selectionIndicator) _selectionIndicator.SetActive(true);  }
        public void OnDeselected() { if (_selectionIndicator) _selectionIndicator.SetActive(false); }
        public void TakeDamage(float rawAtk, float rawMatk, bool isPhysical)
            => Debug.Log("[NetworkPlayer] PvP não implementado.");

        [Header("Visuals")]
        [SerializeField] private GameObject            _selectionIndicator;
        [SerializeField] private TMPro.TMP_Text        _nameTagText;
        [SerializeField] private UnityEngine.UI.Slider _hpBarSlider;

        [Header("Respawn Points")]
        [SerializeField] private Transform[] _respawnPoints;

        // ── Componentes ────────────────────────────────────────────────────
        private NavMeshAgent     _agent;
        private Animator         _animator;
        private PlayerEntity     _playerEntity;
        private NetworkInventory _inventory;

        // ── Estado do servidor ─────────────────────────────────────────────
        private CharacterData _serverCharData;
        private DerivedStats  _serverStats;
        private string        _serverAccountUsername;
        private float         _autoSaveTimer;
        private float         _lastAllocateTime   = -999f;
        private float         _lastDamageTime     = -999f;
        private bool          _isDirty            = false;

        public DerivedStats ServerStats => _serverStats;

        private readonly Dictionary<int, float> _serverSkillCooldowns = new();
        private Coroutine _regenCoroutine;

        // ── Estado do cliente ──────────────────────────────────────────────
        private bool          _clientInitialized = false;
        private bool          _pendingClientInit = false;
        private CharacterData _pendingInitData   = null;

        // CORREÇÃO v22: flag para consolidar múltiplos hooks de alloc em um único refresh
        private bool  _allocDirty = false;

        private float       _lastMovingCmdTime;
        private const float MOVING_CMD_INTERVAL = 0.1f;

        public bool Dead => CurrentHP <= 0f;

        // ── Lifecycle ──────────────────────────────────────────────────────

        private void Awake()
        {
            _agent        = GetComponent<NavMeshAgent>();
            _animator     = GetComponentInChildren<Animator>();
            _playerEntity = GetComponent<PlayerEntity>();
            _inventory    = GetComponent<NetworkInventory>();
        }

        public override void OnStartServer()
        {
            All.Add(this);
            _autoSaveTimer = SAVE_INTERVAL;
        }

        public override void OnStopServer()
        {
            All.Remove(this);
            StopRegenLoop();

            if (_serverCharData != null && !string.IsNullOrEmpty(_serverAccountUsername))
                ServerSaveCharacterForced();
            else
                Debug.LogWarning($"[Server] OnStopServer: {CharacterName} sem dados ou username — save ignorado.");
        }

        public override void OnStartClient()
        {
            if (_nameTagText        != null) _nameTagText.text = CharacterName;
            if (_selectionIndicator != null) _selectionIndicator.SetActive(false);
            if (!isLocalPlayer && _agent != null) _agent.enabled = false;
        }

        public override void OnStopClient()
        {
            _clientInitialized = false;
            _pendingClientInit = false;
            _pendingInitData   = null;
            _allocDirty        = false;
        }

        public override void OnStartLocalPlayer()
        {
            _playerEntity = GetComponent<PlayerEntity>();
            _agent        = GetComponent<NavMeshAgent>();
            if (_agent != null) _agent.enabled = true;

            Debug.Log("[NetworkPlayer] Local player ativo — aguardando RpcInitializeLocalPlayer.");

            if (_pendingClientInit && _pendingInitData != null)
            {
                var data = _pendingInitData;
                _pendingClientInit = false;
                _pendingInitData   = null;
                StartCoroutine(DelayedClientInit(data));
            }
        }

        private void Update()
        {
            if (isServer) ServerUpdate();
            if (!isLocalPlayer || Dead) return;

            ClientMovingUpdate();

            // CORREÇÃO v22: processa alloc dirty uma vez por frame no cliente
            if (_allocDirty)
            {
                _allocDirty = false;
                ApplyAllocatedDataToEntity();
            }
        }

        [Server]
        private void ServerUpdate()
        {
            _autoSaveTimer -= Time.deltaTime;
            if (_autoSaveTimer <= 0f)
            {
                _autoSaveTimer = SAVE_INTERVAL;
                if (_isDirty) ServerSaveCharacterForced();
            }
        }

        private void ClientMovingUpdate()
        {
            if (_agent == null || !_agent.enabled) return;
            bool moving = _agent.velocity.sqrMagnitude > 0.05f;
            if (moving != IsMoving && Time.time - _lastMovingCmdTime >= MOVING_CMD_INTERVAL)
            {
                _lastMovingCmdTime = Time.time;
                CmdSetMoving(moving);
            }
        }

        // ── Inicialização pelo servidor ────────────────────────────────────

        [Server]
        public void ServerInitialize(CharacterData charData, string accountUsername)
        {
            if (charData == null || string.IsNullOrEmpty(accountUsername))
            {
                Debug.LogError("[NetworkPlayer] ServerInitialize: charData ou accountUsername inválidos!");
                return;
            }

            _serverAccountUsername = accountUsername;
            _serverCharData        = charData;
            _serverStats           = charData.GetDerivedStats();

            float maxHP = Mathf.Min(_serverStats.MaxHP, MAX_HP_CAP);
            float maxMP = Mathf.Min(_serverStats.MaxMP, MAX_MP_CAP);

            CharacterName         = charData.CharacterName;
            RaceStr               = charData.Race.ToString();
            Level                 = charData.Level;
            Experience            = charData.Experience;
            ExperienceToNextLevel = charData.ExperienceToNextLevel;
            FreeAttributePoints   = charData.FreeAttributePoints;
            AllocatedSTR          = charData.AllocatedSTR;
            AllocatedAGI          = charData.AllocatedAGI;
            AllocatedVIT          = charData.AllocatedVIT;
            AllocatedDEX          = charData.AllocatedDEX;
            AllocatedINT          = charData.AllocatedINT;
            AllocatedLUK          = charData.AllocatedLUK;
            BaseSTR = charData.BaseAttributes.STR;
            BaseAGI = charData.BaseAttributes.AGI;
            BaseVIT = charData.BaseAttributes.VIT;
            BaseDEX = charData.BaseAttributes.DEX;
            BaseINT = charData.BaseAttributes.INT;
            BaseLUK = charData.BaseAttributes.LUK;

            // CORREÇÃO v23: MaxHP/MaxMP setados ANTES de CurrentHP/CurrentMP aqui também,
            // para que clientes que ingressam depois da inicialização recebam a ordem correta.
            MaxHP     = maxHP;
            MaxMP     = maxMP;
            CurrentHP = (charData.CurrentHP > 0f && charData.CurrentHP <= maxHP) ? charData.CurrentHP : maxHP;
            CurrentMP = (charData.CurrentMP > 0f && charData.CurrentMP <= maxMP) ? charData.CurrentMP : maxMP;

            StatsVersion++;

            var savedPos = new Vector3(charData.PosX, charData.PosY, charData.PosZ);
            if (savedPos.sqrMagnitude > 0.01f)
            {
                transform.position = savedPos;
                if (_agent != null && _agent.isOnNavMesh) _agent.Warp(savedPos);
            }
            if (_agent != null) _agent.speed = Mathf.Clamp(_serverStats.MoveSpeed, 3f, 7f);

            _inventory?.ServerLoadFromDatabase(charData.CharacterId);
            _inventory?.ServerLoadGemLoadout(charData.CharacterId);

            StartRegenLoop();

            Debug.Log($"[Server] {charData.CharacterName} Lv{Level} HP:{CurrentHP:0}/{MaxHP:0} inicializado.");
            StartCoroutine(SendInitRpcDelayed(charData));
        }

        [Server]
        private IEnumerator SendInitRpcDelayed(CharacterData charData)
        {
            yield return null;
            yield return null;

            var eq = charData.EquipmentBonuses ?? new EquipmentBonuses();

            var initData = new PlayerInitData
            {
                CharName   = charData.CharacterName,
                Race       = (int)charData.Race,
                Level      = charData.Level,
                Exp        = charData.Experience,
                ExpToNext  = charData.ExperienceToNextLevel,
                FreePoints = charData.FreeAttributePoints,
                AllocSTR   = charData.AllocatedSTR,
                AllocAGI   = charData.AllocatedAGI,
                AllocVIT   = charData.AllocatedVIT,
                AllocDEX   = charData.AllocatedDEX,
                AllocINT   = charData.AllocatedINT,
                AllocLUK   = charData.AllocatedLUK,
                BaseSTR    = charData.BaseAttributes.STR,
                BaseAGI    = charData.BaseAttributes.AGI,
                BaseVIT    = charData.BaseAttributes.VIT,
                BaseDEX    = charData.BaseAttributes.DEX,
                BaseINT    = charData.BaseAttributes.INT,
                BaseLUK    = charData.BaseAttributes.LUK,
                CurHP      = CurrentHP,
                CurMP      = CurrentMP,
                EquipATK   = eq.ATK,  EquipDEF  = eq.DEF,
                EquipMATK  = eq.MATK, EquipMDEF = eq.MDEF,
                EquipSTR   = eq.STR,  EquipAGI  = eq.AGI,
                EquipVIT   = eq.VIT,  EquipDEX  = eq.DEX,
                EquipINT   = eq.INT,  EquipLUK  = eq.LUK,
                EquipHPBonus = eq.HPBonus, EquipMPBonus = eq.MPBonus,
                EquipResistFire      = eq.ResistFire,
                EquipResistIce       = eq.ResistIce,
                EquipResistPoison    = eq.ResistPoison,
                EquipResistLightning = eq.ResistLightning
            };

            RpcInitializeLocalPlayer(initData);
        }

        // ── Regen Loop ─────────────────────────────────────────────────────

        [Server]
        private void StartRegenLoop()
        {
            StopRegenLoop();
            _regenCoroutine = StartCoroutine(ServerRegenLoop());
        }

        [Server]
        private void StopRegenLoop()
        {
            if (_regenCoroutine != null)
            {
                StopCoroutine(_regenCoroutine);
                _regenCoroutine = null;
            }
        }

        [Server]
        private IEnumerator ServerRegenLoop()
        {
            var wait = new WaitForSeconds(REGEN_INTERVAL);
            while (true)
            {
                yield return wait;

                // CORREÇÃO v22: verifica this, isServer e _serverStats após o yield
                if (this == null || !isServer) yield break;
                if (Dead) continue;

                // Captura referência local para evitar race condition
                var stats = _serverStats;
                if (stats == null) continue;

                bool inCombat = (Time.time - _lastDamageTime) < REGEN_COMBAT_SUPPRESSION;
                if (inCombat) continue;

                bool needsHPRegen = CurrentHP < MaxHP && stats.HPRegen > 0f;
                bool needsMPRegen = CurrentMP < MaxMP && stats.MPRegen > 0f;
                if (!needsHPRegen && !needsMPRegen) continue;

                float hpRestored = 0f;
                float mpRestored = 0f;

                if (needsHPRegen)
                {
                    float before = CurrentHP;
                    CurrentHP = Mathf.Min(MaxHP, CurrentHP + stats.HPRegen);
                    hpRestored = CurrentHP - before;
                    if (_serverCharData != null) _serverCharData.CurrentHP = CurrentHP;
                }

                if (needsMPRegen)
                {
                    float before = CurrentMP;
                    CurrentMP = Mathf.Min(MaxMP, CurrentMP + stats.MPRegen);
                    mpRestored = CurrentMP - before;
                    if (_serverCharData != null) _serverCharData.CurrentMP = CurrentMP;
                }

                if (hpRestored >= REGEN_DISPLAY_THRESHOLD || mpRestored >= REGEN_DISPLAY_THRESHOLD)
                    RpcShowRegenTick(hpRestored, mpRestored);
            }
        }

        // ── Commands ──────────────────────────────────────────────────────

        [Command] public void CmdSetMoving(bool moving) => IsMoving = moving;

        [Command]
        public void CmdAllocateAttribute(int attributeIndex)
        {
            if (Time.time - _lastAllocateTime < ALLOCATE_MIN_INTERVAL)
            {
                Debug.LogWarning($"[Server] CmdAllocateAttribute spam: {CharacterName}");
                return;
            }

            if (FreeAttributePoints <= 0 || _serverCharData == null) return;
            if (attributeIndex < 0 || attributeIndex > 5) return;

            _lastAllocateTime = Time.time;

            bool limitExceeded = attributeIndex switch
            {
                0 => _serverCharData.AllocatedSTR >= CharacterData.MAX_ALLOCATED_PER_STAT,
                1 => _serverCharData.AllocatedAGI >= CharacterData.MAX_ALLOCATED_PER_STAT,
                2 => _serverCharData.AllocatedVIT >= CharacterData.MAX_ALLOCATED_PER_STAT,
                3 => _serverCharData.AllocatedDEX >= CharacterData.MAX_ALLOCATED_PER_STAT,
                4 => _serverCharData.AllocatedINT >= CharacterData.MAX_ALLOCATED_PER_STAT,
                5 => _serverCharData.AllocatedLUK >= CharacterData.MAX_ALLOCATED_PER_STAT,
                _ => true
            };

            if (limitExceeded)
            {
                Debug.LogWarning($"[Security] {CharacterName} tentou alocar atributo {attributeIndex} além do limite.");
                return;
            }

            FreeAttributePoints--;
            _serverCharData.FreeAttributePoints--;

            switch (attributeIndex)
            {
                case 0: AllocatedSTR++; _serverCharData.AllocatedSTR++; break;
                case 1: AllocatedAGI++; _serverCharData.AllocatedAGI++; break;
                case 2: AllocatedVIT++; _serverCharData.AllocatedVIT++; break;
                case 3: AllocatedDEX++; _serverCharData.AllocatedDEX++; break;
                case 4: AllocatedINT++; _serverCharData.AllocatedINT++; break;
                case 5: AllocatedLUK++; _serverCharData.AllocatedLUK++; break;
            }

            _serverStats = _serverCharData.GetDerivedStats();

            // CORREÇÃO v23: MaxHP/MaxMP atualizados ANTES para manter consistência com ordem de SyncVars
            MaxHP = Mathf.Min(_serverStats.MaxHP, MAX_HP_CAP);
            MaxMP = Mathf.Min(_serverStats.MaxMP, MAX_MP_CAP);
            if (CurrentHP > MaxHP) CurrentHP = MaxHP;
            if (CurrentMP > MaxMP) CurrentMP = MaxMP;

            if (_agent != null && _agent.isOnNavMesh)
                _agent.speed = Mathf.Clamp(_serverStats.MoveSpeed, 3f, 7f);

            StatsVersion++;
            MarkDirty();
        }

        [Command] public void CmdRequestRespawn()
        {
            if (!Dead) return;
            ServerRespawn();
        }

        [Command]
        public void CmdRequestSelfSkill(int skillIndex)
        {
            if (Dead || _serverStats == null) return;

            var skill = _inventory?.GetEquippedSkill(skillIndex);
            if (skill == null) { RpcSkillRejected(skillIndex, "Nenhuma joia equipada neste slot."); return; }

            if (!ServerCheckAndSetCooldown(skillIndex, skill.Cooldown))
            {
                if (_serverSkillCooldowns.TryGetValue(skillIndex, out float endTime))
                    RpcSkillRejected(skillIndex, $"{skill.Name}: aguarde {endTime - Time.time:0.0}s");
                return;
            }

            if (CurrentMP < skill.ManaCost) { RpcSkillRejected(skillIndex, "MP insuficiente!"); return; }

            ServerConsumeMP(skill.ManaCost);

            if (skill.Type == SkillType.Heal)
            {
                float heal = Mathf.Max(10f, _serverStats.MATK * skill.AtkMultiplier);
                float before = CurrentHP;
                CurrentHP = Mathf.Min(MaxHP, CurrentHP + heal);
                float healed = CurrentHP - before;
                if (_serverCharData != null) _serverCharData.CurrentHP = CurrentHP;
                if (healed > 0f) RpcShowHeal(healed);
            }

            RpcSkillConfirmed(skillIndex, skill.Cooldown);
        }

        // ── Métodos de servidor ────────────────────────────────────────────

        [Server]
        public void ServerApplyDamage(float dmg)
        {
            if (Dead) return;
            _lastDamageTime = Time.time;
            CurrentHP = Mathf.Max(0f, CurrentHP - dmg);
            if (_serverCharData != null) _serverCharData.CurrentHP = CurrentHP;
            if (CurrentHP <= 0f) ServerDie();
        }

        [Server]
        public void ServerApplyDamageWithFeedback(float dmg)
        {
            if (Dead) return;
            _lastDamageTime = Time.time;
            float before = CurrentHP;
            CurrentHP = Mathf.Max(0f, CurrentHP - dmg);
            float actualDmg = before - CurrentHP;
            if (_serverCharData != null) _serverCharData.CurrentHP = CurrentHP;
            if (actualDmg > 0f) RpcShowDamageTaken(actualDmg);
            if (CurrentHP <= 0f) ServerDie();
        }

        [Server]
        public void ServerApplyHeal(float amount)
        {
            if (Dead || amount <= 0f) return;
            float before = CurrentHP;
            CurrentHP = Mathf.Min(MaxHP, CurrentHP + amount);
            float healed = CurrentHP - before;
            if (_serverCharData != null) _serverCharData.CurrentHP = CurrentHP;
            if (healed > 0f) RpcShowHeal(healed);
        }

        [Server]
        public void ServerRestoreMP(float amount)
        {
            if (Dead || amount <= 0f) return;
            CurrentMP = Mathf.Min(MaxMP, CurrentMP + amount);
            if (_serverCharData != null) _serverCharData.CurrentMP = CurrentMP;
        }

        [Server]
        public void ServerConsumeMP(float amount)
        {
            CurrentMP = Mathf.Max(0f, CurrentMP - amount);
            if (_serverCharData != null) _serverCharData.CurrentMP = CurrentMP;
        }

        [Server]
        public bool ServerCheckAndSetCooldown(int skillIndex, float cooldownDuration)
        {
            if (_serverSkillCooldowns.TryGetValue(skillIndex, out float endTime) && Time.time < endTime)
                return false;
            _serverSkillCooldowns[skillIndex] = Time.time + cooldownDuration;
            return true;
        }

        [Server]
        public void ServerGrantExp(long amount)
        {
            if (_serverCharData == null || amount <= 0) return;

            bool leveledUp = _serverCharData.AddExperience(amount);

            Experience            = _serverCharData.Experience;
            ExperienceToNextLevel = _serverCharData.ExperienceToNextLevel;
            Level                 = _serverCharData.Level;

            FreeAttributePoints   = Mathf.Min(_serverCharData.FreeAttributePoints, MAX_FREE_POINTS);
            _serverCharData.FreeAttributePoints = FreeAttributePoints;

            if (leveledUp)
            {
                _serverStats = _serverCharData.GetDerivedStats();

                // CORREÇÃO v23: MaxHP/MaxMP atualizados ANTES de CurrentHP/CurrentMP
                // para manter consistência com a ordem de declaração dos SyncVars.
                MaxHP     = Mathf.Min(_serverStats.MaxHP, MAX_HP_CAP);
                MaxMP     = Mathf.Min(_serverStats.MaxMP, MAX_MP_CAP);
                CurrentHP = MaxHP;
                CurrentMP = MaxMP;
                _serverCharData.CurrentHP = MaxHP;
                _serverCharData.CurrentMP = MaxMP;

                if (_agent != null && _agent.isOnNavMesh)
                    _agent.speed = Mathf.Clamp(_serverStats.MoveSpeed, 3f, 7f);

                StatsVersion++;
                StartRegenLoop();
                Debug.Log($"[Server] {CharacterName} → Lv {Level}!");
            }

            DatabaseManager.Instance?.LogEconomy(_serverCharData.CharacterId, "exp_gain", amount);

            if (leveledUp) ServerSaveCharacterForced();
            else           MarkDirty();

            RpcOnExpGained(amount, leveledUp);
        }

        [Server] private void MarkDirty() => _isDirty = true;

        [Server]
        public void ServerSaveCharacterForced()
        {
            if (_serverCharData == null)
            {
                Debug.LogWarning($"[Server] ServerSaveCharacterForced: sem dados para {CharacterName}");
                return;
            }
            if (string.IsNullOrEmpty(_serverAccountUsername))
            {
                Debug.LogError($"[Server] ServerSaveCharacterForced: username vazio para {CharacterName} — save cancelado!");
                return;
            }

            _serverCharData.CurrentHP = CurrentHP;
            _serverCharData.CurrentMP = CurrentMP;
            _serverCharData.PosX      = transform.position.x;
            _serverCharData.PosY      = transform.position.y;
            _serverCharData.PosZ      = transform.position.z;

            DatabaseManager.Instance?.SaveCharacter(_serverCharData, _serverAccountUsername);
            _inventory?.ServerSaveAll(_serverCharData.CharacterId, _serverAccountUsername);
            _isDirty = false;
        }

        [Server] public void ServerSaveCharacter() => ServerSaveCharacterForced();

        // ── ClientRpcs ─────────────────────────────────────────────────────

        [ClientRpc]
        private void RpcInitializeLocalPlayer(PlayerInitData d)
        {
            if (!isLocalPlayer) return;

            var data = new CharacterData
            {
                CharacterName         = d.CharName,
                Race                  = (CharacterRace)d.Race,
                Level                 = d.Level,
                Experience            = d.Exp,
                ExperienceToNextLevel = d.ExpToNext,
                FreeAttributePoints   = d.FreePoints,
                AllocatedSTR          = d.AllocSTR,
                AllocatedAGI          = d.AllocAGI,
                AllocatedVIT          = d.AllocVIT,
                AllocatedDEX          = d.AllocDEX,
                AllocatedINT          = d.AllocINT,
                AllocatedLUK          = d.AllocLUK,
                CurrentHP             = d.CurHP,
                CurrentMP             = d.CurMP,
                BaseAttributes = new BaseAttributes
                {
                    STR = d.BaseSTR, AGI = d.BaseAGI, VIT = d.BaseVIT,
                    DEX = d.BaseDEX, INT = d.BaseINT, LUK = d.BaseLUK
                },
                EquipmentBonuses = new EquipmentBonuses
                {
                    ATK  = d.EquipATK,  DEF  = d.EquipDEF,
                    MATK = d.EquipMATK, MDEF = d.EquipMDEF,
                    STR  = d.EquipSTR,  AGI  = d.EquipAGI,
                    VIT  = d.EquipVIT,  DEX  = d.EquipDEX,
                    INT  = d.EquipINT,  LUK  = d.EquipLUK,
                    HPBonus = d.EquipHPBonus, MPBonus = d.EquipMPBonus,
                    ResistFire      = d.EquipResistFire,
                    ResistIce       = d.EquipResistIce,
                    ResistPoison    = d.EquipResistPoison,
                    ResistLightning = d.EquipResistLightning
                }
            };

            if (_playerEntity == null)
            {
                _pendingClientInit = true;
                _pendingInitData   = data;
                return;
            }

            if (_clientInitialized) return;
            _clientInitialized = true;
            StartCoroutine(DelayedClientInit(data));
        }

        private IEnumerator DelayedClientInit(CharacterData data)
        {
            yield return null;

            if (_playerEntity == null)
            {
                _playerEntity = GetComponent<PlayerEntity>();
                if (_playerEntity == null)
                {
                    Debug.LogError("[NetworkPlayer] PlayerEntity não encontrado!");
                    yield break;
                }
            }

            _playerEntity.InitializeFromServer(data);
            UIManager.Instance?.BindLocalPlayer(_playerEntity);
            AttributeWindowUI.Instance?.BindPlayer(_playerEntity);

            Debug.Log($"[Client] Inicializado: {data.CharacterName} Lv{data.Level}");
        }

        [ClientRpc]
        private void RpcPlayerDied()
        {
            if (!isLocalPlayer) return;
            if (_agent != null) { _agent.ResetPath(); _agent.isStopped = true; }
            GetComponent<NetworkPlayerController>()?.SetEnabled(false);
            _playerEntity?.OnServerDeath();
            DeathScreenUI.Show(this);
        }

        [ClientRpc]
        private void RpcOnRespawned(Vector3 position, float hp, float maxHp, float mp, float maxMp)
        {
            if (!isLocalPlayer) return;
            if (_agent != null) { _agent.isStopped = false; _agent.Warp(position); }
            GetComponent<NetworkPlayerController>()?.SetEnabled(true);
            _playerEntity?.OnServerRespawn(position, hp, maxHp, mp, maxMp);
            DeathScreenUI.Hide();
        }

        [ClientRpc] public void RpcPlayAnimation(string trigger) => _animator?.SetTrigger(trigger);

        [ClientRpc]
        private void RpcOnExpGained(long amount, bool leveledUp)
        {
            if (!isLocalPlayer) return;
            FloatingTextManager.Instance?.Show($"+{amount} XP", transform.position + Vector3.up * 2f, Color.cyan);
            if (leveledUp)
            {
                FloatingTextManager.Instance?.Show("LEVEL UP!", transform.position + Vector3.up * 2.5f, Color.yellow);
                UIManager.Instance?.ShowMessage("Level up! Você evoluiu!");
            }
        }

        [ClientRpc]
        private void RpcShowDamageTaken(float dmg)
        {
            FloatingTextManager.Instance?.Show(
                $"-{dmg:0}", transform.position + Vector3.up * 2f,
                new Color(1f, 0.25f, 0.25f));
        }

        [ClientRpc]
        private void RpcShowRegenTick(float hpRestored, float mpRestored)
        {
            if (!isLocalPlayer) return;
            Vector3 basePos = transform.position + Vector3.up * 2f;
            if (hpRestored >= REGEN_DISPLAY_THRESHOLD)
                FloatingTextManager.Instance?.Show($"+{hpRestored:0} HP", basePos, new Color(0.4f, 1f, 0.4f));
            if (mpRestored >= REGEN_DISPLAY_THRESHOLD)
                FloatingTextManager.Instance?.Show($"+{mpRestored:0} MP", basePos + new Vector3(0.3f, 0.2f, 0f), new Color(0.4f, 0.7f, 1f));
        }

        [ClientRpc]
        private void RpcShowHeal(float amount)
        {
            FloatingTextManager.Instance?.Show(
                $"+{amount:0} HP", transform.position + Vector3.up * 1.5f, Color.green);
        }

        [ClientRpc]
        public void RpcSkillConfirmed(int skillIndex, float cooldown)
        {
            if (!isLocalPlayer) return;
            GetComponent<SkillSystem>()?.OnServerSkillConfirmed(skillIndex, cooldown);
        }

        [ClientRpc]
        public void RpcSkillRejected(int skillIndex, string reason)
        {
            if (!isLocalPlayer) return;
            GetComponent<SkillSystem>()?.OnServerSkillRejected(skillIndex, reason);
        }

        // ── Morte / Respawn ────────────────────────────────────────────────

        [Server]
        private void ServerDie()
        {
            CurrentHP = 0f;
            StopRegenLoop();
            if (_agent != null) _agent.ResetPath();
            ServerSaveCharacterForced();
            RpcPlayerDied();
        }

        [Server]
        private void ServerRespawn()
        {
            if (_serverStats == null) return;

            Vector3 pos = GetRespawnPosition();
            transform.position = pos;
            if (_agent != null && _agent.isOnNavMesh) _agent.Warp(pos);

            // CORREÇÃO v23: MaxHP/MaxMP setados ANTES de CurrentHP/CurrentMP
            MaxHP     = Mathf.Min(_serverStats.MaxHP, MAX_HP_CAP);
            MaxMP     = Mathf.Min(_serverStats.MaxMP, MAX_MP_CAP);
            CurrentHP = MaxHP * 0.5f;
            CurrentMP = MaxMP * 0.5f;

            if (_serverCharData != null)
            {
                _serverCharData.CurrentHP = CurrentHP;
                _serverCharData.CurrentMP = CurrentMP;
                ServerSaveCharacterForced();
            }

            _lastDamageTime = -999f;
            StartRegenLoop();

            RpcOnRespawned(pos, CurrentHP, MaxHP, CurrentMP, MaxMP);
        }

        /// <summary>
        /// CORREÇÃO v22: fallback tenta NavMesh.SamplePosition antes de aceitar Vector3.zero.
        /// </summary>
        [Server]
        private Vector3 GetRespawnPosition()
        {
            if (_respawnPoints != null && _respawnPoints.Length > 0)
            {
                var pt = _respawnPoints[UnityEngine.Random.Range(0, _respawnPoints.Length)];
                if (pt != null) return pt.position;
            }

            if (_serverCharData != null)
            {
                var nm = RPGNetworkManager.singleton;
                if (nm != null)
                {
                    Vector3 racePos = nm.GetSpawnPositionForRace(_serverCharData.Race, _serverCharData);
                    if (racePos.sqrMagnitude > 0.01f) return racePos;
                }
            }

            if (NavMesh.SamplePosition(Vector3.zero, out NavMeshHit hit, 50f, NavMesh.AllAreas))
                return hit.position;

            Debug.LogWarning($"[Server] GetRespawnPosition: nenhum ponto válido para {CharacterName}. Usando origem.");
            return Vector3.zero;
        }

        // ── SyncVar Hooks ──────────────────────────────────────────────────

        private void OnNetNameChanged(string _, string v)
        {
            if (_nameTagText != null) _nameTagText.text = v;
        }

        /// <summary>
        /// CORREÇÃO v23: MaxHP processado ANTES de CurrentHP (pela ordem de declaração).
        /// Quando ambos chegam no mesmo batch (level-up), este hook já tem o MaxHP correto
        /// disponível quando OnNetHPChanged disparar a seguir.
        /// </summary>
        private void OnNetMaxHPChanged(float _, float newMax)
        {
            if (_hpBarSlider != null) _hpBarSlider.maxValue = newMax;
            if (isLocalPlayer && _playerEntity != null && _playerEntity.IsInitialized)
                _playerEntity.RefreshStatsFromServer(newMax, MaxMP);
        }

        /// <summary>
        /// CORREÇÃO v23: disparado DEPOIS de OnNetMaxHPChanged (por estar declarado depois).
        /// MaxHP já está atualizado quando este hook roda, eliminando o spike visual.
        /// </summary>
        private void OnNetHPChanged(float _, float newHP)
        {
            if (_hpBarSlider != null)
            {
                _hpBarSlider.maxValue = MaxHP;
                _hpBarSlider.value    = newHP;
                _hpBarSlider.gameObject.SetActive(newHP < MaxHP);
            }
            if (isLocalPlayer && _playerEntity != null && _playerEntity.IsInitialized)
                _playerEntity.SetHPFromServer(newHP, MaxHP);
        }

        private void OnNetMaxMPChanged(float _, float newMax)
        {
            if (isLocalPlayer && _playerEntity != null && _playerEntity.IsInitialized)
                _playerEntity.RefreshStatsFromServer(MaxHP, newMax);
        }

        private void OnNetMPChanged(float _, float newMP)
        {
            if (isLocalPlayer && _playerEntity != null && _playerEntity.IsInitialized)
                _playerEntity.SetMPFromServer(newMP, MaxMP);
        }

        private void OnNetLevelChanged(int _, int v)
        {
            if (isLocalPlayer) UIManager.Instance?.RefreshLevel(v);
        }

        private void OnNetFreePointsChanged(int _, int newPoints)
        {
            if (!isLocalPlayer) return;
            AttributeWindowUI.Instance?.OnFreePointsUpdated(newPoints);
        }

        private void OnNetMovingChanged(bool _, bool v)
        {
            if (!isLocalPlayer) _animator?.SetBool("IsMoving", v);
        }

        private void OnNetExpChanged(long _, long __)
        {
            if (!isLocalPlayer) return;
            UIManager.Instance?.RefreshExpBar(Experience, ExperienceToNextLevel);
            AttributeWindowUI.Instance?.RefreshXPBar(Experience, ExperienceToNextLevel);
        }

        private void OnNetExpToNextChanged(long _, long __)
        {
            if (!isLocalPlayer) return;
            UIManager.Instance?.RefreshExpBar(Experience, ExperienceToNextLevel);
            AttributeWindowUI.Instance?.RefreshXPBar(Experience, ExperienceToNextLevel);
        }

        // CORREÇÃO v22: hooks individuais que apenas setam a flag dirty
        private void OnAllocSTRChanged(int _, int __) { if (isLocalPlayer) _allocDirty = true; }
        private void OnAllocAGIChanged(int _, int __) { if (isLocalPlayer) _allocDirty = true; }
        private void OnAllocVITChanged(int _, int __) { if (isLocalPlayer) _allocDirty = true; }
        private void OnAllocDEXChanged(int _, int __) { if (isLocalPlayer) _allocDirty = true; }
        private void OnAllocINTChanged(int _, int __) { if (isLocalPlayer) _allocDirty = true; }
        private void OnAllocLUKChanged(int _, int __) { if (isLocalPlayer) _allocDirty = true; }

        private void ApplyAllocatedDataToEntity()
        {
            if (_playerEntity?.Data == null) return;

            _playerEntity.Data.BaseAttributes.STR = BaseSTR;
            _playerEntity.Data.BaseAttributes.AGI = BaseAGI;
            _playerEntity.Data.BaseAttributes.VIT = BaseVIT;
            _playerEntity.Data.BaseAttributes.DEX = BaseDEX;
            _playerEntity.Data.BaseAttributes.INT = BaseINT;
            _playerEntity.Data.BaseAttributes.LUK = BaseLUK;

            _playerEntity.Data.AllocatedSTR = AllocatedSTR;
            _playerEntity.Data.AllocatedAGI = AllocatedAGI;
            _playerEntity.Data.AllocatedVIT = AllocatedVIT;
            _playerEntity.Data.AllocatedDEX = AllocatedDEX;
            _playerEntity.Data.AllocatedINT = AllocatedINT;
            _playerEntity.Data.AllocatedLUK = AllocatedLUK;

            if (_playerEntity.IsInitialized)
                _playerEntity.FullRefreshStatsFromData();
        }

        private void OnStatsVersionChanged(int _, int __)
        {
            if (!isLocalPlayer) return;
            if (_playerEntity == null || !_playerEntity.IsInitialized) return;
            _playerEntity.FullRefreshStatsFromData();
        }
    }
}
