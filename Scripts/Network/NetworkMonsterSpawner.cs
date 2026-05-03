using UnityEngine;
using UnityEngine.AI;
using Mirror;
using System.Collections.Generic;

namespace RPG.Network
{
    /// <summary>
    /// NetworkMonsterSpawner v2 — Sistema de spawn por área e por ponto fixo.
    ///
    /// DOIS MODOS de spawn por grupo:
    ///
    ///   1. ZONA (useFixedPoints = false)
    ///      Spawna N monstros em posições aleatórias dentro de um raio ao redor
    ///      de zoneCenter. Ideal para campos de mobs comuns.
    ///      Os monstros patrulham dentro de patrolRadius (normalmente = zoneRadius).
    ///
    ///   2. PONTOS FIXOS (useFixedPoints = true)
    ///      Spawna um monstro em cada ponto da lista fixedSpawnPoints.
    ///      Ideal para bosses, mobs nomeados, mobs em posições específicas.
    ///      Cada mob patrulha dentro de patrolRadius ao redor do seu ponto de spawn.
    ///
    /// CONFIGURAÇÃO NO UNITY:
    ///   1. Adicione este script a um GameObject vazio chamado "MonsterSpawner"
    ///   2. Para zonas:
    ///      - Crie um Empty chamado "ZoneCenter_Floresta" e posicione no mapa
    ///      - Configure zoneRadius (ex: 15), spawnCount (ex: 5), patrolRadius (ex: 12)
    ///   3. Para pontos fixos:
    ///      - Marque useFixedPoints = true
    ///      - Crie Empties "SpawnPoint_1", "SpawnPoint_2" etc. e arraste aqui
    ///
    /// GIZMOS:
    ///   Selecione o spawner no Editor para visualizar as áreas de spawn (azul)
    ///   e patrulha (amarelo) no Scene View.
    /// </summary>
    public class NetworkMonsterSpawner : MonoBehaviour
    {
        [System.Serializable]
        public class SpawnGroup
        {
            [Header("Prefab (deve ter NetworkIdentity + NetworkMonsterEntity)")]
            public GameObject monsterPrefab;

            [Header("─── MODO ZONA (spawn aleatório na área) ───")]
            [Tooltip("Marque TRUE para usar pontos fixos em vez de área.")]
            public bool useFixedPoints = false;

            [Tooltip("Centro da área de spawn. Crie um Empty e arraste aqui.")]
            public Transform zoneCenter;

            [Tooltip("Raio da área em que os monstros são criados.")]
            public float zoneRadius = 15f;

            [Tooltip("Quantos monstros spawnar nesta zona.")]
            public int spawnCount = 3;

            [Header("─── MODO PONTOS FIXOS (bosses / mobs específicos) ───")]
            [Tooltip("Spawna 1 mob por ponto. Só usado se useFixedPoints = true.")]
            public Transform[] fixedSpawnPoints;

            [Header("─── PATRULHA ───")]
            [Tooltip("Raio de patrulha ao redor do ponto de spawn de cada mob.\n" +
                     "0 = mob fica parado (tipo sentinela).")]
            public float patrolRadius = 12f;

            [Tooltip("Rótulo para identificação nos logs e Gizmos.")]
            public string groupLabel = "Grupo";
        }

        [SerializeField] private SpawnGroup[] spawnGroups;
        [SerializeField] private bool         logSpawns = true;

        // ── Tentativas máximas para achar posição válida no NavMesh ──────
        private const int NAVMESH_ATTEMPTS = 20;
        private const float NAVMESH_SAMPLE_RADIUS = 3f;

        private void Start()
        {
            if (!NetworkServer.active) return;
            SpawnAll();
        }

        private void SpawnAll()
        {
            int totalSpawned = 0;

            foreach (var group in spawnGroups)
            {
                if (!ValidateGroup(group)) continue;

                if (group.useFixedPoints)
                    totalSpawned += SpawnAtFixedPoints(group);
                else
                    totalSpawned += SpawnInZone(group);
            }

            Debug.Log($"[NetworkMonsterSpawner] Total spawnado: {totalSpawned} monstros.");
        }

        // ── Spawn por pontos fixos ────────────────────────────────────────

        private int SpawnAtFixedPoints(SpawnGroup group)
        {
            int count = 0;
            foreach (var point in group.fixedSpawnPoints)
            {
                if (point == null) continue;

                // Para pontos fixos tentamos achar posição no NavMesh próxima ao ponto
                Vector3 spawnPos = SnapToNavMesh(point.position);
                SpawnMonster(group, spawnPos);
                count++;
            }
            return count;
        }

        // ── Spawn por zona ────────────────────────────────────────────────

        private int SpawnInZone(SpawnGroup group)
        {
            if (group.zoneCenter == null)
            {
                Debug.LogWarning($"[NetworkMonsterSpawner] Grupo '{group.groupLabel}': " +
                                 "zoneCenter não configurado!");
                return 0;
            }

            int count = 0;
            var usedPositions = new List<Vector3>();

            for (int i = 0; i < group.spawnCount; i++)
            {
                Vector3? pos = FindSpawnPositionInZone(group.zoneCenter.position,
                                                        group.zoneRadius, usedPositions);
                if (pos == null)
                {
                    Debug.LogWarning($"[NetworkMonsterSpawner] Grupo '{group.groupLabel}': " +
                                     $"não foi possível achar posição para mob {i + 1}/{group.spawnCount}.");
                    continue;
                }

                usedPositions.Add(pos.Value);
                SpawnMonster(group, pos.Value);
                count++;
            }

            return count;
        }

        // ── Spawn individual ──────────────────────────────────────────────

