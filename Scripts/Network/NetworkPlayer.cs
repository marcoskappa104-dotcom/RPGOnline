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
    /// NetworkPlayer v5 — TOTALMENTE SERVER-AUTHORITATIVE
    ///
    /// FLUXO:
    ///   1. RPGNetworkManager spawna o player e chama ServerInitialize(charData, username).
    ///   2. Servidor calcula tudo e seta SyncVars.
    ///   3. RpcInitializeLocalPlayer envia os dados ao cliente local para exibição.
    ///   4. NENHUM dado de gameplay (HP, dano, XP) vem do cliente.
    ///
    /// REMOVIDO:
    ///   - CmdRegisterCharacter (dados vêm direto do servidor via ServerInitialize).
    ///   - Qualquer leitura de GameManager no lado server.
    ///   - SelectedCharacter / CurrentAccount no servidor.
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    [RequireComponent(typeof(NetworkIdentity))]
    [RequireComponent(typeof(NetworkTransformReliable))]
    public class NetworkPlayer : NetworkBehaviour, ITargetable
    {
        // ── Registro estático (servidor) ───────────────────────────────
        public static readonly HashSet<NetworkPlayer> All = new HashSet<NetworkPlayer>();

        // ── Limites anti-cheat ─────────────────────────────────────────
        private const int   MAX_LEVEL      = 99;
        private const float MAX_HP_CAP     = 500_000f;
        private const float MAX_MP_CAP     = 200_000f;

        // ── SyncVars ───────────────────────────────────────────────────
        [SyncVar(hook = nameof(OnNameChanged))]
        public string CharacterName = "...";

        [SyncVar]
        public string RaceStr = "Human";

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

        // ── ITargetable ────────────────────────────────────────────────
        string  ITargetable.DisplayName => CharacterName;
        float   ITargetable.CurrentHP   => CurrentHP;
        float   ITargetable.MaxHP       => MaxHP;
        bool    ITargetable.IsDead      => Dead;
        Vector3 ITargetable.Position    => transform.position;

        public void OnSelected()   { if (selectionIndicator) selectionIndicator.SetActive(true);  }
        public void OnDeselected() { if (selectionIndicator) selectionIndicator.SetActive(false); }
        public void TakeDamage(float rawAtk, float rawMatk, bool isPhysical)
            => Debug.Log("[NetworkPlayer] PvP não implementado.");

        // ── Componentes ────────────────────────────────────────────────
        private NavMeshAgent _agent;
        private Animator     _animator;
        private PlayerEntity _playerEntity;

        [Header("Visuals")]
        [SerializeField] private GameObject            selectionIndicator;
        [SerializeField] private TMPro.TMP_Text        nameTagText;
        [SerializeField] private UnityEngine.UI.Slider hpBarSlider;

        [Header("Spawn Points (fallback)")]
        [SerializeField] private Transform[] spawnPoints;

        // ── Throttle de IsMoving ───────────────────────────────────────
        private float _lastMovingCmdTime;
        private const float MOVING_CMD_INTERVAL = 0.1f;

        public bool Dead => CurrentHP <= 0f;

        // ── Dados autoritativos (SERVIDOR) ─────────────────────────────
        private CharacterData _serverCharData;
        private DerivedStats  _serverStats;
        private string        _serverAccountUsername;

        public DerivedStats ServerStats => _serverStats;

        // ── Lifecycle ──────────────────────────────────────────────────

        private void Awake()
        {
            _agent        = GetComponent<NavMeshAgent>();
            _animator     = GetComponentInChildren<Animator>();
            _playerEntity = GetComponent<PlayerEntity>();
        }

        public override void OnStartServer() => All.Add(this);
        public override void OnStopServer()  => All.Remove(this);

        public override void OnStartClient()
        {
            if (nameTagText        != null) nameTagText.text = CharacterName;
            if (selectionIndicator != null) selectionIndicator.SetActive(false);
        }

        public override void OnStartLocalPlayer()
        {
            Debug.Log("[NetworkPlayer] Local player ativo — aguardando RpcInitializeLocalPlayer.");

            if (_playerEntity != null)
                _playerEntity.ManagedByNetwork = true;
        }

        private void Update()
        {
            if (!isLocalPlayer || Dead) return;

            bool moving = _agent != null && _agent.velocity.sqrMagnitude > 0.05f;
            if (moving != IsMoving && Time.time - _lastMovingCmdTime >= MOVING_CMD_INTERVAL)
            {
                _lastMovingCmdTime = Time.time;
                CmdSetMoving(moving);
            }
        }

        // ── Inicialização pelo servidor ────────────────────────────────

        /// <summary>
        /// Chamado SOMENTE pelo RPGNetworkManager após spawn.
        /// Nunca chamado pelo cliente.
        /// </summary>
        [Server]
        public void ServerInitialize(CharacterData charData, string accountUsername)
        {
            _serverAccountUsername = accountUsername;
            _serverCharData        = charData;
            _serverStats           = charData.GetDerivedStats();

            float maxHP = Mathf.Min(_serverStats.MaxHP, MAX_HP_CAP);
            float maxMP = Mathf.Min(_serverStats.MaxMP, MAX_MP_CAP);

            // Seta SyncVars
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

            MaxHP     = maxHP;
            MaxMP     = maxMP;
            CurrentHP = (charData.CurrentHP > 0f && charData.CurrentHP <= maxHP)
                        ? charData.CurrentHP : maxHP;
            CurrentMP = (charData.CurrentMP > 0f && charData.CurrentMP <= maxMP)
                        ? charData.CurrentMP : maxMP;

            // Posiciona o player
            if (charData.PosX != 0f || charData.PosY != 0f || charData.PosZ != 0f)
            {
                var pos = new Vector3(charData.PosX, charData.PosY, charData.PosZ);
                transform.position = pos;
                _agent?.Warp(pos);
            }

            Debug.Log($"[Server] {charData.CharacterName} inicializado | " +
                      $"HP:{CurrentHP:0}/{MaxHP:0} | Lv:{Level} | Conta:{accountUsername}");

            // Inicializa o cliente local
            RpcInitializeLocalPlayer(
                charData.CharacterName,
                charData.Race,
                charData.Level,
                charData.Experience,
                charData.ExperienceToNextLevel,
                charData.FreeAttributePoints,
                charData.AllocatedSTR, charData.AllocatedAGI, charData.AllocatedVIT,
                charData.AllocatedDEX, charData.AllocatedINT, charData.AllocatedLUK,
                CurrentHP, CurrentMP,
                charData.EquipmentBonuses.ATK,  charData.EquipmentBonuses.DEF,
                charData.EquipmentBonuses.MATK, charData.EquipmentBonuses.MDEF
            );
        }

        // ── Commands ───────────────────────────────────────────────────

        [Command]
        public void CmdSetMoving(bool moving) => IsMoving = moving;

        [Command]
        public void CmdAllocateAttribute(int attributeIndex)
        {
            if (FreeAttributePoints <= 0) return;
            if (_serverCharData == null) return;
            if (attributeIndex < 0 || attributeIndex > 5) return;

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
        }

        [Command]
        public void CmdRequestRespawn() => ServerRespawn();

        // ── Métodos de servidor ────────────────────────────────────────

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

            Experience += amount;
            _serverCharData.Experience += amount;

            bool leveledUp = false;

            while (Experience >= ExperienceToNextLevel && Level < MAX_LEVEL)
            {
                Experience                            -= ExperienceToNextLevel;
                _serverCharData.Experience            -= _serverCharData.ExperienceToNextLevel;

                Level++;
                _serverCharData.Level++;

                FreeAttributePoints++;
                _serverCharData.FreeAttributePoints += 5;
                // Correção: era += 5 mas o loop incrementa 1 por ciclo, somando 5 em bloco
                FreeAttributePoints += 4; // total +5

                long nextExp = _serverCharData.GetExperienceForLevel(_serverCharData.Level);
                ExperienceToNextLevel = nextExp;
                _serverCharData.ExperienceToNextLevel = nextExp;

                _serverStats = _serverCharData.GetDerivedStats();
                MaxHP = Mathf.Min(_serverStats.MaxHP, MAX_HP_CAP);
                MaxMP = Mathf.Min(_serverStats.MaxMP, MAX_MP_CAP);
                CurrentHP = MaxHP;
                CurrentMP = MaxMP;
                _serverCharData.CurrentHP = MaxHP;
                _serverCharData.CurrentMP = MaxMP;

                leveledUp = true;
                Debug.Log($"[Server] {CharacterName} → Lv {Level}!");
            }

            ServerSaveCharacter();
            RpcOnExpGained(amount, leveledUp);
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
            _agent?.Warp(pos);

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
        public void ServerSaveCharacter()
        {
            if (_serverCharData == null || string.IsNullOrEmpty(_serverAccountUsername)) return;

            _serverCharData.CurrentHP = CurrentHP;
            _serverCharData.CurrentMP = CurrentMP;
            _serverCharData.PosX      = transform.position.x;
            _serverCharData.PosY      = transform.position.y;
            _serverCharData.PosZ      = transform.position.z;

            var account = SaveManager.Instance?.LoadAccount(_serverAccountUsername);
            if (account == null)
            {
                Debug.LogWarning($"[Server] Conta '{_serverAccountUsername}' não encontrada para save.");
                return;
            }
            SaveManager.Instance?.SaveCharacter(account, _serverCharData);
        }

        [Server]
        private Vector3 GetSpawnPosition()
        {
            if (spawnPoints != null && spawnPoints.Length > 0)
                return spawnPoints[UnityEngine.Random.Range(0, spawnPoints.Length)].position;
            return new Vector3(0f, 0f, 0f);
        }

        // ── ClientRpcs ─────────────────────────────────────────────────

        [TargetRpc]
        private void RpcInitializeLocalPlayer(
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

            _playerEntity = GetComponent<PlayerEntity>();
            if (_playerEntity != null)
            {
                _playerEntity.ManagedByNetwork = true;
                _playerEntity.Initialize(data);
            }

            Debug.Log($"[Client] PlayerEntity inicializado: {charName} Lv{level}");
        }

        [ClientRpc]
        private void RpcPlayerDied()
        {
            if (!isLocalPlayer) return;
            _agent?.ResetPath();
            if (_agent != null) _agent.isStopped = true;
            GetComponent<NetworkPlayerController>()?.SetEnabled(false);
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
            GetComponent<NetworkPlayerController>()?.SetEnabled(true);
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

        [ClientRpc]
        private void RpcOnExpGained(long amount, bool leveledUp)
        {
            if (!isLocalPlayer) return;

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
        }

        // ── SyncVar Hooks ──────────────────────────────────────────────

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
            UIManager.Instance?.RefreshLevel(newLevel);
        }

        private void OnExpChanged(long _, long newExp)
        {
            // UI atualiza via UpdateExpBar() no UIManager que lê as SyncVars
        }

        private void OnFreePointsChanged(int _, int newPoints)
        {
            if (!isLocalPlayer) return;
            AttributeWindowUI.Instance?.OnFreePointsUpdated(newPoints);
        }

        private void OnMovingChanged(bool _, bool newVal)
        {
            if (!isLocalPlayer)
                _animator?.SetBool("IsMoving", newVal);
        }
    }
}
