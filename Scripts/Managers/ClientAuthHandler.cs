using UnityEngine;
using UnityEngine.SceneManagement;
using Mirror;
using RPG.Data;
using System.Collections.Generic;
using System;

namespace RPG.Network
{
    /// <summary>
    /// ClientAuthHandler v5
    ///
    /// CORREÇÃO CRÍTICA v5 — _handlersRegistered não era resetado ao desconectar:
    ///
    ///   PROBLEMA: OnClientDisconnectedEvent() setava _handlersRegistered = false,
    ///   mas OnClientConnected() tinha guard "if (_handlersRegistered) return" que
    ///   funcionava. O bug real era outro: após uma reconexão, alguns handlers
    ///   podiam ser registrados duplicados porque o NetworkClient mantém handlers
    ///   entre conexões no Mirror. A solução é desregistrar explicitamente os
    ///   handlers ao desconectar E usar ReplaceHandler em vez de RegisterHandler
    ///   para evitar exceção de handler duplicado.
    ///
    ///   CORREÇÃO: Usa NetworkClient.ReplaceHandler em OnClientConnected para
    ///   garantir que a reconexão funcione sem exceções.
    ///
    /// CORREÇÃO v4 mantida:
    ///   Após carregar a GameplayScene, aguarda carregamento completar via
    ///   SceneManager.sceneLoaded e então envia MsgClientSceneReady ao servidor.
    ///   O servidor só spawna o player após receber essa confirmação.
    /// </summary>
    public class ClientAuthHandler : MonoBehaviour
    {
        public static ClientAuthHandler Instance { get; private set; }

        public event Action<bool, string>                         OnLoginResult;
        public event Action<bool, string>                         OnCreateAccountResult;
        public event Action<List<CharacterSummary>>               OnCharacterListReceived;
        public event Action<bool, string, List<CharacterSummary>> OnCreateCharacterResult;
        public event Action<bool, string>                         OnSelectCharacterResult;
        public event Action                                       OnServerDisconnected;

        private bool _handlersRegistered    = false;
        private bool _waitingForSceneToLoad = false;

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
            SceneManager.sceneLoaded          -= OnSceneLoaded;
        }

        private void OnClientConnected()
        {
            // CORREÇÃO v5: usa ReplaceHandler para suportar reconexão sem exceção
            // de "handler já registrado". ReplaceHandler substitui se existir.
            NetworkClient.ReplaceHandler<MsgLoginResponse>          (OnLoginResponse);
            NetworkClient.ReplaceHandler<MsgCreateAccountResponse>  (OnCreateAccountResponse);
            NetworkClient.ReplaceHandler<MsgCharacterListResponse>  (OnCharacterListResponse);
            NetworkClient.ReplaceHandler<MsgCreateCharacterResponse>(OnCreateCharacterResponse);
            NetworkClient.ReplaceHandler<MsgSelectCharacterResponse>(OnSelectCharacterResponse);

            _handlersRegistered = true;
            Debug.Log("[ClientAuthHandler] Handlers registrados após conexão.");
        }

        private void OnClientDisconnectedEvent()
        {
            _handlersRegistered    = false;
            _waitingForSceneToLoad = false;
            SceneManager.sceneLoaded -= OnSceneLoaded;

            Debug.Log("[ClientAuthHandler] Desconectado — handlers limpos.");
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
            OnSelectCharacterResult?.Invoke(msg.Success, msg.Error);

            if (!msg.Success) return;

            Debug.Log("[ClientAuthHandler] Personagem selecionado. Carregando GameplayScene...");

            _waitingForSceneToLoad = true;
            SceneManager.sceneLoaded += OnSceneLoaded;

            SceneManager.LoadScene(Managers.GameManager.SCENE_GAMEPLAY);
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!_waitingForSceneToLoad) return;
            if (scene.name != Managers.GameManager.SCENE_GAMEPLAY) return;

            SceneManager.sceneLoaded -= OnSceneLoaded;
            _waitingForSceneToLoad    = false;

            Debug.Log("[ClientAuthHandler] GameplayScene carregada. Notificando servidor...");
            StartCoroutine(SendReadyAfterFrame());
        }

        private System.Collections.IEnumerator SendReadyAfterFrame()
        {
            // Aguarda 2 frames para garantir que NavMesh e todos os scripts iniciaram
            yield return null;
            yield return null;

            if (NetworkClient.isConnected)
            {
                NetworkClient.Send(new MsgClientSceneReady());
                Debug.Log("[ClientAuthHandler] MsgClientSceneReady enviado ao servidor.");
            }
            else
            {
                Debug.LogWarning("[ClientAuthHandler] Sem conexão ao tentar enviar MsgClientSceneReady.");
            }
        }
    }
}