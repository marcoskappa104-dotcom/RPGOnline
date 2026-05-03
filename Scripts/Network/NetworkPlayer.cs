using UnityEngine;
using UnityEngine.AI;
using Mirror;
using RPG.Data;
using RPG.UI;
using RPG.Managers;
using RPG.Character;
using System.Collections.Generic;
using System;

namespace RPG.Network
{
    /// <summary>
    /// NetworkPlayer — SERVIDOR É AUTORIDADE sobre HP, MP, XP e atributos.
    ///
    /// FLUXO DE DADOS:
    ///   1. Cliente entra → CmdRegisterCharacter (envia dados salvos).
    ///   2. Servidor calcula stats canônicos → seta SyncVars → RpcInitializeLocalPlayer.
    ///   3. HP/MP só mudam via ServerApplyDamage ou ServerGrantExp.
    ///   4. Hooks das SyncVars propagam para PlayerEntity.SetHPFromNetwork / SetMPFromNetwork.
    ///   5. Save acontece SOMENTE no servidor via ServerSaveCharacter (com AccountData correta).
    ///
    /// CORREÇÕES v8:
    ///   - ServerSaveCharacter agora usa _serverAccountUsername para encontrar a AccountData
    ///     correta. O bug anterior usava CharacterName como Username.
    ///   - CmdSetMoving tem throttle de 100 ms para não inundar o servidor.
    ///   - Dead calculado a partir de CurrentHP (fonte única de verdade).
    ///   - _isDead local removido — usa Dead property.
    ///   - Validação de range no CmdRegisterCharacter (evita valores absurdos).
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    [RequireComponent(typeof(NetworkIdentity))]
    [RequireComponent(typeof(NetworkTransformReliable))]
    public class NetworkPlayer : NetworkBehaviour, ITargetable
    {
        // ── Registro estático ──────────────────────────────────────────────
        public static readonly HashSet<NetworkPlayer> All = new HashSet<NetworkPlayer>();

        // ── Limites de validação server-side ──────────────────────────────
        private const int   MAX_LEVEL      = 99;
        private const long  MAX_EXPERIENCE = 99_999_999L;
        private const float MAX_HP_CAP     = 500_000f;
        private const float MAX_MP_CAP     = 200_000f;
        private const int   MAX_ALLOC_STAT = 500;

        // ── SyncVars ───────────────────────────────────────────────────────
        [SyncVar(hook = nameof(OnNameChanged))]
        public string CharacterName = "...";

        [SyncVar]
        public string Race = "Human";

        [SyncVar(hook = nameof(OnLevelChanged))]
        public int Level = 1;

        [SyncVar(hook = nameof(OnHPChanged))]
        public float CurrentHP = 0f;

        [SyncVar(hook = nameof(OnMaxHPChanged))]
        public float MaxHP = 1f;

        [SyncVar(hook = nameof(OnMPChanged))]
        public float CurrentMP = 0f;

        [SyncVar(hook = nameof(OnMaxMPChanged))]
        public float MaxMP = 1f;

        [SyncVar(hook = nameof(OnMovingChanged))]
        public bool IsMoving = false;

        [SyncVar(hook = nameof(OnExpChanged))]
        public long Experience = 0;

        [SyncVar]
        public long ExperienceToNextLevel = 100;

        [SyncVar(hook = nameof(OnFreePointsChanged))]
        public int FreeAttributePoints = 0;

        [SyncVar] public int AllocatedSTR = 0;
        [SyncVar] public int AllocatedAGI = 0;
        [SyncVar] public int AllocatedVIT = 0;
        [SyncVar] public int AllocatedDEX = 0;
        [SyncVar] public int AllocatedINT = 0;
        [SyncVar] public int AllocatedLUK = 0;

        // ── ITargetable ────────────────────────────────────────────────────
        string  ITargetable.DisplayName => CharacterName;
        float   ITargetable.CurrentHP   => CurrentHP;
        float   ITargetable.MaxHP       => MaxHP;
        bool    ITargetable.IsDead      => Dead;
        Vector3 ITargetable.Position    => transform.position;

        public void OnSelected()   { if (selectionIndicator) selectionIndicator.SetActive(true);  }
        public void OnDeselected() { if (selectionIndicator) selectionIndicator.SetActive(false); }
        public void TakeDamage(float rawAtk, float rawMatk, bool isPhysical)
            => Debug.Log("[NetworkPlayer] PvP não implementado.");

        // ── Componentes ────────────────────────────────────────────────────
        private NavMeshAgent _agent;
        private Animator     _animator;

