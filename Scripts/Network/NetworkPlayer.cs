using UnityEngine;
using UnityEngine.AI;
using Mirror;
using RPG.Data;
using RPG.UI;
using RPG.Managers;
using RPG.Character;
using RPG.Combat;
using System.Collections.Generic;
using System;

namespace RPG.Network
{
    /// <summary>
    /// NetworkPlayer v6 — TOTALMENTE SERVER-AUTHORITATIVE
    ///
    /// CORREÇÕES v6:
    ///   - Usa PlayerEntity.InitializeFromServer() em vez de Initialize().
    ///   - Usa PlayerEntity.OnServerDeath() em vez de OnNetworkDeath().
    ///   - Usa PlayerEntity.OnServerRespawn() em vez de ForceSetHP/ForceSetMP/Respawn.
    ///   - Usa PlayerEntity.SetHPFromServer() / SetMPFromServer().
    ///   - Removido ManagedByNetwork (PlayerEntity não tem mais este campo).
    ///   - Adicionados ServerConsumeMP / RpcSkillConfirmed / RpcSkillRejected
    ///     (chamados pelo NetworkMonsterEntity ao processar skills).
    ///   - Cooldowns de skill gerenciados no servidor (_serverSkillCooldowns).
    ///   - FreeAttributePoints no ServerGrantExp corrigido (estava +1+4 por loop).
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    [RequireComponent(typeof(NetworkIdentity))]
    [RequireComponent(typeof(NetworkTransformReliable))]
    public class NetworkPlayer : NetworkBehaviour, ITargetable
    {
        // ── Registro estático (servidor) ───────────────────────────────────
        public static readonly HashSet<NetworkPlayer> All = new HashSet<NetworkPlayer>();

        // ── Caps anti-cheat ────────────────────────────────────────────────
        private const int   MAX_LEVEL  = 99;
        private const float MAX_HP_CAP = 500_000f;
        private const float MAX_MP_CAP = 200_000f;

        // ── SyncVars ───────────────────────────────────────────────────────
        [SyncVar(hook = nameof(OnNetNameChanged))]
        public string CharacterName = "...";

        [SyncVar]
        public string RaceStr = "Human";

        [SyncVar(hook = nameof(OnNetLevelChanged))]
        public int Level = 1;

        [SyncVar(hook = nameof(OnNetHPChanged))]
        public float CurrentHP = 0f;

        [SyncVar(hook = nameof(OnNetMaxHPChanged))]
        public float MaxHP = 1f;

        [SyncVar(hook = nameof(OnNetMPChanged))]
        public float CurrentMP = 0f;

        [SyncVar(hook = nameof(OnNetMaxMPChanged))]
        public float MaxMP = 1f;

        [SyncVar(hook = nameof(OnNetMovingChanged))]
        public bool IsMoving = false;

        [SyncVar]
        public long Experience = 0;

        [SyncVar]
        public long ExperienceToNextLevel = 100;

        [SyncVar(hook = nameof(OnNetFreePointsChanged))]
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

        // ── Componentes serializados ───────────────────────────────────────
        [Header("Visuals")]
        [SerializeField] private GameObject            selectionIndicator;
        [SerializeField] private TMPro.TMP_Text        nameTagText;
        [SerializeField] private UnityEngine.UI.Slider hpBarSlider;

        [Header("Spawn Points (fallback)")]
        [SerializeField] private Transform[] spawnPoints;

        // ── Componentes em runtime ─────────────────────────────────────────
        private NavMeshAgent _agent;
        private Animator     _animator;
        private PlayerEntity _playerEntity;

        // ── Estado público ─────────────────────────────────────────────────
        public bool Dead => CurrentHP <= 0f;

        // ── Dados autoritativos no servidor ───────────────────────────────
        private CharacterData _serverCharData;
        private DerivedStats  _serverStats;
        private string        _serverAccountUsername;

        /// <summary>Stats autoritativos — acessados pelo NetworkMonsterEntity.</summary>
        public DerivedStats ServerStats => _serverStats;

