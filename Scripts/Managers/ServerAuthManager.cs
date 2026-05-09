using UnityEngine;
using Mirror;
using RPG.Data;
using RPG.Managers;
using System.Collections.Generic;
using System.Collections;

namespace RPG.Network
{
    /// <summary>
    /// ServerAuthManager v7
    ///
    /// CORREÇÕES v7:
    ///
    ///   ALINHAMENTO COM PIPELINE v9:
    ///     TryLoginWithSignedHash agora funciona porque o banco armazena SHA256(senha)
    ///     e ValidateLoginWithNonce calcula SHA256(SHA256(senha)+nonce) corretamente.
    ///     Nenhuma mudança de lógica necessária aqui — o bug estava em GameManager/DatabaseManager.
    ///
    ///   MELHORIA — logs de debug detalhados no fluxo de login:
    ///     Agora é possível rastrear exatamente onde o login falha sem modificar o código.
    ///     Ative DEBUG_AUTH no Inspector ou via define para ver os detalhes.
    ///
    ///   MELHORIA — OnCreateAccountRequest agora retorna erro apropriado quando
    ///     username ou hash são vazios, em vez de passar null para TryCreateAccount.
    ///
    ///   Todas as correções v6 mantidas (rate limiting, nonce, session TTL, cleanup).
    /// </summary>
    public class ServerAuthManager : MonoBehaviour
    {
        public static ServerAuthManager Instance { get; private set; }

        // ── Configuração de segurança ──────────────────────────────────────
        private const int   LOGIN_MAX_ATTEMPTS  = 5;
        private const float SESSION_TTL_SECONDS = 300f; // 5 minutos sem atividade

        [Header("Debug")]
        [Tooltip("Ativa logs detalhados do fluxo de autenticação. DESATIVE em produção.")]
        [SerializeField] private bool debugAuth = false;

        private enum ConnState { Unauthenticated, Authenticated, InGame }

        private class ConnData
        {
            public ConnState   State           = ConnState.Unauthenticated;
            public string      Username        = "";
            public string      CharacterId     = "";
            public AccountData CachedAccount   = null;
            public int         LoginAttempts   = 0;
            public string      SessionNonce    = "";
            public float       LastActivityTime;

            public ConnData() => LastActivityTime = UnityEngine.Time.time;
        }

