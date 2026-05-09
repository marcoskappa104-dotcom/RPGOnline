using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using Mirror;
using RPG.Managers;
using RPG.Data;

namespace RPG.Network
{
    /// <summary>
    /// RPGNetworkManager v8
    ///
    /// CORREÇÕES v8:
    ///   1. TIMEOUT em _pendingSpawns: se o cliente cair após selecionar personagem
    ///      mas antes de enviar MsgClientSceneReady, a entrada ficava no dicionário
    ///      para sempre — vazamento de memória e potencial bug de spawn tardio.
    ///      Agora cada entrada tem um timeout de PENDING_SPAWN_TIMEOUT segundos.
    ///      Se expirar, a entrada é removida automaticamente.
    ///
    ///   2. OnServerDisconnect agora cancela coroutines de spawn pendentes
    ///      para a conexão que caiu, evitando spawn de ghost players.
    ///
    ///   3. DoSpawnPlayer verifica conn.isReady antes E depois do yield para
    ///      cobrir desconexões durante a espera pelo NavMesh.
    ///
    ///   4. RegisterSpawnablePrefabs: proteção contra prefabs null na lista.
    /// </summary>
    public class RPGNetworkManager : NetworkManager
    {
        public static new RPGNetworkManager singleton =>
            NetworkManager.singleton as RPGNetworkManager;

        private static readonly Dictionary<CharacterRace, Vector3> RaceSpawnPoints = new()
        {
            { CharacterRace.Human,  new Vector3(   0f, 1f,   0f) },
            { CharacterRace.Elf,    new Vector3(  20f, 1f,  10f) },
            { CharacterRace.Dwarf,  new Vector3( -20f, 1f,  10f) },
            { CharacterRace.Orc,    new Vector3(   0f, 1f,  30f) },
            { CharacterRace.Undead, new Vector3( -20f, 1f, -10f) },
        };

        private const float SPAWN_NAVMESH_RADIUS    = 15f;

        /// <summary>
        /// CORREÇÃO v8: tempo máximo que um spawn pode ficar pendente.
        /// Se o cliente não confirmar a cena em 30s, o spawn é cancelado.
        /// </summary>
        private const float PENDING_SPAWN_TIMEOUT = 30f;

        [Header("Spawnable Prefabs")]
        [Tooltip("Todos os prefabs de monstro (precisam ter NetworkIdentity)")]
        [SerializeField] private List<GameObject> spawnablePrefabs = new List<GameObject>();

        private readonly Dictionary<int, PendingSpawn> _pendingSpawns = new();

        // CORREÇÃO v8: rastreia coroutines de spawn por connectionId para cancelamento
        private readonly Dictionary<int, Coroutine> _spawnCoroutines = new();

        private ServerAuthManager _authManager;

        private struct PendingSpawn
        {
            public NetworkConnectionToClient Conn;
            public CharacterData            CharData;
            public string                   AccountUsername;
            public float                    ExpiresAt; // CORREÇÃO v8: timestamp de expiração
        }

        // ── Lifecycle ──────────────────────────────────────────────────────

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

            NetworkServer.RegisterHandler<MsgClientSceneReady>(OnClientSceneReady, false);

            // CORREÇÃO v8: coroutine que limpa spawns expirados
            StartCoroutine(CleanExpiredPendingSpawns());

            Debug.Log("[RPGNetworkManager] Servidor iniciado.");
        }

        public override void OnStartClient()
        {
            base.OnStartClient();
            RegisterSpawnablePrefabs();
        }

        // ── Conexões ───────────────────────────────────────────────────────

        public override void OnServerConnect(NetworkConnectionToClient conn)
        {
            base.OnServerConnect(conn);
            _authManager?.OnServerConnect(conn);
        }

        public override void OnServerDisconnect(NetworkConnectionToClient conn)
        {
            // CORREÇÃO v8: cancela spawn pendente da conexão que caiu
            _pendingSpawns.Remove(conn.connectionId);

            if (_spawnCoroutines.TryGetValue(conn.connectionId, out var coroutine))
            {
                if (coroutine != null) StopCoroutine(coroutine);
                _spawnCoroutines.Remove(conn.connectionId);
                Debug.Log($"[RPGNetworkManager] Spawn cancelado para conn:{conn.connectionId} (desconectou).");
            }

            _authManager?.OnServerDisconnect(conn);
            base.OnServerDisconnect(conn);
            Debug.Log($"[Server] Player desconectado: connId={conn.connectionId}");
        }

        public override void OnServerAddPlayer(NetworkConnectionToClient conn)
        {
            // Vazio — spawn é controlado pelo fluxo ServerAuthManager → SpawnPlayerForConnection
        }

        public override void OnClientConnect()
        {
            base.OnClientConnect();
            Debug.Log("[Client] Conectado ao servidor.");
        }

        public override void OnClientDisconnect()
        {
            base.OnClientDisconnect();
            Debug.Log("[Client] Desconectado do servidor.");
            ClientAuthHandler.Instance?.OnDisconnectedFromServer();
        }

        public override void OnServerSceneChanged(string sceneName)
        {
            base.OnServerSceneChanged(sceneName);
            RegisterSpawnablePrefabs();
        }

        // ── Spawn do player ────────────────────────────────────────────────

