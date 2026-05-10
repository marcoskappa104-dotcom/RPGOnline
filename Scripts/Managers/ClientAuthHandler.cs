using UnityEngine;
using UnityEngine.SceneManagement;
using Mirror;
using RPG.Data;
using System.Collections.Generic;
using System;

namespace RPG.Network
{
    /// <summary>
    /// ClientAuthHandler v7
    ///
    /// CORREÇÕES v7:
    ///   - Suporte ao sistema de nonce challenge-response (MsgAuthChallenge).
    ///     Ao conectar, o servidor envia um nonce. O cliente armazena o nonce
    ///     e usa GameManager.HashPasswordWithNonce() ao fazer login.
    ///     Isso elimina replay attacks básicos de credenciais capturadas.
    ///
    ///   - SendLogin agora exige que o nonce tenha sido recebido antes de enviar.
    ///     Se o nonce não chegou, aguarda até 5s antes de falhar.
    ///
    ///   - Todas as correções v6 mantidas (ReplaceHandler, reconexão, etc).
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

        private bool   _waitingForSceneToLoad = false;
        private string _sessionNonce          = "";
        private bool   _nonceReceived         = false;

        // Fila de ações aguardando nonce
        private Action _pendingLoginAction = null;

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
            _nonceReceived = false;
            _sessionNonce  = "";

            // ReplaceHandler substitui se já existir, evitando exceção na reconexão.
            NetworkClient.ReplaceHandler<MsgAuthChallenge>          (OnAuthChallenge);
            NetworkClient.ReplaceHandler<MsgLoginResponse>          (OnLoginResponse);
            NetworkClient.ReplaceHandler<MsgCreateAccountResponse>  (OnCreateAccountResponse);
            NetworkClient.ReplaceHandler<MsgCharacterListResponse>  (OnCharacterListResponse);
            NetworkClient.ReplaceHandler<MsgCreateCharacterResponse>(OnCreateCharacterResponse);
            NetworkClient.ReplaceHandler<MsgSelectCharacterResponse>(OnSelectCharacterResponse);

            Debug.Log("[ClientAuthHandler] Handlers registrados — aguardando challenge do servidor.");
        }

        private void OnClientDisconnectedEvent()
        {
            _waitingForSceneToLoad = false;
            _nonceReceived         = false;
            _sessionNonce          = "";
            _pendingLoginAction    = null;
            SceneManager.sceneLoaded -= OnSceneLoaded;

            Debug.Log("[ClientAuthHandler] Desconectado — estado limpo.");
        }

        public void OnDisconnectedFromServer()
        {
            Debug.Log("[ClientAuthHandler] Desconectado do servidor.");
            OnServerDisconnected?.Invoke();
        }

        // ── Challenge / Nonce ──────────────────────────────────────────────

        private void OnAuthChallenge(MsgAuthChallenge msg)
        {
            _sessionNonce  = msg.Nonce;
            _nonceReceived = true;
            Debug.Log("[ClientAuthHandler] Nonce recebido do servidor.");

            // Se havia login pendente esperando o nonce, executa agora
            if (_pendingLoginAction != null)
            {
                var action = _pendingLoginAction;
                _pendingLoginAction = null;
                action();
            }
        }

        // ── Envio ──────────────────────────────────────────────────────────

        /// <summary>
        /// Envia requisição de login. Aguarda o nonce se ainda não chegou.
        /// </summary>
        public void SendLogin(string username, string password)
        {
            if (!NetworkClient.isConnected)
            {
                OnLoginResult?.Invoke(false, "Sem conexão com o servidor.");
                return;
            }

            string baseHash = Managers.GameManager.HashPassword(password);

            void DoSend()
            {
                string signedHash = Managers.GameManager.HashPasswordWithNonce(baseHash, _sessionNonce);
                NetworkClient.Send(new MsgLoginRequest
                {
                    Username   = username.Trim(),
                    SignedHash = signedHash
                });
            }

            if (_nonceReceived)
            {
                DoSend();
            }
            else
            {
                // Aguarda nonce com timeout via coroutine
                _pendingLoginAction = DoSend;
                StartCoroutine(WaitForNonceThenLogin(username));
            }
        }

        private System.Collections.IEnumerator WaitForNonceThenLogin(string username)
        {
            float elapsed = 0f;
            while (!_nonceReceived && elapsed < 5f)
            {
                elapsed += Time.deltaTime;
                yield return null;
            }

            if (!_nonceReceived)
            {
                _pendingLoginAction = null;
                OnLoginResult?.Invoke(false, "Timeout aguardando o servidor. Tente novamente.");
                Debug.LogWarning("[ClientAuthHandler] Timeout aguardando nonce do servidor.");
            }
            // Se nonce chegou, _pendingLoginAction já foi executado em OnAuthChallenge
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