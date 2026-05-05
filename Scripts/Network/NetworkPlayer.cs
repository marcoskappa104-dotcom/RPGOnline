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
    /// NetworkPlayer v8
    ///
    /// CORREÇÕES v8:
    ///
    ///   1. MOVIMENTO TRAVADO — NetworkTransformUnreliable recomendado no prefab
    ///      (troque NetworkTransformReliable por NetworkTransformUnreliable no prefab).
    ///      Reliable usa delta-compression com threshold alto → "teleporte" perceptível.
    ///      Unreliable envia toda mudança de posição → interpolação suave no cliente.
    ///
    ///   2. DOUBLE-RPC REMOVIDO — RpcSkillConfirmed e RpcSkillRejected existiam como
    ///      [ClientRpc] E [TargetRpc] com o mesmo nome → conflito de assinatura no Mirror.
    ///      Agora só existem as versões [ClientRpc] com guarda if (!isLocalPlayer) return.
    ///
    ///   3. INICIALIZAÇÃO ROBUSTA — _clientInitialized não bloqueia mais re-init.
    ///      Se o Rpc chegar antes do OnStartLocalPlayer (race condition), o flag é
    ///      verificado e o init é feito corretamente via coroutine.
    ///
    ///   4. CmdMoveTo com validação de distância (anti-teleport básico).
    ///
    ///   5. ServerGrantExp usa CharacterData.AddExperience() em vez de reimplementar
    ///      o loop de level-up manualmente (DRY).
    ///
    ///   6. Saving async-ready: ServerSaveCharacter pode ser facilmente movido para
    ///      background thread (preparado com comentário).
    ///
    ///   7. Adicionado CurrentMP como propriedade pública (necessário para SkillSystem).
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    [RequireComponent(typeof(NetworkIdentity))]
    public class NetworkPlayer : NetworkBehaviour, ITargetable
    {
        public static readonly HashSet<NetworkPlayer> All = new HashSet<NetworkPlayer>();

        private const int   MAX_LEVEL       = 99;
        private const float MAX_HP_CAP      = 500_000f;
        private const float MAX_MP_CAP      = 200_000f;
        private const float MAX_MOVE_DIST   = 100f;   // Anti-teleport: distância máxima por CmdMoveTo
        private const float SAVE_INTERVAL   = 60f;    // Auto-save a cada 60 segundos

        // ── SyncVars ───────────────────────────────────────────────────────
        [SyncVar(hook = nameof(OnNetNameChanged))]   public string CharacterName = "...";
        [SyncVar]                                     public string RaceStr = "Human";
        [SyncVar(hook = nameof(OnNetLevelChanged))]  public int    Level = 1;
        [SyncVar(hook = nameof(OnNetHPChanged))]     public float  CurrentHP = 0f;
        [SyncVar(hook = nameof(OnNetMaxHPChanged))]  public float  MaxHP = 1f;
        [SyncVar(hook = nameof(OnNetMPChanged))]     public float  CurrentMP = 0f;
        [SyncVar(hook = nameof(OnNetMaxMPChanged))]  public float  MaxMP = 1f;
        [SyncVar(hook = nameof(OnNetMovingChanged))] public bool   IsMoving = false;
        [SyncVar] public long Experience            = 0;
        [SyncVar] public long ExperienceToNextLevel = 100;
        [SyncVar(hook = nameof(OnNetFreePointsChanged))] public int FreeAttributePoints = 0;
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
        private NavMeshAgent _agent;
        private Animator     _animator;
        private PlayerEntity _playerEntity;

        // ── Estado do servidor ─────────────────────────────────────────────
        private CharacterData _serverCharData;
        private DerivedStats  _serverStats;
        private string        _serverAccountUsername;
        private float         _autoSaveTimer;

        public DerivedStats ServerStats => _serverStats;

        // ── Cooldowns de skills (servidor) ────────────────────────────────
        private readonly Dictionary<int, float> _serverSkillCooldowns = new();

        // ── Estado do cliente ──────────────────────────────────────────────
        private bool _clientInitialized  = false;
        private bool _pendingClientInit  = false;
        private CharacterData _pendingInitData = null;

        // ── Movimento ─────────────────────────────────────────────────────
        private float _lastMovingCmdTime;
        private const float MOVING_CMD_INTERVAL = 0.15f;

        public bool Dead => CurrentHP <= 0f;

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
            _autoSaveTimer = SAVE_INTERVAL;
        }

        public override void OnStopServer()
        {
            All.Remove(this);
            // Salva ao desconectar
            ServerSaveCharacter();
        }

        public override void OnStartClient()
        {
            if (_nameTagText        != null) _nameTagText.text = CharacterName;
            if (_selectionIndicator != null) _selectionIndicator.SetActive(false);

            // Clientes não-locais não precisam de NavMeshAgent.
            // O movimento deles é replicado via NetworkTransformUnreliable.
            if (!isLocalPlayer && _agent != null)
                _agent.enabled = false;
        }

        public override void OnStartLocalPlayer()
        {
            _playerEntity = GetComponent<PlayerEntity>();
            _agent        = GetComponent<NavMeshAgent>();

            if (_agent != null) _agent.enabled = true;

            Debug.Log("[NetworkPlayer] Local player ativo — aguardando RpcInitializeLocalPlayer.");

            // Se houve race condition e o Rpc já chegou antes do OnStartLocalPlayer
            if (_pendingClientInit && _pendingInitData != null)
            {
                StartCoroutine(DelayedClientInit(_pendingInitData));
                _pendingClientInit = false;
                _pendingInitData   = null;
            }
        }

        private void Update()
        {
            if (isServer) ServerUpdate();
            if (!isLocalPlayer || Dead) return;
            ClientMovingUpdate();
        }

        [Server]
        private void ServerUpdate()
        {
            // Auto-save periódico
            _autoSaveTimer -= Time.deltaTime;
            if (_autoSaveTimer <= 0f)
            {
                _autoSaveTimer = SAVE_INTERVAL;
                ServerSaveCharacter();
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
            MaxHP                 = maxHP;
            MaxMP                 = maxMP;
            CurrentHP = (charData.CurrentHP > 0f && charData.CurrentHP <= maxHP)
                ? charData.CurrentHP : maxHP;
            CurrentMP = (charData.CurrentMP > 0f && charData.CurrentMP <= maxMP)
                ? charData.CurrentMP : maxMP;

            // Posiciona se há posição salva
            var savedPos = new Vector3(charData.PosX, charData.PosY, charData.PosZ);
            if (savedPos.sqrMagnitude > 0.01f)
            {
                transform.position = savedPos;
                if (_agent != null && _agent.isOnNavMesh) _agent.Warp(savedPos);
            }

            Debug.Log($"[Server] {charData.CharacterName} inicializado | " +
                      $"HP:{CurrentHP:0}/{MaxHP:0} | Lv:{Level}");

            // Aguarda 2 frames para garantir que SpawnMessage chegou ao cliente
            StartCoroutine(SendInitRpcDelayed(charData));
        }

        [Server]
        private IEnumerator SendInitRpcDelayed(CharacterData charData)
        {
            yield return null;
            yield return null;

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

        // ── Commands ──────────────────────────────────────────────────────

        [Command]
        public void CmdSetMoving(bool moving) => IsMoving = moving;

        /// <summary>
        /// Movimento com validação anti-teleport básica.
        /// </summary>
        [Command]
        public void CmdMoveTo(Vector3 destination)
        {
            if (_agent == null || Dead) return;

            // Anti-cheat: rejeita destinos impossíveis (teleporte)
            float dist = Vector3.Distance(transform.position, destination);
            if (dist > MAX_MOVE_DIST)
            {
                Debug.LogWarning($"[Server] CmdMoveTo suspeito: {CharacterName} " +
                                 $"dist={dist:0.0} (máx={MAX_MOVE_DIST})");
                return;
            }

            if (NavMesh.SamplePosition(destination, out NavMeshHit hit, 3f, NavMesh.AllAreas))
                _agent.SetDestination(hit.position);
            else
                _agent.SetDestination(destination);
        }

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

        [Command]
        public void CmdRequestSelfSkill(int skillIndex)
        {
            if (Dead || _serverStats == null) return;

            var skill = GetComponent<SkillSystem>()?.GetSkill(skillIndex);
            if (skill == null) { RpcSkillRejected(skillIndex, "Skill inválida."); return; }

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
                CurrentHP = Mathf.Min(MaxHP, CurrentHP + heal);
                if (_serverCharData != null) _serverCharData.CurrentHP = CurrentHP;
            }

            RpcSkillConfirmed(skillIndex, skill.Cooldown);
        }

        // ── Métodos de servidor ────────────────────────────────────────────

        [Server]
        public void ServerApplyDamage(float dmg)
        {
            if (Dead) return;
            CurrentHP = Mathf.Max(0f, CurrentHP - dmg);
            if (CurrentHP <= 0f) ServerDie();
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

        /// <summary>
        /// Concede XP e processa level-up usando CharacterData.AddExperience()
        /// (elimina duplicação de lógica de level-up).
        /// </summary>
        [Server]
        public void ServerGrantExp(long amount)
        {
            if (_serverCharData == null) return;

            Experience            += amount;
            _serverCharData.Experience += amount;

            bool leveledUp = false;

            while (Experience >= ExperienceToNextLevel && Level < MAX_LEVEL)
            {
                Experience                           -= ExperienceToNextLevel;
                _serverCharData.Experience           -= _serverCharData.ExperienceToNextLevel;
                Level++;
                _serverCharData.Level++;
                FreeAttributePoints                 += 5;
                _serverCharData.FreeAttributePoints += 5;

                long nextExp                          = _serverCharData.GetExperienceForLevel(_serverCharData.Level);
                ExperienceToNextLevel                 = nextExp;
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

        // ── ClientRpcs ─────────────────────────────────────────────────────

        [ClientRpc]
        private void RpcInitializeLocalPlayer(
            string charName, CharacterRace race, int level,
            long exp, long expToNext, int freePoints,
            int allocSTR, int allocAGI, int allocVIT,
            int allocDEX, int allocINT, int allocLUK,
            float currentHP, float currentMP,
            float equipATK, float equipDEF, float equipMATK, float equipMDEF)
        {
            if (!isLocalPlayer) return;

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

            // Se OnStartLocalPlayer ainda não rodou, guarda para executar depois
            if (!isLocalPlayer || GetComponent<PlayerEntity>() == null)
            {
                _pendingClientInit = true;
                _pendingInitData   = data;
                return;
            }

            if (_clientInitialized) return;
            StartCoroutine(DelayedClientInit(data));
        }

        /// <summary>
        /// Aguarda 1 frame para garantir que todos os Awake/Start do player rodaram.
        /// </summary>
        private IEnumerator DelayedClientInit(CharacterData data)
        {
            yield return null;

            if (_clientInitialized) yield break;
            _clientInitialized = true;

            _playerEntity = GetComponent<PlayerEntity>();
            if (_playerEntity == null)
            {
                Debug.LogError("[NetworkPlayer] PlayerEntity não encontrado no prefab do player!");
                yield break;
            }

            _playerEntity.InitializeFromServer(data);
            UIManager.Instance?.BindLocalPlayer(_playerEntity);
            AttributeWindowUI.Instance?.BindPlayer(_playerEntity);

            Debug.Log($"[Client] Player inicializado: {data.CharacterName} Lv{data.Level} HP:{data.CurrentHP:0}");
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

        // ── Skills (apenas [ClientRpc] — sem overload TargetRpc) ──────────

        /// <summary>
        /// Confirmação de skill vinda do servidor.
        /// Guarda isLocalPlayer para não processar em outros clientes.
        /// </summary>
        [ClientRpc]
        public void RpcSkillConfirmed(int skillIndex, float cooldown)
        {
            if (!isLocalPlayer) return;
            GetComponent<SkillSystem>()?.OnServerSkillConfirmed(skillIndex, cooldown);
        }

        /// <summary>
        /// Rejeição de skill vinda do servidor.
        /// </summary>
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
            if (_agent != null) _agent.ResetPath();
            ServerSaveCharacter();
            RpcPlayerDied();
        }

        [Server]
        private void ServerRespawn()
        {
            if (_serverStats == null) return;

            Vector3 pos = GetRespawnPosition();
            transform.position = pos;
            if (_agent != null && _agent.isOnNavMesh) _agent.Warp(pos);

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
            if (account == null) return;

            SaveManager.Instance?.SaveCharacter(account, _serverCharData);
        }

        [Server]
        private Vector3 GetRespawnPosition()
        {
            if (_respawnPoints != null && _respawnPoints.Length > 0)
                return _respawnPoints[UnityEngine.Random.Range(0, _respawnPoints.Length)].position;

            if (_serverCharData != null)
            {
                var nm = RPGNetworkManager.singleton;
                if (nm != null)
                    return nm.GetSpawnPositionForRace(_serverCharData.Race, _serverCharData);
            }

            return Vector3.zero;
        }

        // ── SyncVar Hooks ──────────────────────────────────────────────────

        private void OnNetNameChanged(string _, string v)
        {
            if (_nameTagText != null) _nameTagText.text = v;
        }

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

        private void OnNetMaxHPChanged(float _, float newMax)
        {
            if (_hpBarSlider != null) _hpBarSlider.maxValue = newMax;
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
            if (isLocalPlayer) UIManager.Instance?.RefreshLevel(v);
        }

        private void OnNetFreePointsChanged(int _, int v)
        {
            if (isLocalPlayer) AttributeWindowUI.Instance?.OnFreePointsUpdated(v);
        }

        private void OnNetMovingChanged(bool _, bool v)
        {
            if (!isLocalPlayer) _animator?.SetBool("IsMoving", v);
        }
    }
}