        // ── Cooldowns de skill no servidor (anti-cheat) ───────────────────
        // Chave = índice da skill, Valor = timestamp em que o cooldown termina
        private readonly Dictionary<int, float> _serverSkillCooldowns = new();

        // ── Throttle de IsMoving ───────────────────────────────────────────
        private float _lastMovingCmdTime;
        private const float MOVING_CMD_INTERVAL = 0.1f;

        // ── Lifecycle ──────────────────────────────────────────────────────

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
            _playerEntity = GetComponent<PlayerEntity>();
            Debug.Log("[NetworkPlayer] Local player ativo — aguardando RpcInitializeLocalPlayer.");
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

        // ── Inicialização pelo servidor ────────────────────────────────────

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

            if (charData.PosX != 0f || charData.PosY != 0f || charData.PosZ != 0f)
            {
                var pos = new Vector3(charData.PosX, charData.PosY, charData.PosZ);
                transform.position = pos;
                _agent?.Warp(pos);
            }

            Debug.Log($"[Server] {charData.CharacterName} inicializado | " +
                      $"HP:{CurrentHP:0}/{MaxHP:0} | Lv:{Level} | Conta:{accountUsername}");

            RpcInitializeLocalPlayer(
                charData.CharacterName, charData.Race, charData.Level,
                charData.Experience, charData.ExperienceToNextLevel,
                charData.FreeAttributePoints,
                charData.AllocatedSTR, charData.AllocatedAGI, charData.AllocatedVIT,
                charData.AllocatedDEX, charData.AllocatedINT, charData.AllocatedLUK,
                CurrentHP, CurrentMP,
                charData.EquipmentBonuses.ATK,  charData.EquipmentBonuses.DEF,
                charData.EquipmentBonuses.MATK, charData.EquipmentBonuses.MDEF
            );
        }

        // ── Commands (cliente → servidor) ──────────────────────────────────

        [Command]
        public void CmdSetMoving(bool moving) => IsMoving = moving;