        [Header("Visuals")]
        [SerializeField] private GameObject            selectionIndicator;
        [SerializeField] private TMPro.TMP_Text        nameTagText;
        [SerializeField] private UnityEngine.UI.Slider hpBarSlider;

        [Header("Spawn Points")]
        [SerializeField] private Transform[] spawnPoints;

        // ── Estado local ───────────────────────────────────────────────────
        private CharacterData _charData;
        private PlayerEntity  _playerEntity;

        // Throttle de CmdSetMoving
        private float _lastMovingCmdTime;
        private const float MOVING_CMD_INTERVAL = 0.1f;

        public bool Dead => CurrentHP <= 0f;

        // ── Servidor: dados autoritativos ──────────────────────────────────
        private CharacterData _serverCharData;
        private DerivedStats  _serverStats;
        private string        _serverAccountUsername; // username da conta (diferente de CharacterName!)

        public DerivedStats ServerStats => _serverStats;

        // ── Lifecycle ──────────────────────────────────────────────────────

        private void Awake()
        {
            _agent        = GetComponent<NavMeshAgent>();
            _animator     = GetComponentInChildren<Animator>();
            _playerEntity = GetComponent<PlayerEntity>();
        }

        public override void OnStartServer()
        {
            All.Add(this);
        }

        public override void OnStopServer()
        {
            All.Remove(this);
        }

        public override void OnStartClient()
        {
            if (nameTagText        != null) nameTagText.text = CharacterName;
            if (selectionIndicator != null) selectionIndicator.SetActive(false);
        }

        public override void OnStartLocalPlayer()
        {
            Debug.Log("[NetworkPlayer] Local player iniciado.");

            _charData = GameManager.Instance?.SelectedCharacter;
            if (_charData == null)
            {
                Debug.LogError("[NetworkPlayer] SelectedCharacter é null!");
                return;
            }

            _playerEntity = GetComponent<PlayerEntity>();
            if (_playerEntity != null)
                _playerEntity.ManagedByNetwork = true;

            string accountUsername = GameManager.Instance?.CurrentAccount?.Username ?? "";

            CmdRegisterCharacter(
                accountUsername,
                _charData.CharacterName,
                _charData.Race.ToString(),
                (int)_charData.Race,
                _charData.Level,
                _charData.Experience,
                _charData.ExperienceToNextLevel,
                _charData.FreeAttributePoints,
                _charData.AllocatedSTR, _charData.AllocatedAGI, _charData.AllocatedVIT,
                _charData.AllocatedDEX, _charData.AllocatedINT, _charData.AllocatedLUK,
                _charData.CurrentHP,
                _charData.CurrentMP,
                _charData.EquipmentBonuses.ATK,  _charData.EquipmentBonuses.DEF,
                _charData.EquipmentBonuses.MATK, _charData.EquipmentBonuses.MDEF
            );
        }

        private void Update()
        {
            if (!isLocalPlayer || Dead) return;

            // Throttle: envia CmdSetMoving no máximo a cada 100 ms
            bool moving = _agent != null && _agent.velocity.sqrMagnitude > 0.05f;
            if (moving != IsMoving && Time.time - _lastMovingCmdTime >= MOVING_CMD_INTERVAL)
            {
                _lastMovingCmdTime = Time.time;
                CmdSetMoving(moving);
            }
        }

        // ── Commands ───────────────────────────────────────────────────────

