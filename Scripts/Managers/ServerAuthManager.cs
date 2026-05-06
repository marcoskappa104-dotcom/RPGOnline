using UnityEngine;
using Mirror;
using RPG.Data;
using RPG.Managers;
using System.Collections.Generic;

namespace RPG.Network
{
    /// <summary>
    /// ServerAuthManager v5 — Atualizado para usar DatabaseManager (SQLite).
    ///
    /// MUDANÇAS em relação à v4:
    ///   - Toda referência a SaveManager substituída por DatabaseManager.
    ///   - LoadAccount() não existe mais: login retorna AccountData diretamente
    ///     via TryLoginWithHash (que já carrega os personagens do banco).
    ///   - RequireAuth agora usa _sessions para recuperar o AccountData
    ///     que já foi carregado no momento do login (sem reler o banco).
    ///   - OnSelectCharacter carrega o CharacterData diretamente pelo ID,
    ///     sem precisar do AccountData completo.
    ///
    /// REMOVER DO PROJETO:
    ///   DELETE: SaveManager.cs
    /// </summary>
    public class ServerAuthManager : MonoBehaviour
    {
        public static ServerAuthManager Instance { get; private set; }

        private enum ConnState { Unauthenticated, Authenticated, InGame }

        private class ConnData
        {
            public ConnState  State       = ConnState.Unauthenticated;
            public string     Username    = "";
            public string     CharacterId = "";
            public AccountData CachedAccount = null; // Evita reler banco
        }

        private readonly Dictionary<int, ConnData> _sessions = new();

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this); return; }
            Instance = this;
        }

        public void RegisterHandlers()
        {
            NetworkServer.RegisterHandler<MsgLoginRequest>          (OnLoginRequest,          false);
            NetworkServer.RegisterHandler<MsgCreateAccountRequest>  (OnCreateAccountRequest,  false);
            NetworkServer.RegisterHandler<MsgRequestCharacterList>  (OnRequestCharacterList,  false);
            NetworkServer.RegisterHandler<MsgCreateCharacterRequest>(OnCreateCharacterRequest, false);
            NetworkServer.RegisterHandler<MsgSelectCharacter>       (OnSelectCharacter,        false);
            Debug.Log("[ServerAuthManager] Handlers registrados.");
        }

        public void OnServerConnect(NetworkConnectionToClient conn)
        {
            _sessions[conn.connectionId] = new ConnData();
            Debug.Log($"[ServerAuth] Nova conexão: {conn.connectionId}");
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

            // DatabaseManager faz login + carrega personagens em uma transação
            var account = DatabaseManager.Instance?.TryLoginWithHash(msg.Username, msg.PasswordHash);
            if (account == null)
            {
                conn.Send(new MsgLoginResponse { Success = false, Error = "Usuário ou senha incorretos." });
                return;
            }

            session.State         = ConnState.Authenticated;
            session.Username      = account.Username;
            session.CachedAccount = account; // cacheia para não reler o banco

            Debug.Log($"[ServerAuth] Login OK: {account.Username}");
            conn.Send(new MsgLoginResponse { Success = true, Username = account.Username });
            SendCharacterList(conn, account);
        }

        // ── Criar conta ────────────────────────────────────────────────────

        private void OnCreateAccountRequest(NetworkConnectionToClient conn, MsgCreateAccountRequest msg)
        {
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

            // Recarrega lista atualizada do banco
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

            var error = DatabaseManager.Instance?.TryCreateCharacter(
                session.Username, msg.Name, (CharacterRace)msg.RaceIndex);

            if (error != null)
            {
                conn.Send(new MsgCreateCharacterResponse { Success = false, Error = error });
                return;
            }

            // Recarrega lista atualizada
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

            // Carrega o personagem diretamente do banco pelo ID
            var charData = DatabaseManager.Instance?.LoadCharacter(msg.CharacterId);

            // Verifica se o personagem pertence a esta conta
            if (charData == null)
            {
                conn.Send(new MsgSelectCharacterResponse { Success = false, Error = "Personagem não encontrado." });
                return;
            }

            // Segurança: verifica ownership pelo banco (evita seleção de personagem de outra conta)
            var ownedChars = DatabaseManager.Instance?.LoadCharacters(session.Username);
            bool owned = ownedChars?.Exists(c => c.CharacterId == msg.CharacterId) ?? false;
            if (!owned)
            {
                conn.Send(new MsgSelectCharacterResponse { Success = false, Error = "Personagem não pertence a esta conta." });
                Debug.LogWarning($"[ServerAuth] SECURITY: {session.Username} tentou selecionar personagem de outra conta!");
                return;
            }

            session.State       = ConnState.InGame;
            session.CharacterId = msg.CharacterId;

            RPGNetworkManager.singleton?.SpawnPlayerForConnection(conn, charData, session.Username);
            Debug.Log($"[ServerAuth] {charData.CharacterName} ({charData.Race}) entrando | conn:{conn.connectionId}");
        }

        // ── RequireAuth simplificado ───────────────────────────────────────

        private bool RequireAuth(NetworkConnectionToClient conn, out ConnData session)
        {
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
            return true;
        }
    }
}
