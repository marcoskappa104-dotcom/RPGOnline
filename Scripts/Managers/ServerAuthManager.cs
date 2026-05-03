using UnityEngine;
using Mirror;
using RPG.Data;
using RPG.Managers;
using System.Collections.Generic;

namespace RPG.Network
{
    /// <summary>
    /// ServerAuthManager — roda APENAS no servidor.
    /// Gerencia todo o fluxo de autenticação antes do player entrar no jogo:
    ///   Login → Lista de personagens → Criação → Seleção → Spawn
    ///
    /// Cada conexão passa por estados:
    ///   Unauthenticated → Authenticated → CharacterSelected → InGame
    /// </summary>
    public class ServerAuthManager : MonoBehaviour
    {
        public static ServerAuthManager Instance { get; private set; }

        private enum ConnState { Unauthenticated, Authenticated, InGame }

        private class ConnData
        {
            public ConnState State       = ConnState.Unauthenticated;
            public string    Username    = "";
            public string    CharacterId = "";
        }

        // Mapa connectionId → dados da sessão
        private readonly Dictionary<int, ConnData> _sessions = new();

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        private void Start()
        {
            if (!NetworkServer.active) return;
            RegisterHandlers();
            Debug.Log("[ServerAuthManager] Handlers registrados.");
        }

        private void RegisterHandlers()
        {
            NetworkServer.RegisterHandler<MsgLoginRequest>         (OnLoginRequest,         false);
            NetworkServer.RegisterHandler<MsgCreateAccountRequest> (OnCreateAccountRequest, false);
            NetworkServer.RegisterHandler<MsgRequestCharacterList> (OnRequestCharacterList, false);
            NetworkServer.RegisterHandler<MsgCreateCharacterRequest>(OnCreateCharacterRequest, false);
            NetworkServer.RegisterHandler<MsgSelectCharacter>      (OnSelectCharacter,      false);
        }

        // ── Conexão / Desconexão ────────────────────────────────────────

        public void OnServerConnect(NetworkConnectionToClient conn)
        {
            _sessions[conn.connectionId] = new ConnData();
            Debug.Log($"[ServerAuth] Nova conexão: {conn.connectionId}");
        }

        public void OnServerDisconnect(NetworkConnectionToClient conn)
        {
            _sessions.Remove(conn.connectionId);
            Debug.Log($"[ServerAuth] Desconectado: {conn.connectionId}");
        }

        // ── Login ────────────────────────────────────────────────────────

        private void OnLoginRequest(NetworkConnectionToClient conn, MsgLoginRequest msg)
        {
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

            var account = SaveManager.Instance?.LoadAccount(msg.Username);
            if (account == null || account.PasswordHash != msg.PasswordHash)
            {
                conn.Send(new MsgLoginResponse { Success = false, Error = "Usuário ou senha incorretos." });
                return;
            }

            session.State    = ConnState.Authenticated;
            session.Username = account.Username;

            Debug.Log($"[ServerAuth] Login OK: {account.Username} (conn:{conn.connectionId})");

            conn.Send(new MsgLoginResponse
            {
                Success  = true,
                Username = account.Username
            });

            // Envia lista de personagens automaticamente após login
            SendCharacterList(conn, account);
        }

        // ── Criar conta ──────────────────────────────────────────────────

        private void OnCreateAccountRequest(NetworkConnectionToClient conn, MsgCreateAccountRequest msg)
        {
            var error = SaveManager.Instance?.TryCreateAccount(msg.Username, msg.PasswordHash, alreadyHashed: true);
            if (error != null)
            {
                conn.Send(new MsgCreateAccountResponse { Success = false, Error = error });
                return;
            }

            Debug.Log($"[ServerAuth] Conta criada: {msg.Username}");
            conn.Send(new MsgCreateAccountResponse { Success = true });
        }

        // ── Lista de personagens ─────────────────────────────────────────

        private void OnRequestCharacterList(NetworkConnectionToClient conn, MsgRequestCharacterList msg)
        {
            if (!RequireAuth(conn, out var session, out var account)) return;
            SendCharacterList(conn, account);
        }

        private void SendCharacterList(NetworkConnectionToClient conn, AccountData account)
        {
            var list = new List<CharacterSummary>();
            foreach (var ch in account.Characters)
            {
                list.Add(new CharacterSummary
                {
                    CharacterId   = ch.CharacterId,
                    CharacterName = ch.CharacterName,
                    Race          = ch.Race.ToString(),
                    Level         = ch.Level
                });
            }
            conn.Send(new MsgCharacterListResponse { Characters = list });
        }

        // ── Criar personagem ─────────────────────────────────────────────

        private void OnCreateCharacterRequest(NetworkConnectionToClient conn, MsgCreateCharacterRequest msg)
        {
            if (!RequireAuth(conn, out var session, out var account)) return;

            var race  = (CharacterRace)msg.RaceIndex;
            var error = SaveManager.Instance?.TryCreateCharacter(account, msg.Name, race);

            if (error != null)
            {
                conn.Send(new MsgCreateCharacterResponse { Success = false, Error = error });
                return;
            }

            // Recarrega a conta para pegar o personagem recém-criado
            account = SaveManager.Instance?.LoadAccount(session.Username);

            var list = new List<CharacterSummary>();
            if (account != null)
                foreach (var ch in account.Characters)
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

        // ── Selecionar personagem → spawna player ────────────────────────

        private void OnSelectCharacter(NetworkConnectionToClient conn, MsgSelectCharacter msg)
        {
            if (!RequireAuth(conn, out var session, out var account)) return;

            var charData = account.Characters.Find(c => c.CharacterId == msg.CharacterId);
            if (charData == null)
            {
                conn.Send(new MsgSelectCharacterResponse { Success = false, Error = "Personagem não encontrado." });
                return;
            }

            if (session.State == ConnState.InGame)
            {
                conn.Send(new MsgSelectCharacterResponse { Success = false, Error = "Já está em jogo." });
                return;
            }

            session.State       = ConnState.InGame;
            session.CharacterId = msg.CharacterId;

            conn.Send(new MsgSelectCharacterResponse { Success = true });

            // Delega spawn ao RPGNetworkManager
            RPGNetworkManager.singleton?.SpawnPlayerForConnection(conn, charData, session.Username);
            Debug.Log($"[ServerAuth] {charData.CharacterName} entrando no jogo (conn:{conn.connectionId})");
        }

        // ── Helpers ──────────────────────────────────────────────────────

        private bool RequireAuth(NetworkConnectionToClient conn, out ConnData session, out AccountData account)
        {
            account = null;
            if (!_sessions.TryGetValue(conn.connectionId, out session))
            {
                conn.Send(new MsgLoginResponse { Success = false, Error = "Sessão inválida." });
                return false;
            }
            if (session.State == ConnState.Unauthenticated)
            {
                conn.Send(new MsgLoginResponse { Success = false, Error = "Não autenticado." });
                return false;
            }

            account = SaveManager.Instance?.LoadAccount(session.Username);
            if (account == null)
            {
                conn.Send(new MsgLoginResponse { Success = false, Error = "Conta não encontrada no servidor." });
                return false;
            }
            return true;
        }

        /// <summary>Retorna o username da sessão de uma conexão (para uso externo).</summary>
        public string GetUsername(int connectionId)
        {
            return _sessions.TryGetValue(connectionId, out var s) ? s.Username : "";
        }

        public bool IsInGame(int connectionId)
        {
            return _sessions.TryGetValue(connectionId, out var s) && s.State == ConnState.InGame;
        }
    }
}