        [Command]
        private void CmdRegisterCharacter(
            string accountUsername,
            string charName, string raceStr, int raceInt,
            int level, long exp, long expToNext, int freePoints,
            int allocSTR, int allocAGI, int allocVIT,
            int allocDEX, int allocINT, int allocLUK,
            float savedHP, float savedMP,
            float equipATK, float equipDEF, float equipMATK, float equipMDEF)
        {
            // ── Validação server-side (anti-cheat básico) ──────────────────
            level      = Mathf.Clamp(level,      1,    MAX_LEVEL);
            exp        = Math.Clamp(exp,          0,    MAX_EXPERIENCE);
            expToNext  = Math.Clamp(expToNext,    1,    MAX_EXPERIENCE);
            freePoints = Mathf.Clamp(freePoints,  0,    level * 5);
            allocSTR   = Mathf.Clamp(allocSTR,    0,    MAX_ALLOC_STAT);
            allocAGI   = Mathf.Clamp(allocAGI,    0,    MAX_ALLOC_STAT);
            allocVIT   = Mathf.Clamp(allocVIT,    0,    MAX_ALLOC_STAT);
            allocDEX   = Mathf.Clamp(allocDEX,    0,    MAX_ALLOC_STAT);
            allocINT   = Mathf.Clamp(allocINT,    0,    MAX_ALLOC_STAT);
            allocLUK   = Mathf.Clamp(allocLUK,    0,    MAX_ALLOC_STAT);

            _serverAccountUsername = accountUsername;

            var data = new CharacterData
            {
                CharacterName         = charName,
                Race                  = (CharacterRace)raceInt,
                Level                 = level,
                Experience            = exp,
                ExperienceToNextLevel = expToNext,
                FreeAttributePoints   = freePoints,
                AllocatedSTR          = allocSTR,
                AllocatedAGI          = allocAGI,
                AllocatedVIT          = allocVIT,
                AllocatedDEX          = allocDEX,
                AllocatedINT          = allocINT,
                AllocatedLUK          = allocLUK,
                EquipmentBonuses      = new EquipmentBonuses
                {
                    ATK = equipATK, DEF = equipDEF,
                    MATK = equipMATK, MDEF = equipMDEF
                }
            };

            var stats = data.GetDerivedStats();

            // Clamp HP/MP salvos
            float maxHP = Mathf.Min(stats.MaxHP, MAX_HP_CAP);
            float maxMP = Mathf.Min(stats.MaxMP, MAX_MP_CAP);

            CharacterName         = charName;
            Race                  = raceStr;
            Level                 = level;
            Experience            = exp;
            ExperienceToNextLevel = expToNext;
            FreeAttributePoints   = freePoints;
            AllocatedSTR          = allocSTR;
            AllocatedAGI          = allocAGI;
            AllocatedVIT          = allocVIT;
            AllocatedDEX          = allocDEX;
            AllocatedINT          = allocINT;
            AllocatedLUK          = allocLUK;

            MaxHP     = maxHP;
            MaxMP     = maxMP;
            CurrentHP = (savedHP > 0f && savedHP <= maxHP) ? savedHP : maxHP;
            CurrentMP = (savedMP > 0f && savedMP <= maxMP) ? savedMP : maxMP;

            _serverCharData = data;
            _serverStats    = stats;

            Debug.Log($"[Server] {charName} registrado | HP:{CurrentHP:0}/{MaxHP:0} | Lv:{level}");

            RpcInitializeLocalPlayer(
                connectionToClient,
                charName, (CharacterRace)raceInt, level,
                exp, expToNext, freePoints,
                allocSTR, allocAGI, allocVIT, allocDEX, allocINT, allocLUK,
                CurrentHP, CurrentMP,
                equipATK, equipDEF, equipMATK, equipMDEF);
        }

        [Command]
        public void CmdSetMoving(bool moving) => IsMoving = moving;