        [Command]
        public void CmdAllocateAttribute(int attributeIndex)
        {
            if (FreeAttributePoints <= 0 || _serverCharData == null) return;
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

        // ── Self Skill (heal / buff) — Command para o próprio player ───────

        /// <summary>
        /// Cliente pede ao servidor para executar uma skill de self/heal.
        /// Servidor valida MP, cooldown e executa o efeito.
        /// </summary>
        [Command]
        public void CmdRequestSelfSkill(int skillIndex)
        {
            if (Dead) return;
            if (_serverStats == null) return;

            var skillSystem = GetComponent<SkillSystem>();
            var skill = skillSystem?.GetSkill(skillIndex);
            if (skill == null)
            {
                RpcSkillRejected(connectionToClient, skillIndex, "Skill inválida.");
                return;
            }

            // Valida cooldown no servidor
            if (_serverSkillCooldowns.TryGetValue(skillIndex, out float endTime) &&
                Time.time < endTime)
            {
                float remaining = endTime - Time.time;
                RpcSkillRejected(connectionToClient, skillIndex,
                    $"{skill.Name}: aguarde {remaining:0.0}s");
                return;
            }

            // Valida MP
            if (CurrentMP < skill.ManaCost)
            {
                RpcSkillRejected(connectionToClient, skillIndex, "MP insuficiente!");
                return;
            }

            // Consome MP
            ServerConsumeMP(skill.ManaCost);

            // Registra cooldown no servidor
            _serverSkillCooldowns[skillIndex] = Time.time + skill.Cooldown;

            // Aplica efeito
            if (skill.Type == SkillType.Heal)
            {
                float heal = Mathf.Max(10f, (_serverStats.MATK) * skill.AtkMultiplier);
                CurrentHP  = Mathf.Min(MaxHP, CurrentHP + heal);
                _serverCharData.CurrentHP = CurrentHP;
            }
            // Buffs: implementar futuramente

            // Confirma para o cliente
            RpcSkillConfirmed(connectionToClient, skillIndex, skill.Cooldown);
        }

        // ── Métodos de servidor (chamados pelo NetworkMonsterEntity) ────────

        [Server]
        public void ServerApplyDamage(float dmg)
        {
            if (Dead) return;
            CurrentHP = Mathf.Max(0f, CurrentHP - dmg);
            if (CurrentHP <= 0f) ServerDie();
        }

        /// <summary>
        /// Consome MP no servidor. Chamado pelo NetworkMonsterEntity
        /// após validar a skill do atacante.
        /// </summary>
        [Server]
        public void ServerConsumeMP(float amount)
        {
            CurrentMP = Mathf.Max(0f, CurrentMP - amount);
            if (_serverCharData != null) _serverCharData.CurrentMP = CurrentMP;
        }

        /// <summary>
        /// Verifica e registra cooldown de skill no servidor.
        /// Retorna true se a skill pode ser usada (cooldown expirado).
        /// </summary>
        [Server]
        public bool ServerCheckAndSetCooldown(int skillIndex, float cooldownDuration)
        {
            if (_serverSkillCooldowns.TryGetValue(skillIndex, out float endTime) &&
                Time.time < endTime)
                return false;

            _serverSkillCooldowns[skillIndex] = Time.time + cooldownDuration;
            return true;
        }

        [Server]
        public void ServerGrantExp(long amount)
        {
            if (_serverCharData == null) return;

            Experience                 += amount;
            _serverCharData.Experience += amount;

            bool leveledUp = false;

            while (Experience >= ExperienceToNextLevel && Level < MAX_LEVEL)
            {
                Experience                            -= ExperienceToNextLevel;
                _serverCharData.Experience            -= _serverCharData.ExperienceToNextLevel;

                Level++;
                _serverCharData.Level++;

                // +5 pontos por nível (corrigido — não usar += 1 + += 4)
                FreeAttributePoints                  += 5;
                _serverCharData.FreeAttributePoints  += 5;

                long nextExp = _serverCharData.GetExperienceForLevel(_serverCharData.Level);
                ExperienceToNextLevel                = nextExp;
                _serverCharData.ExperienceToNextLevel = nextExp;

                _serverStats = _serverCharData.GetDerivedStats();
                MaxHP        = Mathf.Min(_serverStats.MaxHP, MAX_HP_CAP);
                MaxMP        = Mathf.Min(_serverStats.MaxMP, MAX_MP_CAP);
                CurrentHP    = MaxHP;
                CurrentMP    = MaxMP;
                _serverCharData.CurrentHP = MaxHP;
                _serverCharData.CurrentMP = MaxMP;

                leveledUp = true;
                Debug.Log($"[Server] {CharacterName} → Lv {Level}!");
            }

            ServerSaveCharacter();
            RpcOnExpGained(amount, leveledUp);
        }

        // ── ClientRpcs para skills ─────────────────────────────────────────

        /// <summary>
        /// Informa o cliente que a skill foi aceita pelo servidor.
        /// O cliente então inicia o cooldown visual e o feedback de UI.
        /// </summary>
        [TargetRpc]
        public void RpcSkillConfirmed(NetworkConnection target, int skillIndex, float cooldown)
        {
            GetComponent<SkillSystem>()?.OnServerSkillConfirmed(skillIndex, cooldown);
        }

        /// <summary>
        /// Informa o cliente que a skill foi rejeitada (MP, cooldown, range).
        /// </summary>
        [TargetRpc]
        public void RpcSkillRejected(NetworkConnection target, int skillIndex, string reason)
        {
            GetComponent<SkillSystem>()?.OnServerSkillRejected(skillIndex, reason);
        }

        // ── Morte / Respawn ────────────────────────────────────────────────

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
                Debug.LogWarning($"[Server] Conta '{_serverAccountUsername}' não encontrada.");
                return;
            }
            SaveManager.Instance?.SaveCharacter(account, _serverCharData);
        }

        [Server]
        private Vector3 GetSpawnPosition()
        {
            if (spawnPoints != null && spawnPoints.Length > 0)
                return spawnPoints[UnityEngine.Random.Range(0, spawnPoints.Length)].position;
            return Vector3.zero;
        }

        // ── ClientRpcs ─────────────────────────────────────────────────────

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
                EquipmentBonuses = new EquipmentBonuses
                {
                    ATK = equipATK, DEF = equipDEF,
                    MATK = equipMATK, MDEF = equipMDEF
                }
            };

            _playerEntity = GetComponent<PlayerEntity>();
            // Usa InitializeFromServer — método correto do PlayerEntity novo
            _playerEntity?.InitializeFromServer(data);

            Debug.Log($"[Client] PlayerEntity inicializado via servidor: {charName} Lv{level}");
        }

        [ClientRpc]
        private void RpcPlayerDied()
        {
            if (!isLocalPlayer) return;
            _agent?.ResetPath();
            if (_agent != null) _agent.isStopped = true;
            GetComponent<NetworkPlayerController>()?.SetEnabled(false);
            // Usa OnServerDeath — método correto do PlayerEntity novo
            _playerEntity?.OnServerDeath();
            DeathScreenUI.Show(this);
        }

        [ClientRpc]
        private void RpcOnRespawned(Vector3 position, float hp, float maxHp, float mp, float maxMp)
        {
            if (!isLocalPlayer) return;
            if (_agent != null) { _agent.isStopped = false; _agent.Warp(position); }
            GetComponent<NetworkPlayerController>()?.SetEnabled(true);
            // Usa OnServerRespawn — método correto do PlayerEntity novo
            _playerEntity?.OnServerRespawn(position, hp, maxHp, mp, maxMp);
            DeathScreenUI.Hide();
        }

        [ClientRpc]
        public void RpcPlayAnimation(string trigger) => _animator?.SetTrigger(trigger);

        [ClientRpc]
        private void RpcOnExpGained(long amount, bool leveledUp)
        {
            if (!isLocalPlayer) return;

            FloatingTextManager.Instance?.Show(
                $"+{amount} XP", transform.position + Vector3.up * 2f, Color.cyan);

            if (leveledUp)
            {
                FloatingTextManager.Instance?.Show(
                    "LEVEL UP!", transform.position + Vector3.up * 2.5f, Color.yellow);
                UIManager.Instance?.ShowMessage("Level up! Você evoluiu!");
            }
        }

        // ── SyncVar Hooks ──────────────────────────────────────────────────

        private void OnNetNameChanged(string _, string v)
        {
            if (nameTagText != null) nameTagText.text = v;
        }

        private void OnNetHPChanged(float _, float newHP)
        {
            if (hpBarSlider != null)
            {
                hpBarSlider.maxValue = MaxHP;
                hpBarSlider.value    = newHP;
                hpBarSlider.gameObject.SetActive(newHP < MaxHP);
            }
            // Usa SetHPFromServer — método correto do PlayerEntity novo
            if (isLocalPlayer && _playerEntity != null && _playerEntity.IsInitialized)
                _playerEntity.SetHPFromServer(newHP, MaxHP);
        }

        private void OnNetMaxHPChanged(float _, float newMax)
        {
            if (hpBarSlider != null) hpBarSlider.maxValue = newMax;
            if (isLocalPlayer && _playerEntity != null && _playerEntity.IsInitialized)
                _playerEntity.SetHPFromServer(CurrentHP, newMax);
        }

        private void OnNetMPChanged(float _, float newMP)
        {
            if (isLocalPlayer && _playerEntity != null && _playerEntity.IsInitialized)
                _playerEntity.SetMPFromServer(newMP, MaxMP);
        }

        private void OnNetMaxMPChanged(float _, float newMax)
        {
            if (isLocalPlayer && _playerEntity != null && _playerEntity.IsInitialized)
                _playerEntity.SetMPFromServer(CurrentMP, newMax);
        }

        private void OnNetLevelChanged(int _, int v)
        {
            if (!isLocalPlayer) return;
            UIManager.Instance?.RefreshLevel(v);
        }

        private void OnNetFreePointsChanged(int _, int v)
        {
            if (!isLocalPlayer) return;
            AttributeWindowUI.Instance?.OnFreePointsUpdated(v);
        }

        private void OnNetMovingChanged(bool _, bool v)
        {
            if (!isLocalPlayer) _animator?.SetBool("IsMoving", v);
        }
    }
}
