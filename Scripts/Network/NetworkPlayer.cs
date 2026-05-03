using UnityEngine;
using UnityEngine.AI;
using Mirror;
using RPG.Data;
using RPG.UI;
using RPG.Managers;
using RPG.Character;
using System.Collections.Generic;

namespace RPG.Network
{
    /// <summary>
    /// NetworkPlayer v7 — SERVIDOR É AUTORIDADE
    ///
    /// CORREÇÕES DESTA VERSÃO:
    ///
    ///   1. HP/MP têm UMA fonte de verdade: SyncVars neste componente.
    ///      PlayerEntity.CurrentHP/CurrentMP são alimentados APENAS pelos
    ///      hooks OnHPChanged/OnMPChanged. Não existe mais ForceSetHP() sendo
    ///      chamado de múltiplos lugares.
    ///
    ///   2. Dano aplicado via ServerApplyDamage() — só o servidor altera HP.
    ///      CmdTakeDamage() foi removido; monstros chamam ServerApplyDamage()
    ///      diretamente (ambos rodam no servidor).
    ///
    ///   3. XP e Level Up movidos para cá (ServerGrantExp / ServerLevelUp).
    ///      RpcGrantExp no NetworkMonsterEntity foi substituído por chamada
    ///      direta a NetworkPlayer.ServerGrantExp(amount).
    ///
    ///   4. Atributos livres alocados via CmdAllocateAttribute() —
    ///      servidor valida, aplica e sincroniza; cliente só pede.
    ///
    ///   5. HashSet All ainda presente para lookups rápidos da IA.
    ///
    ///   6. SaveCharacter() chamado SOMENTE no servidor.
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    [RequireComponent(typeof(NetworkIdentity))]
    [RequireComponent(typeof(NetworkTransformReliable))]
    public class NetworkPlayer : NetworkBehaviour, ITargetable
    {
        // ── Registro estático ─────────────────────────────────────────────
        public static readonly HashSet<NetworkPlayer> All = new HashSet<NetworkPlayer>();

        // ── SyncVars — servidor escreve, clientes lêem ────────────────────
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

        // Atributos alocados — sincronizados para que outros possam ver as stats
        [SyncVar] public int AllocatedSTR = 0;
        [SyncVar] public int AllocatedAGI = 0;
        [SyncVar] public int AllocatedVIT = 0;
        [SyncVar] public int AllocatedDEX = 0;
        [SyncVar] public int AllocatedINT = 0;
        [SyncVar] public int AllocatedLUK = 0;

        // ── ITargetable ───────────────────────────────────────────────────
        string  ITargetable.DisplayName => CharacterName;
        float   ITargetable.CurrentHP   => CurrentHP;
        float   ITargetable.MaxHP       => MaxHP;
        bool    ITargetable.IsDead      => Dead;
        Vector3 ITargetable.Position    => transform.position;

        public void OnSelected()   { if (selectionIndicator) selectionIndicator.SetActive(true);  }
        public void OnDeselected() { if (selectionIndicator) selectionIndicator.SetActive(false); }

        // PvP desabilitado por ora
        public void TakeDamage(float rawAtk, float rawMatk, bool isPhysical)
            => Debug.Log("[NetworkPlayer] PvP não implementado.");

        // ── Componentes ───────────────────────────────────────────────────
        private NavMeshAgent _agent;
        private Animator     _animator;

        [Header("Visuals — World Space")]
        [SerializeField] private GameObject            selectionIndicator;
        [SerializeField] private TMPro.TMP_Text        nameTagText;
        [SerializeField] private UnityEngine.UI.Slider hpBarSlider;

        [Header("Spawn Points")]
        [SerializeField] private Transform[] spawnPoints;

        // ── Estado local ──────────────────────────────────────────────────
        private CharacterData _charData;     // referência local (cliente local)
        private PlayerEntity  _playerEntity; // componente local de gameplay
        private bool          _isDead;

        public bool Dead => CurrentHP <= 0f;

        // ── Unity / Mirror ────────────────────────────────────────────────

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