        private void SpawnMonster(SpawnGroup group, Vector3 position)
        {
            var mob = Instantiate(group.monsterPrefab, position, Quaternion.identity);
            NetworkServer.Spawn(mob);

            // Passa os dados de área para o mob configurar sua patrulha
            var entity = mob.GetComponent<NetworkMonsterEntity>();
            if (entity != null)
                entity.SetSpawnData(position, group.patrolRadius);

            if (logSpawns)
                Debug.Log($"[NetworkMonsterSpawner] [{group.groupLabel}] " +
                          $"{mob.name} em {position} | PatrolR:{group.patrolRadius}");
        }

        // ── Helpers de NavMesh ────────────────────────────────────────────

        /// <summary>
        /// Encontra uma posição válida no NavMesh dentro do raio da zona,
        /// tentando evitar empilhamento com posições já usadas (minDist = 2m).
        /// </summary>
        private Vector3? FindSpawnPositionInZone(Vector3 center, float radius,
                                                  List<Vector3> usedPositions)
        {
            const float MIN_DIST_BETWEEN_MOBS = 2f;

            for (int attempt = 0; attempt < NAVMESH_ATTEMPTS; attempt++)
            {
                // Ponto aleatório dentro do círculo (distribuição uniforme)
                Vector2 rand2D = Random.insideUnitCircle * radius;
                Vector3 candidate = center + new Vector3(rand2D.x, 0f, rand2D.y);

                // Ajusta Y para o terreno
                if (Physics.Raycast(candidate + Vector3.up * 20f, Vector3.down, out RaycastHit hit, 40f))
                    candidate = hit.point;

                if (!NavMesh.SamplePosition(candidate, out NavMeshHit navHit,
                                            NAVMESH_SAMPLE_RADIUS, NavMesh.AllAreas))
                    continue;

                Vector3 pos = navHit.position;

                // Verifica se não está muito perto de outro mob já spawnado
                bool tooClose = false;
                foreach (var used in usedPositions)
                {
                    if (Vector3.Distance(pos, used) < MIN_DIST_BETWEEN_MOBS)
                    {
                        tooClose = true;
                        break;
                    }
                }

                if (!tooClose) return pos;
            }

            return null;
        }

        private Vector3 SnapToNavMesh(Vector3 position)
        {
            if (NavMesh.SamplePosition(position, out NavMeshHit hit,
                                       NAVMESH_SAMPLE_RADIUS, NavMesh.AllAreas))
                return hit.position;
            return position;
        }

        // ── Validação ─────────────────────────────────────────────────────

        private bool ValidateGroup(SpawnGroup group)
        {
            if (group.monsterPrefab == null)
            {
                Debug.LogWarning($"[NetworkMonsterSpawner] Grupo '{group.groupLabel}': prefab null.");
                return false;
            }
            if (group.monsterPrefab.GetComponent<NetworkIdentity>() == null)
            {
                Debug.LogError($"[NetworkMonsterSpawner] '{group.monsterPrefab.name}' " +
                               "não tem NetworkIdentity!");
                return false;
            }
            if (group.monsterPrefab.GetComponent<NetworkMonsterEntity>() == null)
            {
                Debug.LogError($"[NetworkMonsterSpawner] '{group.monsterPrefab.name}' " +
                               "não tem NetworkMonsterEntity!");
                return false;
            }
            return true;
        }

        // ── Gizmos ────────────────────────────────────────────────────────

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            if (spawnGroups == null) return;

            foreach (var group in spawnGroups)
            {
                if (group == null) continue;

                if (!group.useFixedPoints && group.zoneCenter != null)
                {
                    // Área de spawn — azul
                    UnityEditor.Handles.color = new Color(0.2f, 0.5f, 1f, 0.15f);
                    UnityEditor.Handles.DrawSolidDisc(
                        group.zoneCenter.position, Vector3.up, group.zoneRadius);

                    UnityEditor.Handles.color = new Color(0.2f, 0.5f, 1f, 0.8f);
                    UnityEditor.Handles.DrawWireDisc(
                        group.zoneCenter.position, Vector3.up, group.zoneRadius);

                    // Área de patrulha — amarelo (ligeiramente menor que spawn)
                    UnityEditor.Handles.color = new Color(1f, 0.85f, 0f, 0.08f);
                    UnityEditor.Handles.DrawSolidDisc(
                        group.zoneCenter.position, Vector3.up, group.patrolRadius);

                    UnityEditor.Handles.color = new Color(1f, 0.85f, 0f, 0.6f);
                    UnityEditor.Handles.DrawWireDisc(
                        group.zoneCenter.position, Vector3.up, group.patrolRadius);

                    // Label
                    Gizmos.color = Color.white;
                    UnityEditor.Handles.Label(
                        group.zoneCenter.position + Vector3.up * 0.5f,
                        $"{group.groupLabel}\n×{group.spawnCount}");
                }
                else if (group.useFixedPoints && group.fixedSpawnPoints != null)
                {
                    foreach (var pt in group.fixedSpawnPoints)
                    {
                        if (pt == null) continue;

                        // Ponto fixo — vermelho
                        Gizmos.color = new Color(1f, 0.3f, 0.3f, 0.8f);
                        Gizmos.DrawSphere(pt.position, 0.4f);

                        // Raio de patrulha ao redor do ponto fixo — amarelo
                        if (group.patrolRadius > 0f)
                        {
                            UnityEditor.Handles.color = new Color(1f, 0.85f, 0f, 0.5f);
                            UnityEditor.Handles.DrawWireDisc(
                                pt.position, Vector3.up, group.patrolRadius);
                        }

                        UnityEditor.Handles.Label(
                            pt.position + Vector3.up * 0.6f, group.groupLabel);
                    }
                }
            }
        }
#endif
    }
}