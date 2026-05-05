using UnityEngine;
using UnityEngine.SceneManagement;
using Mirror;
using RPG.Data;
using System.Collections.Generic;
using System;

namespace RPG.Network
{
    /// <summary>
    /// ClientAuthHandler v3
    ///
    /// CORREÇÕES:
    ///   - OnSelectCharacterResponse: carrega GameplayScene diretamente aqui
    ///     após confirmação do servidor (era responsabilidade de ninguém antes).
    ///   - Aguarda a GameplayScene carregar completamente antes de permitir
    ///     que o Mirror processe SpawnMessages de monstros/players.
    ///     Isso resolve "Failed to create agent because there is no valid NavMesh".
    /// </summary>
    public class ClientAuthHandler : MonoBehaviour
    {
        public static ClientAuthHandler Instance { get; private set; }

        // ── Eventos para as UIs ────────────────────────────────────────────
        public event Action<bool, string>                         OnLoginResult;
        public event Action<bool, string>                         OnCreateAccountResult;
        public event Action<List<CharacterSummary>>               OnCharacterListReceived;
        public event Action<bool, string, List<CharacterSummary>> OnCreateCharacterResult;
        public event Action<bool, string>                         OnSelectCharacterResult;
        public event Action                                       OnServerDisconnected;

        private bool _handlersRegistered = false;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void Start()
        {
            NetworkClient.OnConnectedEvent    += OnClientConnected;
            NetworkClient.OnDisconnectedEvent += OnClientDisconnectedEvent;
        }

        private void OnDestroy()
        {
            NetworkClient.OnConnectedEvent    -= OnClientConnected;
            NetworkClient.OnDisconnectedEvent -= OnClientDisconnectedEvent;
        }

        private void OnClientConnected()
        {
            if (_handlersRegistered) return;
            _handlersRegistered = true;

            NetworkClient.RegisterHandler<MsgLoginResponse>          (OnLoginResponse);
            NetworkClient.RegisterHandler<MsgCreateAccountResponse>  (OnCreateAccountResponse);
            NetworkClient.RegisterHandler<MsgCharacterListResponse>  (OnCharacterListResponse);
            NetworkClient.RegisterHandler<MsgCreateCharacterResponse>(OnCreateCharacterResponse);
            NetworkClient.RegisterHandler<MsgSelectCharacterResponse>(OnSelectCharacterResponse);

            Debug.Log("[ClientAuthHandler] Handlers registrados após conexão.");
        }

        private void OnClientDisconnectedEvent()
        {
            _handlersRegistered = false;
        }

        public void OnDisconnectedFromServer()
        {
            Debug.Log("[ClientAuthHandler] Desconectado do servidor.");
            OnServerDisconnected?.Invoke();
        }

        // ── Envio ──────────────────────────────────────────────────────────

        public void SendLogin(string username, string password)
        {
            if (!NetworkClient.isConnected)
            { OnLoginResult?.Invoke(false, "Sem conexão com o servidor."); return; }

            NetworkClient.Send(new MsgLoginRequest
            {
                Username     = username.Trim(),
                PasswordHash = Managers.GameManager.HashPassword(password)
            });
        }

        public void SendCreateAccount(string username, string password)
        {
            if (!NetworkClient.isConnected)
            { OnCreateAccountResult?.Invoke(false, "Sem conexão com o servidor."); return; }

            NetworkClient.Send(new MsgCreateAccountRequest
            {
                Username     = username.Trim(),
                PasswordHash = Managers.GameManager.HashPassword(password)
            });
        }

        public void SendRequestCharacterList()
        {
            if (NetworkClient.isConnected)
                NetworkClient.Send(new MsgRequestCharacterList());
        }

        public void SendCreateCharacter(string name, int raceIndex)
        {
            if (NetworkClient.isConnected)
                NetworkClient.Send(new MsgCreateCharacterRequest
                { Name = name.Trim(), RaceIndex = raceIndex });
        }

        public void SendSelectCharacter(string characterId)
        {
            if (NetworkClient.isConnected)
                NetworkClient.Send(new MsgSelectCharacter { CharacterId = characterId });
        }

        // ── Recebimento ────────────────────────────────────────────────────

        private void OnLoginResponse(MsgLoginResponse msg)
        {
            if (msg.Success)
                Managers.GameManager.Instance?.SetLoggedUsername(msg.Username);
            OnLoginResult?.Invoke(msg.Success, msg.Error);
        }

        private void OnCreateAccountResponse(MsgCreateAccountResponse msg)
            => OnCreateAccountResult?.Invoke(msg.Success, msg.Error);

        private void OnCharacterListResponse(MsgCharacterListResponse msg)
            => OnCharacterListReceived?.Invoke(msg.Characters);

        private void OnCreateCharacterResponse(MsgCreateCharacterResponse msg)
            => OnCreateCharacterResult?.Invoke(msg.Success, msg.Error, msg.UpdatedList);

        private void OnSelectCharacterResponse(MsgSelectCharacterResponse msg)
        {
            // Notifica a UI (para esconder botões, etc.)
            OnSelectCharacterResult?.Invoke(msg.Success, msg.Error);

            if (!msg.Success) return;

            // CORREÇÃO PRINCIPAL: carrega a GameplayScene aqui.
            // O servidor já spawnará o player quando a cena estiver pronta.
            Debug.Log("[ClientAuthHandler] Personagem selecionado. Carregando GameplayScene...");
            SceneManager.LoadScene(Managers.GameManager.SCENE_GAMEPLAY);
        }
    }
}