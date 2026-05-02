using UnityEngine;
using UnityEngine.AI;
using Mirror;
using RPG.Data;
using RPG.UI;
using RPG.Managers;
using RPG.Character;

namespace RPG.Network
{
    /// <summary>
    /// NetworkPlayer v5
    ///
    /// CORREÇÕES:
    ///   - Update() não roda Commands no servidor dedicado (eliminado spam de CmdSetMoving)
    ///   - Linha morta de UpdateAnimations removida do Update()
    ///   - ManagedByNetwork flag para PlayerEntity.Start() não inicializar outros jogadores
    ///   - RequireComponent NetworkIdentity adicionado
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    [RequireComponent(typeof(NetworkIdentity))]
    [RequireComponent(typeof(NetworkTransformReliable))]
    public class NetworkPlayer : NetworkBehaviour, ITargetable
    {
        // ── SyncVars ──────────────────────────────────────────────────────
        [SyncVar(hook = nameof(OnNameChanged))]
        public string CharacterName = "...";

        [SyncVar(hook = nameof(OnRaceChanged))]
        public string Race = "Human";

        [SyncVar]
        public int Level = 1;

        [SyncVar(hook = nameof(OnHPChanged))]
        public float CurrentHP = 100f;

        [SyncVar]
        public float MaxHP = 100f;

        [SyncVar(hook = nameof(OnMovingChanged))]
        public bool IsMoving = false;

        // ── ITargetable ───────────────────────────────────────────────────
        string  ITargetable.DisplayName => CharacterName;
        float   ITargetable.CurrentHP   => CurrentHP;
        float   ITargetable.MaxHP       => MaxHP;
        bool    ITargetable.IsDead      => Dead;
        Vector3 ITargetable.Position    => transform.position;

        public void OnSelected()
        {
            if (selectionIndicator) selectionIndicator.SetActive(true);
        }

        public void OnDeselected()
        {
            if (selectionIndicator) selectionIndicator.SetActive(false);
        }

        // PvP desabilitado — não aplica dano entre jogadores
        public void TakeDamage(float rawAtk, float rawMatk, bool isPhysical)
        {
            Debug.Log("[NetworkPlayer] PvP não implementado.");
        }

        // ── Componentes ───────────────────────────────────────────────────
        private NavMeshAgent _agent;
        private Animator     _animator;

        [Header("Visuals — World Space")]
        [SerializeField] private GameObject            selectionIndicator;
        [SerializeField] private TMPro.TMP_Text        nameTagText;
        [SerializeField] private UnityEngine.UI.Slider hpBarSlider;

        [Header("Spawn Points")]
        [SerializeField] private Transform[] spawnPoints;

        // ── Estado local (só cliente dono) ────────────────────────────────
        private CharacterData _charData;
        private PlayerEntity  _playerEntity;
        private bool          _isDead;
        private float         _moveCheckTimer;

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
            // No servidor dedicado o NavMeshAgent é controlado pelo servidor.
            // Não há input, não há câmera, não há Commands de movimento local.
        }

        public override void OnStartClient()
        {
            if (nameTagText        != null) nameTagText.text = CharacterName;
            if (selectionIndicator != null) selectionIndicator.SetActive(false);
        }

        public override void OnStartLocalPlayer()
        {
            Debug.Log($"[NetworkPlayer] Local player iniciado: {CharacterName}");

            _charData = GameManager.Instance?.SelectedCharacter;
            if (_charData == null)
            {
                Debug.LogError("[NetworkPlayer] SelectedCharacter é null! Verifique o GameManager.");
                return;
            }

            // Marca PlayerEntity para não se auto-inicializar no Start()
            _playerEntity = GetComponent<PlayerEntity>();
            if (_playerEntity != null)
                _playerEntity.ManagedByNetwork = true;

            // Inicializa PlayerEntity com os dados do personagem
            _playerEntity?.Initialize(_charData);

            // Envia dados ao servidor para sincronizar com todos
            CmdSetCharacterInfo(
                _charData.CharacterName,
                _charData.Race.ToString(),
                _charData.Level,
                _charData.CurrentHP > 0 ? _charData.CurrentHP : _charData.GetDerivedStats().MaxHP,
                _charData.GetDerivedStats().MaxHP
            );
        }

        private void Update()
        {
            // CORREÇÃO: servidor dedicado não tem cliente local — nunca processa input aqui
            if (!isLocalPlayer) return;
            if (_isDead)        return;

            // Sincroniza IsMoving com o servidor a cada 0.1s
            _moveCheckTimer += Time.deltaTime;
            if (_moveCheckTimer >= 0.1f)
            {
                _moveCheckTimer = 0f;
                bool moving = _agent != null && _agent.velocity.sqrMagnitude > 0.05f;
                if (moving != IsMoving)
                    CmdSetMoving(moving);
            }
        }