        [Server]
        public void SpawnPlayerForConnection(
            NetworkConnectionToClient conn,
            CharacterData charData,
            string accountUsername)
        {
            if (playerPrefab == null)
            {
                Debug.LogError("[RPGNetworkManager] playerPrefab não configurado!");
                conn.Send(new MsgSelectCharacterResponse { Success = false, Error = "Erro interno do servidor." });
                return;
            }

            _pendingSpawns[conn.connectionId] = new PendingSpawn
            {
                Conn            = conn,
                CharData        = charData,
                AccountUsername = accountUsername,
                ExpiresAt       = Time.time + PENDING_SPAWN_TIMEOUT // CORREÇÃO v8
            };

            conn.Send(new MsgSelectCharacterResponse { Success = true });

            Debug.Log($"[RPGNetworkManager] {charData.CharacterName} na fila. " +
                      $"Timeout em {PENDING_SPAWN_TIMEOUT}s. Aguardando cena pronta.");
        }

        [Server]
        private void OnClientSceneReady(NetworkConnectionToClient conn, MsgClientSceneReady msg)
        {
            if (!_pendingSpawns.TryGetValue(conn.connectionId, out var pending))
            {
                Debug.LogWarning($"[RPGNetworkManager] MsgClientSceneReady de conn:{conn.connectionId} " +
                                 "sem spawn pendente (talvez já expirou). Ignorando.");
                return;
            }

            // CORREÇÃO v8: verifica se ainda não expirou
            if (Time.time > pending.ExpiresAt)
            {
                Debug.LogWarning($"[RPGNetworkManager] Spawn de {pending.CharData.CharacterName} expirou. " +
                                 "Cliente demorou mais de 30s para confirmar a cena.");
                _pendingSpawns.Remove(conn.connectionId);
                return;
            }

            _pendingSpawns.Remove(conn.connectionId);
            Debug.Log($"[RPGNetworkManager] Cena confirmada. Spawnando {pending.CharData.CharacterName}...");

            var coroutine = StartCoroutine(DoSpawnPlayer(conn, pending.CharData, pending.AccountUsername));
            _spawnCoroutines[conn.connectionId] = coroutine;
        }

        [Server]
        private IEnumerator DoSpawnPlayer(
            NetworkConnectionToClient conn,
            CharacterData charData,
            string accountUsername)
        {
            // CORREÇÃO v8: verifica antes de começar a esperar
            if (conn == null || !conn.isReady)
            {
                Debug.LogWarning("[RPGNetworkManager] Conexão perdida antes de iniciar spawn.");
                _spawnCoroutines.Remove(conn?.connectionId ?? -1);
                yield break;
            }

            Vector3 spawnPos = GetSpawnPositionForRace(charData.Race, charData);

            float elapsed = 0f;
            while (elapsed < 5f)
            {
                if (NavMesh.SamplePosition(spawnPos, out NavMeshHit hit, SPAWN_NAVMESH_RADIUS, NavMesh.AllAreas))
                {
                    spawnPos = hit.position;
                    break;
                }
                elapsed += Time.deltaTime;
                yield return null;
            }

            // CORREÇÃO v8: verifica novamente após yield (pode ter desconectado durante espera)
            if (conn == null || !conn.isReady)
            {
                Debug.LogWarning("[RPGNetworkManager] Conexão perdida durante espera de NavMesh.");
                _spawnCoroutines.Remove(conn?.connectionId ?? -1);
                yield break;
            }

            var playerGO = Instantiate(playerPrefab, spawnPos, Quaternion.identity);
            NetworkServer.AddPlayerForConnection(conn, playerGO);

            var netPlayer = playerGO.GetComponent<NetworkPlayer>();
            if (netPlayer != null)
                netPlayer.ServerInitialize(charData, accountUsername);
            else
                Debug.LogError("[RPGNetworkManager] playerPrefab não tem NetworkPlayer!");

            _spawnCoroutines.Remove(conn.connectionId);

            Debug.Log($"[Server] Spawnado: {charData.CharacterName} ({charData.Race}) " +
                      $"| connId={conn.connectionId} | pos={spawnPos}");
        }

        // ── CORREÇÃO v8: limpeza de spawns expirados ───────────────────────

        [Server]
        private IEnumerator CleanExpiredPendingSpawns()
        {
            var wait = new WaitForSeconds(5f);
            while (true)
            {
                yield return wait;

                var toRemove = new List<int>();
                foreach (var kv in _pendingSpawns)
                {
                    if (Time.time > kv.Value.ExpiresAt)
                    {
                        toRemove.Add(kv.Key);
                        Debug.LogWarning($"[RPGNetworkManager] Spawn expirado removido: " +
                                         $"connId={kv.Key} char={kv.Value.CharData?.CharacterName}");
                    }
                }
                foreach (var id in toRemove)
                    _pendingSpawns.Remove(id);
            }
        }

        // ── Spawn point por raça ───────────────────────────────────────────

        public Vector3 GetSpawnPositionForRace(CharacterRace race, CharacterData charData)
        {
            var saved = new Vector3(charData.PosX, charData.PosY, charData.PosZ);
            if (saved.sqrMagnitude > 0.01f &&
                NavMesh.SamplePosition(saved, out NavMeshHit savedHit, 5f, NavMesh.AllAreas))
            {
                Debug.Log($"[RPGNetworkManager] {charData.CharacterName}: posição salva {savedHit.position}");
                return savedHit.position;
            }

            if (RaceSpawnPoints.TryGetValue(race, out Vector3 racePos))
            {
                Debug.Log($"[RPGNetworkManager] {charData.CharacterName} ({race}): spawn da raça em {racePos}");
                return racePos;
            }

            Debug.LogWarning($"[RPGNetworkManager] Raça {race} sem spawn point. Usando origem.");
            return Vector3.zero;
        }

        // ── Registro de prefabs ────────────────────────────────────────────

        private void RegisterSpawnablePrefabs()
        {
            foreach (var prefab in spawnablePrefabs)
            {
                if (prefab == null) continue; // CORREÇÃO v8: proteção contra null

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
    }
}