        /// <summary>
        /// Chamado apenas no cliente que é o dono (local player).
        /// Aqui iniciamos o PlayerEntity com os dados do personagem selecionado
        /// e enviamos as informações iniciais ao servidor.
        /// </summary>
        public override void OnStartLocalPlayer()
        {
            Debug.Log($"[NetworkPlayer] Local player iniciado.");

            _charData = GameManager.Instance?.SelectedCharacter;
            if (_charData == null)
            {
                Debug.LogError("[NetworkPlayer] SelectedCharacter é null!");
                return;
            }

            // Marca PlayerEntity para não inicializar sozinho
            _playerEntity = GetComponent<PlayerEntity>();
            if (_playerEntity != null)
                _playerEntity.ManagedByNetwork = true;

            // Envia dados do personagem ao servidor para validação e sincronização
            CmdRegisterCharacter(
                _charData.CharacterName,
                _charData.Race.ToString(),
                (int)_charData.Race,
                _charData.Level,
                _charData.Experience,
                _charData.ExperienceToNextLevel,
                _charData.FreeAttributePoints,
                _charData.AllocatedSTR,
                _charData.AllocatedAGI,
                _charData.AllocatedVIT,
                _charData.AllocatedDEX,
                _charData.AllocatedINT,
                _charData.AllocatedLUK,
                _charData.CurrentHP,
                _charData.CurrentMP,
                _charData.EquipmentBonuses.ATK,
                _charData.EquipmentBonuses.DEF,
                _charData.EquipmentBonuses.MATK,
                _charData.EquipmentBonuses.MDEF
            );
        }

        private void Update()
        {
            if (!isLocalPlayer || _isDead) return;

            // Atualiza flag de movimento
            bool moving = _agent != null && _agent.velocity.sqrMagnitude > 0.05f;
            if (moving != IsMoving) CmdSetMoving(moving);
        }

        // ── Commands — cliente → servidor ─────────────────────────────────

        /// <summary>
        /// Servidor recebe os dados do personagem, calcula stats derivados
        /// e seta todas as SyncVars. É o único ponto onde HP/MP/Stats são
        /// inicializados canonicamente.
        /// </summary>
        [Command]
        private void CmdRegisterCharacter(
            string charName, string raceStr, int raceInt,
            int level, long exp, long expToNext, int freePoints,
            int allocSTR, int allocAGI, int allocVIT,
            int allocDEX, int allocINT, int allocLUK,
            float savedHP, float savedMP,
            float equipATK, float equipDEF, float equipMATK, float equipMDEF)
        {
            // Reconstrói CharacterData no servidor para calcular stats de forma autoritativa
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

            // Calcula stats no servidor — fonte de verdade
            var stats = data.GetDerivedStats();

            // Seta SyncVars (servidor → todos os clientes)
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

            MaxHP     = stats.MaxHP;
            MaxMP     = stats.MaxMP;
            CurrentHP = (savedHP > 0f && savedHP <= stats.MaxHP) ? savedHP : stats.MaxHP;
            CurrentMP = (savedMP > 0f && savedMP <= stats.MaxMP) ? savedMP : stats.MaxMP;

            // Armazena CharacterData no servidor para uso interno (XP, save)
            _serverCharData = data;
            _serverStats    = stats;

            Debug.Log($"[Server] {charName} registrado | HP:{CurrentHP:0}/{MaxHP:0} | Lv:{level}");

            // Notifica o cliente local para inicializar o PlayerEntity
RpcInitializeLocalPlayer(connectionToClient,
                         charName, (CharacterRace)raceInt, level,
                         exp, expToNext, freePoints,
                         allocSTR, allocAGI, allocVIT, allocDEX, allocINT, allocLUK,
                         CurrentHP, CurrentMP,
                         equipATK, equipDEF, equipMATK, equipMDEF);
        }

        [Command] public void CmdSetMoving(bool moving) => IsMoving = moving;

