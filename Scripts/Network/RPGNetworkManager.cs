using System.Collections.Generic;
using UnityEngine;
using Mirror;
using RPG.Managers;
using RPG.Data;

namespace RPG.Network
{
    /// <summary>
    /// RPGNetworkManager v5
    ///
    /// CORREÇÕES:
    ///   - DontDestroyOnLoad correto: o Mirror já faz isso internamente,
    ///     mas precisamos garantir que não duplique entre cenas.
    ///   - OnClientDisconnect NÃO carrega LoginScene automaticamente
    ///     (isso estava causando loop infinito de reconexão).
    ///     A navegação é responsabilidade do ClientAuthHandler / UI.
    ///   - OnServerAddPlayer intencionalmente vazio (spawn ocorre via
    ///     ServerAuthManager após login + seleção de personagem).
    /// </summary>
    public class RPGNetworkManager : NetworkManager
    {
        public static new RPGNetworkManager singleton =>
            NetworkManager.singleton as RPGNetworkManager;

        [Header("RPG Settings")]
        [SerializeField] private Transform[] spawnPoints;

        [Header("Spawnable Prefabs")]
        [Tooltip("Todos os prefabs de monstro (precisam ter NetworkIdentity)")]
        [SerializeField] private List<GameObject> spawnablePrefabs = new List<GameObject>();

        private ServerAuthManager _authManager;

        // ── Lifecycle ──────────────────────────────────────────────────────

        public override void Awake()
        {
            // Mirror's NetworkManager.Awake() já chama DontDestroyOnLoad e
            // destrói duplicatas — NÃO chame base.Awake() manualmente se já
            // estiver herdando. Apenas delegue.
            base.Awake();
        }

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

            _authManager.RegisterHandlers();
            Debug.Log("[RPGNetworkManager] Servidor iniciado.");
        }

        public override void OnStartClient()
        {
            base.OnStartClient();
            RegisterSpawnablePrefabs();
        }

        // ── Conexões do servidor ───────────────────────────────────────────

        public override void OnServerConnect(NetworkConnectionToClient conn)
        {
            base.OnServerConnect(conn);
            _authManager?.OnServerConnect(conn);
        }

        public override void OnServerDisconnect(NetworkConnectionToClient conn)
        {
            _authManager?.OnServerDisconnect(conn);
            base.OnServerDisconnect(conn);
            Debug.Log($"[Server] Player desconectado: connId={conn.connectionId}");
        }

        /// <summary>
        /// Intencionalmente VAZIO.
        /// O player só spawna após login + seleção de personagem via ServerAuthManager.
        /// </summary>
        public override void OnServerAddPlayer(NetworkConnectionToClient conn)
        {
            // Não faz nada — o spawn é disparado pelo ServerAuthManager.
        }

        // ── Spawn do player (chamado pelo ServerAuthManager) ───────────────

        [Server]
        public void SpawnPlayerForConnection(
            NetworkConnectionToClient conn,
            CharacterData charData,
            string accountUsername)
        {
            if (playerPrefab == null)
            {
                Debug.LogError("[RPGNetworkManager] playerPrefab não configurado!");
                return;
            }

            Transform spawn    = GetSpawnPoint(charData);
            var       playerGO = Instantiate(playerPrefab, spawn.position, spawn.rotation);

            NetworkServer.AddPlayerForConnection(conn, playerGO);

            var netPlayer = playerGO.GetComponent<NetworkPlayer>();
            netPlayer?.ServerInitialize(charData, accountUsername);

            Debug.Log($"[Server] Player spawnado: {charData.CharacterName} | " +
                      $"connId={conn.connectionId} | pos={spawn.position}");
        }

        // ── Conexões do cliente ────────────────────────────────────────────

        public override void OnClientConnect()
        {
            base.OnClientConnect();
            Debug.Log("[Client] Conectado ao servidor.");
            // NÃO chama AddPlayer — o login é feito via mensagens (ClientAuthHandler).
        }

        public override void OnClientDisconnect()
        {
            base.OnClientDisconnect();
            Debug.Log("[Client] Desconectado do servidor.");

            // Notifica a UI — a UI decide o que fazer (voltar ao login, mostrar mensagem, etc.)
            // NÃO carregamos cena aqui para evitar loop de reconexão.
            ClientAuthHandler.Instance?.OnDisconnectedFromServer();
        }

        // ── Helpers ────────────────────────────────────────────────────────

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

        private Transform GetSpawnPoint(CharacterData charData = null)
        {
            if (charData != null)
            {
                var saved = new Vector3(charData.PosX, charData.PosY, charData.PosZ);
                if (saved.sqrMagnitude > 0.1f)
                {
                    var go = new GameObject("SavedSpawn");
                    go.transform.position = saved;
                    Destroy(go, 2f);
                    return go.transform;
                }
            }

            if (spawnPoints != null && spawnPoints.Length > 0)
                return spawnPoints[numPlayers % spawnPoints.Length];

            var def = new GameObject("DefaultSpawn");
            def.transform.position = Vector3.zero;
            Destroy(def, 2f);
            return def.transform;
        }
    }
}