using UnityEngine;
using UnityEngine.SceneManagement;
using Mirror;
using RPG.Data;
using System.Collections.Generic;
using System;

namespace RPG.Network
{
    /// <summary>
    /// ClientAuthHandler v6
    ///
    /// CORREÇÃO v6:
    ///   - Removido campo _handlersRegistered (CS0414: assigned but never used).
    ///     A proteção contra handlers duplicados já é garantida pelo ReplaceHandler
    ///     do Mirror, que substitui silenciosamente se o handler já existir.
    ///     O campo era redundante e gerava warning de compilação.
    ///
    /// CORREÇÃO v5 mantida:
    ///   Usa NetworkClient.ReplaceHandler em OnClientConnected para suportar
    ///   reconexão sem exceção de handler duplicado.
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

        // CORREÇÃO v6: _handlersRegistered removido — era atribuído mas nunca lido (CS0414).
        // ReplaceHandler já garante que não haverá handlers duplicados na reconexão.
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
            // ReplaceHandler substitui se já existir, evitando exceção na reconexão.
            NetworkClient.ReplaceHandler<MsgLoginResponse>          (OnLoginResponse);
            NetworkClient.ReplaceHandler<MsgCreateAccountResponse>  (OnCreateAccountResponse);
            NetworkClient.ReplaceHandler<MsgCharacterListResponse>  (OnCharacterListResponse);
            NetworkClient.ReplaceHandler<MsgCreateCharacterResponse>(OnCreateCharacterResponse);
            NetworkClient.ReplaceHandler<MsgSelectCharacterResponse>(OnSelectCharacterResponse);

            Debug.Log("[ClientAuthHandler] Handlers registrados após conexão.");
        }

        private void OnClientDisconnectedEvent()
        {
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