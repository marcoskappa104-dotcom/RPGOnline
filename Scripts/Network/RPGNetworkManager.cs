using System.Collections.Generic;
using UnityEngine;
using Mirror;
using RPG.Managers;

namespace RPG.Network
{
    /// <summary>
    /// RPGNetworkManager — substitui o NetworkManager padrão do Mirror.
    ///
    /// SETUP NO INSPECTOR:
    ///   - Player Prefab        → NetworkPlayerPrefab
    ///   - Spawnable Prefabs    → arraste TODOS os prefabs de monstro aqui
    ///   - Spawn Points         → Transforms de spawn do player
    /// </summary>
    public class RPGNetworkManager : NetworkManager
    {
        public static new RPGNetworkManager singleton =>
            (RPGNetworkManager)NetworkManager.singleton;

        [Header("RPG Settings")]
        [SerializeField] private Transform[] spawnPoints;

        [Header("Spawnable Prefabs")]
        [Tooltip("Arraste TODOS os prefabs de monstro aqui (precisam ter NetworkIdentity)")]
        [SerializeField] private List<GameObject> spawnablePrefabs = new List<GameObject>();

        private readonly Dictionary<int, NetworkPlayer> _connectedPlayers = new();

        // ── Registro de prefabs ───────────────────────────────────────────

        public override void Awake()
        {
            base.Awake();
            RegisterSpawnablePrefabs();
        }

        private void RegisterSpawnablePrefabs()
        {
            foreach (var prefab in spawnablePrefabs)
            {
                if (prefab == null) continue;

                var identity = prefab.GetComponent<NetworkIdentity>();
                if (identity == null)
                {
                    Debug.LogError($"[RPGNetworkManager] '{prefab.name}' não tem NetworkIdentity! " +
                                   "Adicione o componente NetworkIdentity ao prefab.");
                    continue;
                }

                // Evita registrar duplicado
                if (NetworkClient.prefabs.ContainsKey(identity.assetId))
                {
                    Debug.Log($"[RPGNetworkManager] '{prefab.name}' já registrado (assetId={identity.assetId}).");
                    continue;
                }

                NetworkClient.RegisterPrefab(prefab);
                Debug.Log($"[RPGNetworkManager] Prefab registrado: '{prefab.name}' (assetId={identity.assetId})");
            }
        }

        // ── Server callbacks ──────────────────────────────────────────────

        public override void OnServerAddPlayer(NetworkConnectionToClient conn)
        {
            Transform spawn = GetSpawnPoint();
            var playerGO = Instantiate(playerPrefab, spawn.position, spawn.rotation);
            NetworkServer.AddPlayerForConnection(conn, playerGO);

            var netPlayer = playerGO.GetComponent<NetworkPlayer>();
            if (netPlayer != null)
                _connectedPlayers[conn.connectionId] = netPlayer;

            Debug.Log($"[Server] Player conectado: connId={conn.connectionId} — total={numPlayers}");
        }

        public override void OnServerDisconnect(NetworkConnectionToClient conn)
        {
            _connectedPlayers.Remove(conn.connectionId);
            base.OnServerDisconnect(conn);
            Debug.Log($"[Server] Player desconectado: connId={conn.connectionId} — total={numPlayers}");
        }

        private Transform GetSpawnPoint()
        {
            if (spawnPoints == null || spawnPoints.Length == 0)
            {
                var go = new GameObject("DefaultSpawn");
                go.transform.position = Vector3.zero;
                return go.transform;
            }
            return spawnPoints[numPlayers % spawnPoints.Length];
        }

        // ── Client callbacks ──────────────────────────────────────────────

        public override void OnClientConnect()
        {
            base.OnClientConnect();
            Debug.Log("[Client] Conectado ao servidor.");
        }

        public override void OnClientDisconnect()
        {
            base.OnClientDisconnect();
            Debug.Log("[Client] Desconectado do servidor.");
            GameManager.Instance?.Logout();
        }

        public IEnumerable<NetworkPlayer> GetAllPlayers() => _connectedPlayers.Values;
    }
}