        [Command]
        public void CmdAllocateAttribute(int attributeIndex)
        {
            if (FreeAttributePoints <= 0)
            {
                Debug.LogWarning($"[Server] {CharacterName}: sem pontos livres para alocar.");
                return;
            }
            if (_serverCharData == null) return;
            if (attributeIndex < 0 || attributeIndex > 5)
            {
                Debug.LogWarning($"[Server] {CharacterName}: índice de atributo inválido ({attributeIndex}).");
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
            MaxHP = Mathf.Min(_serverStats.MaxHP, MAX_HP_CAP);
            MaxMP = Mathf.Min(_serverStats.MaxMP, MAX_MP_CAP);
            if (CurrentHP > MaxHP) CurrentHP = MaxHP;
            if (CurrentMP > MaxMP) CurrentMP = MaxMP;

            ServerSaveCharacter();
            Debug.Log($"[Server] {CharacterName} alocou atributo {attributeIndex} | Pontos: {FreeAttributePoints}");
        }

        [Command]
        public void CmdRequestRespawn() => ServerRespawn();

        // ── Métodos de servidor ────────────────────────────────────────────

        [Server]
        public void ServerApplyDamage(float dmg)
        {
            if (Dead) return;
            CurrentHP = Mathf.Max(0f, CurrentHP - dmg);
            if (CurrentHP <= 0f) ServerDie();
        }

        [Server]
        public void ServerGrantExp(long amount)
        {
            if (_serverCharData == null) return;

            Experience              += amount;
            _serverCharData.Experience += amount;

            bool leveledUp = false;

            while (Experience >= ExperienceToNextLevel && Level < MAX_LEVEL)
            {
                Experience                         -= ExperienceToNextLevel;
                _serverCharData.Experience         -= _serverCharData.ExperienceToNextLevel;

                Level++;
                _serverCharData.Level++;

                FreeAttributePoints              += 5;
                _serverCharData.FreeAttributePoints += 5;

                long nextExp = _serverCharData.GetExperienceForLevel(_serverCharData.Level);
                ExperienceToNextLevel              = nextExp;
                _serverCharData.ExperienceToNextLevel = nextExp;

                _serverStats = _serverCharData.GetDerivedStats();
                MaxHP        = Mathf.Min(_serverStats.MaxHP, MAX_HP_CAP);
                MaxMP        = Mathf.Min(_serverStats.MaxMP, MAX_MP_CAP);

                // Level up: HP/MP cheios
                CurrentHP = MaxHP;
                CurrentMP = MaxMP;
                _serverCharData.CurrentHP = MaxHP;
                _serverCharData.CurrentMP = MaxMP;

                leveledUp = true;
                Debug.Log($"[Server] {CharacterName} → Lv {Level}!");
            }

            ServerSaveCharacter();
            RpcOnExpGained(connectionToClient, amount, leveledUp);
        }

        [Server]
        private void ServerDie()
        {
            CurrentHP = 0f;
            _agent?.ResetPath();
            RpcPlayerDied();
        }

        [Server]
        private void ServerRespawn()
        {
            if (_serverStats == null) return;

            Vector3 pos = GetSpawnPosition();
            transform.position = pos;

            CurrentHP = MaxHP * 0.5f;
            CurrentMP = MaxMP * 0.5f;

            if (_serverCharData != null)
            {
                _serverCharData.CurrentHP = CurrentHP;
                _serverCharData.CurrentMP = CurrentMP;
                ServerSaveCharacter();
            }

            RpcOnRespawned(pos, CurrentHP, MaxHP, CurrentMP, MaxMP);
        }

        [Server]
        private void ServerSaveCharacter()
        {
            if (_serverCharData == null) return;

            _serverCharData.CurrentHP = CurrentHP;
            _serverCharData.CurrentMP = CurrentMP;
            _serverCharData.PosX      = transform.position.x;
            _serverCharData.PosY      = transform.position.y;
            _serverCharData.PosZ      = transform.position.z;

            // CORREÇÃO: carrega a AccountData pela conta correta (não pelo CharacterName)
            if (string.IsNullOrEmpty(_serverAccountUsername))
            {
                Debug.LogWarning($"[Server] {CharacterName}: accountUsername vazio — save ignorado.");
                return;
            }

            var account = SaveManager.Instance?.LoadAccount(_serverAccountUsername);
            if (account == null)
            {
                // Cria uma AccountData mínima (fallback para servidor dedicado sem banco de dados)
                account = new AccountData
                {
                    Username   = _serverAccountUsername,
                    Characters = new System.Collections.Generic.List<CharacterData>()
                };
            }

            SaveManager.Instance?.SaveCharacter(account, _serverCharData);
        }

        [Server]
        private Vector3 GetSpawnPosition()
        {
            if (spawnPoints != null && spawnPoints.Length > 0)
                return spawnPoints[UnityEngine.Random.Range(0, spawnPoints.Length)].position;
            return new Vector3(9f, 0f, 10f);
        }

        // ── ClientRpcs ─────────────────────────────────────────────────────

        [TargetRpc]
        private void RpcInitializeLocalPlayer(
            NetworkConnection target,
            string charName, CharacterRace race, int level,
            long exp, long expToNext, int freePoints,
            int allocSTR, int allocAGI, int allocVIT,
            int allocDEX, int allocINT, int allocLUK,
            float currentHP, float currentMP,
            float equipATK, float equipDEF, float equipMATK, float equipMDEF)
        {
            var data = new CharacterData
            {
                CharacterName         = charName,
                Race                  = race,
                Level                 = level,
                Experience            = exp,
                ExperienceToNextLevel = expToNext,
                FreeAttributePoints   = freePoints,
                AllocatedSTR          = allocSTR,
                AllocatedAGI          = allocAGI,
                AllocatedVIT          = allocVIT,
                AllocatedDEX          = allocDEX,
                AllocatedINT          = allocINT,
                AllocatedLUK          = allocLUK,
                CurrentHP             = currentHP,
                CurrentMP             = currentMP,
                EquipmentBonuses      = new EquipmentBonuses
                {
                    ATK = equipATK, DEF = equipDEF,
                    MATK = equipMATK, MDEF = equipMDEF
                }
            };

            GameManager.Instance?.SetSelectedCharacter(data);

            _playerEntity = GetComponent<PlayerEntity>();
            if (_playerEntity != null)
                _playerEntity.Initialize(data);

            Debug.Log($"[Client] PlayerEntity inicializado via servidor: {charName} Lv{level}");
        }

        [ClientRpc]
        private void RpcPlayerDied()
        {
            if (!isLocalPlayer) return;

            _agent?.ResetPath();
            if (_agent != null) _agent.isStopped = true;

            var ctrl = GetComponent<NetworkPlayerController>();
            if (ctrl != null) ctrl.enabled = false;

            _playerEntity?.OnNetworkDeath();
            DeathScreenUI.Show(this);
        }

        [ClientRpc]
        private void RpcOnRespawned(Vector3 position, float hp, float maxHp, float mp, float maxMp)
        {
            if (!isLocalPlayer) return;

            if (_agent != null)
            {
                _agent.isStopped = false;
                _agent.Warp(position);
            }

            var ctrl = GetComponent<NetworkPlayerController>();
            if (ctrl != null) ctrl.enabled = true;

            if (_playerEntity != null)
            {
                _playerEntity.ForceSetHP(hp, maxHp);
                _playerEntity.ForceSetMP(mp, maxMp);
                _playerEntity.Respawn(position);
            }

            DeathScreenUI.Hide();
        }

        [ClientRpc]
        public void RpcPlayAnimation(string trigger) => _animator?.SetTrigger(trigger);

        [TargetRpc]
        private void RpcOnExpGained(NetworkConnection target, long amount, bool leveledUp)
        {
            FloatingTextManager.Instance?.Show(
                $"+{amount} XP",
                transform.position + Vector3.up * 2f,
                Color.cyan);

            if (leveledUp)
            {
                FloatingTextManager.Instance?.Show(
                    "LEVEL UP!",
                    transform.position + Vector3.up * 2.5f,
                    Color.yellow);
                UIManager.Instance?.ShowMessage("Level up! Você evoluiu!");
            }

            if (_charData != null)
            {
                _charData.Experience            = Experience;
                _charData.ExperienceToNextLevel = ExperienceToNextLevel;
                _charData.Level                 = Level;
                _charData.FreeAttributePoints   = FreeAttributePoints;
            }
        }

        // ── SyncVar Hooks ──────────────────────────────────────────────────

        private void OnNameChanged(string _, string newName)
        {
            if (nameTagText != null) nameTagText.text = newName;
        }

        private void OnHPChanged(float _, float newHP)
        {
            if (hpBarSlider != null)
            {
                hpBarSlider.maxValue = MaxHP;
                hpBarSlider.value    = newHP;
                hpBarSlider.gameObject.SetActive(newHP < MaxHP);
            }

            if (isLocalPlayer && _playerEntity != null && _playerEntity.IsInitialized)
                _playerEntity.SetHPFromNetwork(newHP, MaxHP);
        }

        private void OnMaxHPChanged(float _, float newMaxHP)
        {
            if (hpBarSlider != null) hpBarSlider.maxValue = newMaxHP;
            if (isLocalPlayer && _playerEntity != null && _playerEntity.IsInitialized)
                _playerEntity.SetHPFromNetwork(CurrentHP, newMaxHP);
        }

        private void OnMPChanged(float _, float newMP)
        {
            if (isLocalPlayer && _playerEntity != null && _playerEntity.IsInitialized)
                _playerEntity.SetMPFromNetwork(newMP, MaxMP);
        }

        private void OnMaxMPChanged(float _, float newMaxMP)
        {
            if (isLocalPlayer && _playerEntity != null && _playerEntity.IsInitialized)
                _playerEntity.SetMPFromNetwork(CurrentMP, newMaxMP);
        }

        private void OnLevelChanged(int _, int newLevel)
        {
            if (!isLocalPlayer) return;
            if (_charData != null) _charData.Level = newLevel;
            UIManager.Instance?.RefreshLevel(newLevel);
        }

        private void OnExpChanged(long _, long newExp)
        {
            if (!isLocalPlayer) return;
            if (_charData != null)
            {
                _charData.Experience            = newExp;
                _charData.ExperienceToNextLevel = ExperienceToNextLevel;
            }
        }

        private void OnFreePointsChanged(int _, int newPoints)
        {
            if (!isLocalPlayer) return;
            if (_charData != null) _charData.FreeAttributePoints = newPoints;
            AttributeWindowUI.Instance?.OnFreePointsUpdated(newPoints);
        }

        private void OnMovingChanged(bool _, bool newVal)
        {
            if (!isLocalPlayer)
                _animator?.SetBool("IsMoving", newVal);
        }
    }
}