        // ── Commands (Cliente → Servidor) ─────────────────────────────────

        [Command]
        private void CmdSetCharacterInfo(string charName, string race, int level, float hp, float maxHp)
        {
            CharacterName = charName;
            Race          = race;
            Level         = level;
            CurrentHP     = hp;
            MaxHP         = maxHp;
        }

        [Command]
        public void CmdSetMoving(bool moving) => IsMoving = moving;

        [Command]
        public void CmdSyncHP(float hp, float maxHp)
        {
            CurrentHP = hp;
            MaxHP     = maxHp;
        }

        [Command]
        public void CmdSyncLevel(int newLevel)
        {
            Level = newLevel;
        }

        [Command]
        public void CmdRequestRespawn() => ServerRespawn();

        // ── Server Methods ────────────────────────────────────────────────

        /// <summary>
        /// Chamado pelo NetworkMonsterEntity.ServerAttack() diretamente no servidor.
        /// [Server] garante que só executa no servidor.
        /// </summary>
        [Server]
        public void ServerApplyDamage(float dmg)
        {
            if (Dead) return;

            CurrentHP = Mathf.Max(0f, CurrentHP - dmg);
            Debug.Log($"[NetworkPlayer] {CharacterName} tomou {dmg:0} | HP:{CurrentHP:0}/{MaxHP:0}");

            if (CurrentHP <= 0f)
                ServerDie();
        }

        [Server]
        private void ServerDie()
        {
            CurrentHP = 0f;
            if (_agent != null) _agent.ResetPath();

            Debug.Log($"[NetworkPlayer] {CharacterName} morreu no servidor.");
            RpcPlayerDied();
        }

        [Server]
        private void ServerRespawn()
        {
            Vector3 pos = GetSpawnPosition();
            transform.position = pos;
            CurrentHP          = MaxHP * 0.5f;

            Debug.Log($"[NetworkPlayer] {CharacterName} respawnou em {pos}.");
            RpcOnRespawned(pos, CurrentHP, MaxHP);
        }

        [Server]
        private Vector3 GetSpawnPosition()
        {
            if (spawnPoints != null && spawnPoints.Length > 0)
                return spawnPoints[Random.Range(0, spawnPoints.Length)].position;
            return Vector3.zero;
        }

        // ── ClientRpcs (Servidor → Clientes) ─────────────────────────────

        [ClientRpc]
        private void RpcPlayerDied()
        {
            if (!isLocalPlayer) return;

            _isDead = true;

            if (_agent != null)
            {
                _agent.ResetPath();
                _agent.isStopped = true;
            }

            var ctrl = GetComponent<NetworkPlayerController>();
            if (ctrl != null) ctrl.enabled = false;

            _playerEntity?.OnNetworkDeath();

            DeathScreenUI.Show(this);
            Debug.Log("[NetworkPlayer] Morte processada no cliente.");
        }

        [ClientRpc]
        private void RpcOnRespawned(Vector3 position, float hp, float maxHp)
        {
            if (!isLocalPlayer) return;

            _isDead = false;

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
                _playerEntity.Respawn(position);
            }

            DeathScreenUI.Hide();
            Debug.Log("[NetworkPlayer] Respawn concluído no cliente.");
        }

        [ClientRpc]
        public void RpcPlayAnimation(string trigger)
        {
            _animator?.SetTrigger(trigger);
        }

        // ── SyncVar Hooks ─────────────────────────────────────────────────

        private void OnNameChanged(string _, string newName)
        {
            if (nameTagText != null) nameTagText.text = newName;
        }

        private void OnRaceChanged(string _, string newRace) { }

        private void OnHPChanged(float _, float newHP)
        {
            // Atualiza mini barra acima da cabeça (visível para todos)
            if (hpBarSlider != null)
            {
                hpBarSlider.maxValue = MaxHP;
                hpBarSlider.value    = newHP;
                hpBarSlider.gameObject.SetActive(newHP < MaxHP);
            }

            // Propaga HP para PlayerEntity do cliente dono → UIManager atualiza barra
            if (isLocalPlayer && _playerEntity != null && _playerEntity.IsInitialized)
                _playerEntity.ForceSetHP(newHP, MaxHP);
        }

        private void OnMovingChanged(bool _, bool newVal)
        {
            // Atualiza animação de outros jogadores (não o local player)
            if (!isLocalPlayer)
                UpdateAnimations(newVal);
        }

        // ── Animações ─────────────────────────────────────────────────────

        private void UpdateAnimations(bool moving)
        {
            _animator?.SetBool("IsMoving", moving);
        }
    }
}