        private readonly Dictionary<int, ConnData> _sessions = new();
        private Coroutine _cleanupCoroutine;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this); return; }
            Instance = this;
        }

        private void OnDestroy()
        {
            if (_cleanupCoroutine != null) StopCoroutine(_cleanupCoroutine);
        }

        public void RegisterHandlers()
        {
            NetworkServer.RegisterHandler<MsgLoginRequest>          (OnLoginRequest,          false);
            NetworkServer.RegisterHandler<MsgCreateAccountRequest>  (OnCreateAccountRequest,  false);
            NetworkServer.RegisterHandler<MsgRequestCharacterList>  (OnRequestCharacterList,  false);
            NetworkServer.RegisterHandler<MsgCreateCharacterRequest>(OnCreateCharacterRequest, false);
            NetworkServer.RegisterHandler<MsgSelectCharacter>       (OnSelectCharacter,        false);

            _cleanupCoroutine = StartCoroutine(CleanupExpiredSessions());
            Debug.Log("[ServerAuthManager] Handlers registrados.");
        }

        public void OnServerConnect(NetworkConnectionToClient conn)
        {
            var session = new ConnData();
            session.SessionNonce = GameManager.GenerateNonce();
            _sessions[conn.connectionId] = session;

            conn.Send(new MsgAuthChallenge { Nonce = session.SessionNonce });
            LogAuth($"Nova conexão: {conn.connectionId} | Nonce: {session.SessionNonce}");
            Debug.Log($"[ServerAuth] Nova conexão: {conn.connectionId} | Nonce enviado.");
        }

        public void OnServerDisconnect(NetworkConnectionToClient conn)
        {
            _sessions.Remove(conn.connectionId);
        }

        // ── Login ──────────────────────────────────────────────────────────

        private void OnLoginRequest(NetworkConnectionToClient conn, MsgLoginRequest msg)
        {
            Debug.Log($"[ServerAuth] Login: '{msg.Username}' conn:{conn.connectionId}");

            if (!_sessions.TryGetValue(conn.connectionId, out var session))
            {
                conn.Send(new MsgLoginResponse { Success = false, Error = "Sessão inválida." });
                return;
            }

            if (session.State != ConnState.Unauthenticated)
            {
                conn.Send(new MsgLoginResponse { Success = false, Error = "Já autenticado." });
                return;
            }

            // Rate limiting por conexão
            session.LoginAttempts++;
            if (session.LoginAttempts > LOGIN_MAX_ATTEMPTS)
            {
                Debug.LogWarning($"[ServerAuth] SECURITY: conn:{conn.connectionId} excedeu tentativas ({LOGIN_MAX_ATTEMPTS}). Desconectando.");
                conn.Send(new MsgLoginResponse { Success = false, Error = "Muitas tentativas. Tente mais tarde." });
                conn.Disconnect();
                return;
            }

            // Validação de entrada
            if (string.IsNullOrWhiteSpace(msg.Username) || string.IsNullOrWhiteSpace(msg.SignedHash))
            {
                conn.Send(new MsgLoginResponse { Success = false, Error = "Dados de login inválidos." });
                return;
            }

            if (string.IsNullOrWhiteSpace(session.SessionNonce))
            {
                Debug.LogError($"[ServerAuth] SessionNonce vazio para conn:{conn.connectionId}! Isso não deveria acontecer.");
                conn.Send(new MsgLoginResponse { Success = false, Error = "Erro de sessão. Reconecte." });
                return;
            }

            LogAuth($"Login attempt: user='{msg.Username}' nonce='{session.SessionNonce}' signedHash='{msg.SignedHash}'");

            var account = DatabaseManager.Instance?.TryLoginWithSignedHash(
                msg.Username, msg.SignedHash, session.SessionNonce);

            if (account == null)
            {
                string attempts = $"({session.LoginAttempts}/{LOGIN_MAX_ATTEMPTS})";
                LogAuth($"Login falhou para '{msg.Username}' {attempts}");
                conn.Send(new MsgLoginResponse { Success = false, Error = $"Usuário ou senha incorretos. {attempts}" });
                return;
            }

            session.State            = ConnState.Authenticated;
            session.Username         = account.Username;
            session.CachedAccount    = account;
            session.LoginAttempts    = 0;
            session.LastActivityTime = Time.time;

            Debug.Log($"[ServerAuth] Login OK: {account.Username}");
            conn.Send(new MsgLoginResponse { Success = true, Username = account.Username });
            SendCharacterList(conn, account);
        }

        // ── Criar conta ────────────────────────────────────────────────────

        private void OnCreateAccountRequest(NetworkConnectionToClient conn, MsgCreateAccountRequest msg)
        {
            // Validação de entrada antes de passar para o banco
            if (string.IsNullOrWhiteSpace(msg.Username))
            {
                conn.Send(new MsgCreateAccountResponse { Success = false, Error = "Username inválido." });
                return;
            }
            if (string.IsNullOrWhiteSpace(msg.PasswordHash))
            {
                conn.Send(new MsgCreateAccountResponse { Success = false, Error = "Senha inválida." });
                return;
            }

            var error = DatabaseManager.Instance?.TryCreateAccount(msg.Username, msg.PasswordHash);
            if (error != null)
            {
                conn.Send(new MsgCreateAccountResponse { Success = false, Error = error });
                return;
            }
            Debug.Log($"[ServerAuth] Conta criada: {msg.Username}");
            conn.Send(new MsgCreateAccountResponse { Success = true });
        }

        // ── Lista de personagens ───────────────────────────────────────────

        private void OnRequestCharacterList(NetworkConnectionToClient conn, MsgRequestCharacterList msg)
        {
            if (!RequireAuth(conn, out var session)) return;
            UpdateActivity(session);

            var chars = DatabaseManager.Instance?.LoadCharacters(session.Username)
                        ?? new List<CharacterData>();
            SendCharacterList(conn, session.Username, chars);
        }

        private void SendCharacterList(NetworkConnectionToClient conn, AccountData account)
            => SendCharacterList(conn, account.Username, account.Characters ?? new List<CharacterData>());

        private void SendCharacterList(NetworkConnectionToClient conn, string username, List<CharacterData> chars)
        {
            var list = new List<CharacterSummary>();
            foreach (var ch in chars)
                list.Add(new CharacterSummary
                {
                    CharacterId   = ch.CharacterId,
                    CharacterName = ch.CharacterName,
                    Race          = ch.Race.ToString(),
                    Level         = ch.Level
                });
            conn.Send(new MsgCharacterListResponse { Characters = list });
        }

        // ── Criar personagem ───────────────────────────────────────────────

        private void OnCreateCharacterRequest(NetworkConnectionToClient conn, MsgCreateCharacterRequest msg)
        {
            if (!RequireAuth(conn, out var session)) return;
            UpdateActivity(session);

            var error = DatabaseManager.Instance?.TryCreateCharacter(
                session.Username, msg.Name, (CharacterRace)msg.RaceIndex);

            if (error != null)
            {
                conn.Send(new MsgCreateCharacterResponse { Success = false, Error = error });
                return;
            }

            var chars = DatabaseManager.Instance?.LoadCharacters(session.Username)
                        ?? new List<CharacterData>();
            var list = new List<CharacterSummary>();
            foreach (var ch in chars)
                list.Add(new CharacterSummary
                {
                    CharacterId   = ch.CharacterId,
                    CharacterName = ch.CharacterName,
                    Race          = ch.Race.ToString(),
                    Level         = ch.Level
                });

            conn.Send(new MsgCreateCharacterResponse { Success = true, UpdatedList = list });
            Debug.Log($"[ServerAuth] Personagem criado: {msg.Name} (conta:{session.Username})");
        }

        // ── Selecionar personagem ──────────────────────────────────────────

        private void OnSelectCharacter(NetworkConnectionToClient conn, MsgSelectCharacter msg)
        {
            if (!RequireAuth(conn, out var session)) return;

            if (session.State == ConnState.InGame)
            {
                conn.Send(new MsgSelectCharacterResponse { Success = false, Error = "Já está em jogo." });
                return;
            }

            var charData = DatabaseManager.Instance?.LoadCharacterForAccount(
                msg.CharacterId, session.Username);

            if (charData == null)
            {
                conn.Send(new MsgSelectCharacterResponse
                    { Success = false, Error = "Personagem não encontrado ou não pertence a esta conta." });
                Debug.LogWarning($"[ServerAuth] SECURITY: {session.Username} tentou selecionar personagem {msg.CharacterId}");
                return;
            }

            session.State        = ConnState.InGame;
            session.CharacterId  = msg.CharacterId;
            UpdateActivity(session);

            RPGNetworkManager.singleton?.SpawnPlayerForConnection(conn, charData, session.Username);
            Debug.Log($"[ServerAuth] {charData.CharacterName} ({charData.Race}) entrando | conn:{conn.connectionId}");
        }

        // ── Helpers ────────────────────────────────────────────────────────

        private bool RequireAuth(NetworkConnectionToClient conn, out ConnData session)
        {
            if (!_sessions.TryGetValue(conn.connectionId, out session))
            {
                conn.Send(new MsgErrorResponse { Error = "Sessão inválida." });
                return false;
            }
            if (session.State == ConnState.Unauthenticated)
            {
                conn.Send(new MsgErrorResponse { Error = "Não autenticado." });
                return false;
            }
            return true;
        }

        private static void UpdateActivity(ConnData session)
            => session.LastActivityTime = Time.time;

        private void LogAuth(string msg)
        {
            if (debugAuth) Debug.Log($"[ServerAuth-DEBUG] {msg}");
        }

        // ── Limpeza de sessões expiradas ───────────────────────────────────

        private IEnumerator CleanupExpiredSessions()
        {
            var wait = new WaitForSeconds(60f);
            while (true)
            {
                yield return wait;

                var expired = new List<int>();
                foreach (var kv in _sessions)
                {
                    if (kv.Value.State == ConnState.Unauthenticated &&
                        Time.time - kv.Value.LastActivityTime > SESSION_TTL_SECONDS)
                    {
                        expired.Add(kv.Key);
                    }
                }

                foreach (var id in expired)
                {
                    _sessions.Remove(id);
                    Debug.Log($"[ServerAuthManager] Sessão expirada removida: connId={id}");
                }
            }
        }
    }
}
