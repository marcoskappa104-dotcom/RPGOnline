using System.Collections.Generic;
using UnityEngine;
using Mirror;
using RPG.Managers;
using RPG.Data;

namespace RPG.Network
{
    /// <summary>
    /// RPGNetworkManager v4 — Server-Authoritative
    ///
    /// Mudanças:
    ///   - NÃO chama AddPlayerForConnection em OnServerAddPlayer.
    ///     O spawn acontece SOMENTE após login + seleção de personagem.
    ///   - SpawnPlayerForConnection é chamado pelo ServerAuthManager.
    ///   - Registra handlers de conexão/desconexão no ServerAuthManager.
    /// </summary>
    public class RPGNetworkManager : NetworkManager
    {
        public static new RPGNetworkManager singleton =>
            (RPGNetworkManager)NetworkManager.singleton;

        [Header("RPG Settings")]
        [SerializeField] private Transform[] spawnPoints;

        [Header("Spawnable Prefabs")]
        [Tooltip("Todos os prefabs de monstro (precisam ter NetworkIdentity)")]
        [SerializeField] private List<GameObject> spawnablePrefabs = new List<GameObject>();

        private ServerAuthManager _authManager;

        // ── Lifecycle ────────────────────────────────────────────────────

        public override void Start()
        {
            base.Start();
            RegisterSpawnablePrefabs();
        }

        public override void OnStartServer()
        {
            base.OnStartServer();
            _authManager = GetComponent<ServerAuthManager>();
            if (_authManager == null)
                _authManager = gameObject.AddComponent<ServerAuthManager>();

            Debug.Log("[RPGNetworkManager] Servidor iniciado.");
        }

        public override void OnStartClient()
        {
            base.OnStartClient();
            RegisterSpawnablePrefabs();
        }

        // ── Conexões ─────────────────────────────────────────────────────

        /// <summary>
        /// ATENÇÃO: NÃO spawna o player aqui.
        /// O player só é spawnado após login + seleção de personagem.
        /// </summary>
        public override void OnServerConnect(NetworkConnectionToClient conn)
        {
            base.OnServerConnect(conn);
            _authManager?.OnServerConnect(conn);
        }

        public override void OnServerDisconnect(NetworkConnectionToClient conn)
        {
            _authManager?.OnServerDisconnect(conn);
            base.OnServerDisconnect(conn);
            Debug.Log($"[Server] Desconectado: connId={conn.connectionId}");
        }

        /// <summary>
        /// Sobrescrito para NÃO spawnar automaticamente.
        /// O spawn ocorre via SpawnPlayerForConnection.
        /// </summary>
        public override void OnServerAddPlayer(NetworkConnectionToClient conn)
        {
            // Intencionalmente vazio — não spawna aqui.
            // O ServerAuthManager dispara SpawnPlayerForConnection quando apropriado.
        }

        // ── Spawn do player (chamado pelo ServerAuthManager) ─────────────

        /// <summary>
        /// Spawna o player após autenticação e seleção de personagem.
        /// </summary>
        [Server]
        public void SpawnPlayerForConnection(
            NetworkConnectionToClient conn,
            CharacterData charData,
            string accountUsername)
        {
            Transform spawn    = GetSpawnPoint(charData);
            var       playerGO = Instantiate(playerPrefab, spawn.position, spawn.rotation);

            NetworkServer.AddPlayerForConnection(conn, playerGO);

            var netPlayer = playerGO.GetComponent<NetworkPlayer>();
            netPlayer?.ServerInitialize(charData, accountUsername);

            Debug.Log($"[Server] Player spawnado: {charData.CharacterName} | " +
                      $"connId={conn.connectionId} | pos={spawn.position}");
        }

        // ── Prefabs ──────────────────────────────────────────────────────

        private void RegisterSpawnablePrefabs()
        {
            foreach (var prefab in spawnablePrefabs)
            {
                if (prefab == null) continue;
                var identity = prefab.GetComponent<NetworkIdentity>();
                if (identity == null)
                {
                    Debug.LogError($"[RPGNetworkManager] '{prefab.name}' sem NetworkIdentity!");
                    continue;
                }
                if (!NetworkClient.prefabs.ContainsKey(identity.assetId))
                {
                    NetworkClient.RegisterPrefab(prefab);
                    Debug.Log($"[RPGNetworkManager] Prefab registrado: {prefab.name}");
                }
            }
        }

        // ── Spawn point ──────────────────────────────────────────────────

        private Transform GetSpawnPoint(CharacterData charData = null)
        {
            // Se o personagem tem posição salva e não é (0,0,0), usa ela
            if (charData != null)
            {
                var saved = new Vector3(charData.PosX, charData.PosY, charData.PosZ);
                if (saved.sqrMagnitude > 0.1f)
                {
                    var go = new GameObject("SavedSpawn");
                    go.transform.position = saved;
                    Destroy(go, 1f);
                    return go.transform;
                }
            }

            if (spawnPoints != null && spawnPoints.Length > 0)
                return spawnPoints[numPlayers % spawnPoints.Length];

            var def = new GameObject("DefaultSpawn");
            def.transform.position = new Vector3(0f, 0f, 0f);
            Destroy(def, 1f);
            return def.transform;
        }

        // ── Cliente ──────────────────────────────────────────────────────

        public override void OnClientConnect()
        {
            base.OnClientConnect();
            Debug.Log("[Client] Conectado ao servidor.");
            // NÃO chama AddPlayer aqui — o login é feito via mensagens diretas.
        }

        public override void OnClientDisconnect()
        {
            base.OnClientDisconnect();
            Debug.Log("[Client] Desconectado do servidor.");
            // Volta para tela de login
            UnityEngine.SceneManagement.SceneManager.LoadScene(
                GameManager.SCENE_LOGIN);
        }
    }
}