        /// <summary>
        /// Solicita alocação de 1 ponto em um atributo.
        /// Servidor valida se há pontos disponíveis antes de aplicar.
        /// </summary>
        [Command]
        public void CmdAllocateAttribute(int attributeIndex)
        {
            if (FreeAttributePoints <= 0)
            {
                Debug.LogWarning($"[Server] {CharacterName} tentou alocar atributo sem pontos livres!");
                return;
            }
            if (_serverCharData == null) return;

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
                default:
                    Debug.LogWarning($"[Server] Índice de atributo inválido: {attributeIndex}");
                    FreeAttributePoints++; // devolve o ponto
                    _serverCharData.FreeAttributePoints++;
                    return;
            }

            // Recalcula stats no servidor
            _serverStats = _serverCharData.GetDerivedStats();

            // Atualiza HP/MP máximos (nunca reduz o atual abaixo do novo max)
            float oldMaxHP = MaxHP;
            float oldMaxMP = MaxMP;
            MaxHP = _serverStats.MaxHP;
            MaxMP = _serverStats.MaxMP;
            // Se o max subiu, o atual pode ficar no mesmo valor (não cura automaticamente)
            if (CurrentHP > MaxHP) CurrentHP = MaxHP;
            if (CurrentMP > MaxMP) CurrentMP = MaxMP;

            // Salva no servidor
            ServerSaveCharacter();

            Debug.Log($"[Server] {CharacterName} alocou atributo {attributeIndex} | " +
                      $"Pontos restantes: {FreeAttributePoints}");
        }

        [Command]
        public void CmdRequestRespawn() => ServerRespawn();

        // ── Métodos de servidor — chamados por outros componentes server-side ──

        /// <summary>
        /// Aplica dano ao jogador. Chamado diretamente pelo NetworkMonsterEntity
        /// (que também roda no servidor). Nunca chamado pelo cliente.
        /// </summary>
        [Server]
        public void ServerApplyDamage(float dmg)
        {
            if (Dead) return;
            CurrentHP = Mathf.Max(0f, CurrentHP - dmg);
            if (CurrentHP <= 0f) ServerDie();
        }

        /// <summary>
        /// Concede XP ao jogador após derrotar um monstro.
        /// Toda a lógica de level up acontece aqui, no servidor.
        /// </summary>
        [Server]
        public void ServerGrantExp(long amount)
        {
            if (_serverCharData == null) return;

            Experience += amount;
            _serverCharData.Experience += amount;

            bool leveledUp = false;

            while (Experience >= ExperienceToNextLevel)
            {
                Experience            -= ExperienceToNextLevel;
                _serverCharData.Experience -= _serverCharData.ExperienceToNextLevel;

                Level++;
                _serverCharData.Level++;

                FreeAttributePoints   += 5;
                _serverCharData.FreeAttributePoints += 5;

                long nextExp = _serverCharData.GetExperienceForLevel(_serverCharData.Level);
                ExperienceToNextLevel = nextExp;
                _serverCharData.ExperienceToNextLevel = nextExp;

                // Recalcula stats com o novo nível
                _serverStats = _serverCharData.GetDerivedStats();
                MaxHP        = _serverStats.MaxHP;
                MaxMP        = _serverStats.MaxMP;

                // Level up: cura completo
                CurrentHP = MaxHP;
                CurrentMP = MaxMP;
                _serverCharData.CurrentHP = MaxHP;
                _serverCharData.CurrentMP = MaxMP;

                leveledUp = true;
                Debug.Log($"[Server] {CharacterName} subiu para Lv {Level}!");
            }

            // Salva após ganhar XP
            ServerSaveCharacter();

            // Notifica o cliente local com feedback visual
            RpcOnExpGained(connectionToClient, amount, leveledUp);
        }

        [Server]
        private void ServerDie()
        {
            CurrentHP = 0f;
            if (_agent != null) _agent.ResetPath();
            RpcPlayerDied();
        }

        [Server]
        private void ServerRespawn()
        {
            if (_serverStats == null) return;

            Vector3 pos = GetSpawnPosition();
            transform.position = pos;

            // Respawn com 50% HP/MP
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
            _serverCharData.PosX = transform.position.x;
            _serverCharData.PosY = transform.position.y;
            _serverCharData.PosZ = transform.position.z;

            // Em produção: substituir por chamada HTTP ao banco de dados.
            // Por hora, salva no arquivo local do servidor.
            // ATENÇÃO: em servidor dedicado headless, Application.persistentDataPath
            // aponta para o diretório de dados do processo servidor — OK para dev.
            var accountData = new Data.AccountData { Username = _serverCharData.CharacterName };
            SaveManager.Instance?.SaveCharacter(accountData, _serverCharData);
        }

