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
    /// NetworkPlayer v6
    ///
    /// CORREÇÃO PERFORMANCE:
    ///   Adicionado NetworkPlayer.All (HashSet estático) para que
    ///   NetworkMonsterEntity.TryAggro() e ServerDie() não precisem
    ///   chamar FindObjectsOfType a cada tick da IA.
    ///   Registro feito em OnStartServer/OnStopServer.
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    [RequireComponent(typeof(NetworkIdentity))]
    [RequireComponent(typeof(NetworkTransformReliable))]
    public class NetworkPlayer : NetworkBehaviour, ITargetable
    {
        // ── Registro estático — evita FindObjectsOfType nos monstros ─────
        public static readonly HashSet<NetworkPlayer> All = new HashSet<NetworkPlayer>();

        // ── SyncVars ──────────────────────────────────────────────────────
        [SyncVar(hook = nameof(OnNameChanged))]
        public string CharacterName = "...";

        [SyncVar(hook = nameof(OnRaceChanged))]
        public string Race = "Human";

        [SyncVar]
        public int Level = 1;

        [SyncVar(hook = nameof(OnHPChanged))]
        public float CurrentHP = 0f;

        [SyncVar]
        public float MaxHP = 0f;

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

        // PvP desabilitado
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
            // Registra no HashSet para que os monstros possam encontrar sem FindObjectsOfType
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
            Debug.Log($"[NetworkPlayer] Local player iniciado: {CharacterName}");

            _charData = GameManager.Instance?.SelectedCharacter;
            if (_charData == null)
            {
                Debug.LogError("[NetworkPlayer] SelectedCharacter é null!");
                return;
            }

            _playerEntity = GetComponent<PlayerEntity>();
            if (_playerEntity != null)
                _playerEntity.ManagedByNetwork = true;

            _playerEntity?.Initialize(_charData);

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
            if (!isLocalPlayer) return;
            if (_isDead)        return;

            _moveCheckTimer += Time.deltaTime;
            if (_moveCheckTimer >= 0.1f)
            {
                _moveCheckTimer = 0f;
                bool moving = _agent != null && _agent.velocity.sqrMagnitude > 0.05f;
                if (moving != IsMoving) CmdSetMoving(moving);
            }
        }

        // ── Commands ──────────────────────────────────────────────────────

        [Command]
        private void CmdSetCharacterInfo(string charName, string race, int level, float hp, float maxHp)
        {
            CharacterName = charName;
            Race          = race;
            Level         = level;
            CurrentHP     = hp;
            MaxHP         = maxHp;
        }

        [Command] public void CmdSetMoving(bool moving) => IsMoving = moving;
        [Command] public void CmdSyncHP(float hp, float maxHp) { CurrentHP = hp; MaxHP = maxHp; }
        [Command] public void CmdSyncLevel(int newLevel) => Level = newLevel;
        [Command] public void CmdRequestRespawn() => ServerRespawn();

        // ── Server Methods ────────────────────────────────────────────────

        [Server]
        public void ServerApplyDamage(float dmg)
        {
            if (Dead) return;
            CurrentHP = Mathf.Max(0f, CurrentHP - dmg);
            if (CurrentHP <= 0f) ServerDie();
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
            Vector3 pos = GetSpawnPosition();
            transform.position = pos;
            CurrentHP          = MaxHP * 0.5f;
            RpcOnRespawned(pos, CurrentHP, MaxHP);
        }

        [Server]
        private Vector3 GetSpawnPosition()
        {
            if (spawnPoints != null && spawnPoints.Length > 0)
                return spawnPoints[Random.Range(0, spawnPoints.Length)].position;
            return Vector3.zero;
        }

        // ── ClientRpcs ────────────────────────────────────────────────────

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
        private void RpcOnRespawned(Vector3 position, float hp, float maxHp)
        {
            if (!isLocalPlayer) return;
            _isDead = false;
            if (_agent != null) { _agent.isStopped = false; _agent.Warp(position); }
            var ctrl = GetComponent<NetworkPlayerController>();
            if (ctrl != null) ctrl.enabled = true;
            if (_playerEntity != null) { _playerEntity.ForceSetHP(hp, maxHp); _playerEntity.Respawn(position); }
            DeathScreenUI.Hide();
        }

        [ClientRpc]
        public void RpcPlayAnimation(string trigger) => _animator?.SetTrigger(trigger);

        // ── SyncVar Hooks ─────────────────────────────────────────────────

        private void OnNameChanged(string _, string newName)
        {
            if (nameTagText != null) nameTagText.text = newName;
        }

        private void OnRaceChanged(string _, string newRace) { }

		private void OnHPChanged(float _, float newHP)
		{
			if (hpBarSlider != null)
			{
				hpBarSlider.maxValue = MaxHP;
				hpBarSlider.value    = newHP;
				hpBarSlider.gameObject.SetActive(newHP < MaxHP);
			}
		
			if (isLocalPlayer && _playerEntity != null && _playerEntity.IsInitialized)
			{
				if (MaxHP >= _playerEntity.Stats.MaxHP)
					_playerEntity.ForceSetHP(newHP, MaxHP);
			}
		}
	
			private void OnMovingChanged(bool _, bool newVal)
			{
				if (!isLocalPlayer) _animator?.SetBool("IsMoving", newVal);
			}
		}
	}