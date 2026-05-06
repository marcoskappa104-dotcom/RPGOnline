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
    /// RPGNetworkManager v7
    ///
    /// NOVIDADES:
    ///   - Spawn points por raça definidos por código (Vector3), sem Transform no Inspector.
    ///     Para mudar, edite o dicionário RaceSpawnPoints abaixo.
    ///
    ///   - CORREÇÃO PRINCIPAL: servidor aguarda MsgClientSceneReady antes de spawnar.
    ///     Antes o servidor spawnava imediatamente após OnSelectCharacter, mas o cliente
    ///     ainda estava carregando a GameplayScene → NavMeshAgent falhava, objetos não
    ///     apareciam. Agora o cliente confirma quando a cena está pronta.
    ///
    ///   - ServerAuthManager NÃO envia mais MsgSelectCharacterResponse — isso é feito
    ///     aqui após colocar o spawn na fila. Leia os comentários em SpawnPlayerForConnection.
    /// </summary>
    public class RPGNetworkManager : NetworkManager
    {
        public static new RPGNetworkManager singleton =>
            NetworkManager.singleton as RPGNetworkManager;

        // ── Spawn points por raça ──────────────────────────────────────────
        // Edite as coordenadas X/Z conforme o seu mapa.
        // O Y é ajustado automaticamente pelo NavMesh.
        private static readonly Dictionary<CharacterRace, Vector3> RaceSpawnPoints = new()
        {
            { CharacterRace.Human,  new Vector3(   0f, 1f,   0f) },
            { CharacterRace.Elf,    new Vector3(  20f, 1f,  10f) },
            { CharacterRace.Dwarf,  new Vector3( -20f, 1f,  10f) },
            { CharacterRace.Orc,    new Vector3(   0f, 1f,  30f) },
            { CharacterRace.Undead, new Vector3( -20f, 1f, -10f) },
        };

        // Raio de busca no NavMesh ao redor do ponto da raça
        private const float SPAWN_NAVMESH_RADIUS = 15f;

        [Header("Spawnable Prefabs")]
        [Tooltip("Todos os prefabs de monstro (precisam ter NetworkIdentity)")]
        [SerializeField] private List<GameObject> spawnablePrefabs = new List<GameObject>();

        // Spawns pendentes: servidor guardou os dados mas ainda não spawnouF
        // porque o cliente não confirmou que a cena carregou.
        private readonly Dictionary<int, PendingSpawn> _pendingSpawns = new();

        private ServerAuthManager _authManager;

        private struct PendingSpawn
        {
            public NetworkConnectionToClient Conn;
            public CharacterData            CharData;
            public string                   AccountUsername;
        }

        // ── Lifecycle ──────────────────────────────────────────────────────

        public override void Awake()
        {
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

            // Registra handler que recebe confirmação de cena pronta vinda do cliente
            NetworkServer.RegisterHandler<MsgClientSceneReady>(OnClientSceneReady, false);

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
            _pendingSpawns.Remove(conn.connectionId);
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

        /// <summary>
        /// Chamado pelo ServerAuthManager quando o jogador selecionou um personagem.
        ///
        /// FLUXO CORRETO:
        ///   1. Servidor coloca os dados na fila _pendingSpawns.
        ///   2. Servidor envia MsgSelectCharacterResponse { Success=true } ao cliente.
        ///   3. Cliente recebe → carrega GameplayScene → ao terminar, envia MsgClientSceneReady.
        ///   4. Servidor recebe MsgClientSceneReady → spawna o player.
        ///
        /// IMPORTANTE: o ServerAuthManager não deve mais enviar MsgSelectCharacterResponse.
        ///             Essa mensagem agora é enviada aqui.
        /// </summary>
        [Server]
        public void SpawnPlayerForConnection(
            NetworkConnectionToClient conn,
            CharacterData charData,
            string accountUsername)
        {
            if (playerPrefab == null)
            {
                Debug.LogError("[RPGNetworkManager] playerPrefab não configurado no Inspector!");
                conn.Send(new MsgSelectCharacterResponse { Success = false, Error = "Erro interno do servidor." });
                return;
            }

            _pendingSpawns[conn.connectionId] = new PendingSpawn
            {
                Conn            = conn,
                CharData        = charData,
                AccountUsername = accountUsername
            };

            // Avisa o cliente para carregar a GameplayScene.
            // O cliente só confirma quando a cena estiver completamente carregada.
            conn.Send(new MsgSelectCharacterResponse { Success = true });

            Debug.Log($"[RPGNetworkManager] {charData.CharacterName} ({charData.Race}) " +
                      "na fila. Aguardando cliente confirmar GameplayScene pronta.");
        }

        /// <summary>
        /// Recebido do cliente quando a GameplayScene terminou de carregar.
        /// Agora é seguro spawnar o player.
        /// </summary>
        [Server]
        private void OnClientSceneReady(NetworkConnectionToClient conn, MsgClientSceneReady msg)
        {
            if (!_pendingSpawns.TryGetValue(conn.connectionId, out var pending))
            {
                Debug.LogWarning($"[RPGNetworkManager] MsgClientSceneReady recebido de " +
                                 $"conn:{conn.connectionId} sem spawn pendente. Ignorando.");
                return;
            }

            _pendingSpawns.Remove(conn.connectionId);
            Debug.Log($"[RPGNetworkManager] Cliente {conn.connectionId} confirmou cena pronta. Spawnando {pending.CharData.CharacterName}...");

            StartCoroutine(DoSpawnPlayer(conn, pending.CharData, pending.AccountUsername));
        }

        [Server]
        private IEnumerator DoSpawnPlayer(
            NetworkConnectionToClient conn,
            CharacterData charData,
            string accountUsername)
        {
            Vector3 spawnPos = GetSpawnPositionForRace(charData.Race, charData);

            // Aguarda NavMesh confirmar a posição (até 5s)
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

            if (conn == null || !conn.isReady)
            {
                Debug.LogWarning("[RPGNetworkManager] Conexão perdida antes do spawn.");
                yield break;
            }

            var playerGO = Instantiate(playerPrefab, spawnPos, Quaternion.identity);
            NetworkServer.AddPlayerForConnection(conn, playerGO);

            var netPlayer = playerGO.GetComponent<NetworkPlayer>();
            if (netPlayer != null)
                netPlayer.ServerInitialize(charData, accountUsername);
            else
                Debug.LogError("[RPGNetworkManager] playerPrefab não tem componente NetworkPlayer!");

            Debug.Log($"[Server] Player spawnado: {charData.CharacterName} ({charData.Race}) " +
                      $"| connId={conn.connectionId} | pos={spawnPos}");
        }

        // ── Lógica de spawn point por raça ─────────────────────────────────

        public  Vector3 GetSpawnPositionForRace(CharacterRace race, CharacterData charData)
        {
            // 1. Posição salva do personagem
            var saved = new Vector3(charData.PosX, charData.PosY, charData.PosZ);
            if (saved.sqrMagnitude > 0.01f &&
                NavMesh.SamplePosition(saved, out NavMeshHit savedHit, 5f, NavMesh.AllAreas))
            {
                Debug.Log($"[RPGNetworkManager] {charData.CharacterName}: posição salva {savedHit.position}");
                return savedHit.position;
            }

            // 2. Spawn point da raça
            if (RaceSpawnPoints.TryGetValue(race, out Vector3 racePos))
            {
                Debug.Log($"[RPGNetworkManager] {charData.CharacterName} ({race}): spawn da raça em {racePos}");
                return racePos;
            }

            // 3. Fallback
            Debug.LogWarning($"[RPGNetworkManager] Raça {race} sem spawn point. Usando origem.");
            return Vector3.zero;
        }

        // ── Registro de prefabs ────────────────────────────────────────────

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
    }
}