        [Server]
        private Vector3 GetSpawnPosition()
        {
            if (spawnPoints != null && spawnPoints.Length > 0)
                return spawnPoints[Random.Range(0, spawnPoints.Length)].position;
            return Vector3.zero;
        }

        // Dados do personagem mantidos no servidor para cálculos autoritativos
        private CharacterData _serverCharData;
        private DerivedStats  _serverStats;

        // Acesso público somente-leitura para outros componentes server-side (ex: monstros)
        public DerivedStats ServerStats => _serverStats;

        // ── ClientRpcs ────────────────────────────────────────────────────

        /// <summary>
        /// Enviado apenas ao cliente local após CmdRegisterCharacter ser processado.
        /// Inicializa o PlayerEntity com dados validados pelo servidor.
        /// </summary>
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
            // Reconstrói CharacterData local com os dados validados pelo servidor
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

            // Atualiza referência local do GameManager
            GameManager.Instance?.SetSelectedCharacter(data);

            // Inicializa PlayerEntity local
            _playerEntity = GetComponent<PlayerEntity>();
            if (_playerEntity != null)
                _playerEntity.Initialize(data);

            Debug.Log($"[Client] PlayerEntity inicializado via servidor: {charName} Lv{level}");
        }

        [ClientRpc]
        private void RpcPlayerDied()
        {
            if (!isLocalPlayer) return;
            _isDead = true;
            if (_agent != null) { _agent.ResetPath(); _agent.isStopped = true; }
            var ctrl = GetComponent<NetworkPlayerController>();
            if (ctrl != null) ctrl.enabled = false;
            _playerEntity?.OnNetworkDeath();
            DeathScreenUI.Show(this);
        }

        [ClientRpc]
        private void RpcOnRespawned(Vector3 position, float hp, float maxHp, float mp, float maxMp)
        {
            if (!isLocalPlayer) return;
            _isDead = false;
            if (_agent != null) { _agent.isStopped = false; _agent.Warp(position); }
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

        /// <summary>
        /// Feedback visual de XP/level up no cliente local.
        /// Não modifica nenhum dado — apenas UI.
        /// </summary>
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

                // Cura completo: PlayerEntity será atualizado via hook OnHPChanged/OnMPChanged
                UIManager.Instance?.ShowMessage("Level up! Você evoluiu!");
            }

            // Atualiza CharacterData local para refletir XP/level corretos da UI
            if (_charData != null)
            {
                _charData.Experience            = Experience;
                _charData.ExperienceToNextLevel = ExperienceToNextLevel;
                _charData.Level                 = Level;
                _charData.FreeAttributePoints   = FreeAttributePoints;
            }
        }

        // ── SyncVar Hooks ─────────────────────────────────────────────────

        private void OnNameChanged(string _, string newName)
        {
            if (nameTagText != null) nameTagText.text = newName;
        }

        /// <summary>
        /// HP alterado no servidor → atualiza PlayerEntity local via hook.
        /// Esta é a ÚNICA forma de o HP do PlayerEntity mudar em modo multiplayer.
        /// </summary>
        private void OnHPChanged(float _, float newHP)
        {
            // Atualiza barra de HP world-space (visível para todos os jogadores)
            if (hpBarSlider != null)
            {
                hpBarSlider.maxValue = MaxHP;
                hpBarSlider.value    = newHP;
                hpBarSlider.gameObject.SetActive(newHP < MaxHP);
            }

            // Propaga para PlayerEntity local (só no cliente dono)
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
            // Notifica a janela de atributos para atualizar botões de +
            AttributeWindowUI.Instance?.OnFreePointsUpdated(newPoints);
        }

        private void OnMovingChanged(bool _, bool newVal)
        {
            if (!isLocalPlayer) _animator?.SetBool("IsMoving", newVal);
        }
    }
}