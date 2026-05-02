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
    /// NetworkPlayer v4 — jogador online completo.
    ///
    /// MUDANÇAS:
    ///   - Implementa ITargetable para outros jogadores poderem ser clicados
    ///   - Removida toda lógica offline
    ///   - Controller de input separado em NetworkPlayerController
    ///   - OnHPChanged propaga para PlayerEntity → UIManager atualiza HUD
    ///   - _isDead bloqueia Update e é resetado no respawn
    ///   - ServerApplyDamage [Server] — chamado pelo mob sem passar por Command
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
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

        public void OnSelected()   { if (selectionIndicator) selectionIndicator.SetActive(true);  }
        public void OnDeselected() { if (selectionIndicator) selectionIndicator.SetActive(false); }

        // PvP desabilitado por enquanto — não aplica dano entre jogadores
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
        [SerializeField] private UnityEngine.UI.Slider hpBarSlider;  // mini barra acima da cabeça

        [Header("Spawn Points")]
        [Tooltip("Arraste os Transforms de spawn aqui. Se vazio, spawna em Vector3.zero.")]
        [SerializeField] private Transform[] spawnPoints;

        // ── Estado local (só cliente dono) ────────────────────────────────
        private CharacterData  _charData;
        private PlayerEntity   _playerEntity;
        private bool           _isDead;
        private float          _moveCheckTimer;

        public bool Dead => CurrentHP <= 0f;

        // ── Unity / Mirror ────────────────────────────────────────────────

        private void Awake()
        {
            _agent        = GetComponent<NavMeshAgent>();
            _animator     = GetComponentInChildren<Animator>();
            _playerEntity = GetComponent<PlayerEntity>();
        }

        public override void OnStartClient()
        {
            if (nameTagText     != null) nameTagText.text = CharacterName;
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

            // Envia dados ao servidor para sincronizar com todos
            CmdSetCharacterInfo(
                _charData.CharacterName,
                _charData.Race.ToString(),
                _charData.Level,
                _charData.CurrentHP,
                _charData.GetDerivedStats().MaxHP
            );

            // Inicializa PlayerEntity com os dados do personagem
            _playerEntity = GetComponent<PlayerEntity>();
            _playerEntity?.Initialize(_charData);

            // O NetworkPlayerController cuida da câmera e do HUD binding
        }

        private void Update()
        {
            if (!isLocalPlayer) return;
            if (_isDead) return;

            // Sincroniza IsMoving com o servidor a cada 0.1s
            _moveCheckTimer += Time.deltaTime;
            if (_moveCheckTimer >= 0.1f)
            {
                _moveCheckTimer = 0f;
                bool moving = _agent.velocity.sqrMagnitude > 0.05f;
                if (moving != IsMoving) CmdSetMoving(moving);
            }

            if (!isLocalPlayer) UpdateAnimations(_agent.velocity.sqrMagnitude > 0.05f);
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
        public void CmdRequestRespawn() => ServerRespawn();

        // ── Server Methods ────────────────────────────────────────────────

        /// <summary>
        /// Chamado pelo NetworkMonsterEntity.ServerAttack() diretamente no servidor.
        /// [Server] garante que só executa no servidor — sem passar por Command.
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

            // Para o agente
            if (_agent != null)
            {
                _agent.ResetPath();
                _agent.isStopped = true;
            }

            // Desativa controller de input
            var ctrl = GetComponent<NetworkPlayerController>();
            if (ctrl != null) ctrl.enabled = false;

            // Propaga HP=0 ao PlayerEntity → UIManager atualiza barra
            _playerEntity?.OnNetworkDeath();

            DeathScreenUI.Show(this);
            Debug.Log("[NetworkPlayer] Morte processada no cliente.");
        }

        [ClientRpc]
        private void RpcOnRespawned(Vector3 position, float hp, float maxHp)
        {
            if (!isLocalPlayer) return;

            _isDead = false;

            // Reativa agente e teleporta
            if (_agent != null)
            {
                _agent.isStopped = false;
                _agent.Warp(position);
            }

            // Reativa controller de input
            var ctrl = GetComponent<NetworkPlayerController>();
            if (ctrl != null) ctrl.enabled = true;

            // Atualiza HP no PlayerEntity → UIManager
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

            // ── CORREÇÃO PRINCIPAL ────────────────────────────────────────
            // Propaga HP para PlayerEntity do cliente DONO.
            // Sem isso, o servidor atualiza CurrentHP via SyncVar mas o HUD
            // do player nunca é atualizado — barra de HP fica estática.
            if (isLocalPlayer && _playerEntity != null && _playerEntity.IsInitialized)
                _playerEntity.ForceSetHP(newHP, MaxHP);
        }

        private void OnMovingChanged(bool _, bool newVal)
        {
            if (!isLocalPlayer) UpdateAnimations(newVal);
        }

        // ── Animações ─────────────────────────────────────────────────────

        private void UpdateAnimations(bool moving)
        {
            _animator?.SetBool("IsMoving", moving);
        }
    }
}