using UnityEngine;
using UnityEngine.AI;
using Mirror;
using System.Collections.Generic;

namespace RPG.Network
{
    /// <summary>
    /// NetworkMonsterSpawner — sistema de spawn por zona ou por pontos fixos.
    ///
    /// MODOS:
    ///   ZONA  (useFixedPoints = false): spawna N monstros em posições aleatórias
    ///         dentro de zoneRadius ao redor de zoneCenter.
    ///   FIXO  (useFixedPoints = true):  spawna 1 monstro em cada fixedSpawnPoint.
    ///
    /// CADA monstro recebe SetSpawnData(homePosition, patrolRadius) para
    /// configurar sua área de patrulha individualmente.
    /// </summary>
    public class NetworkMonsterSpawner : MonoBehaviour
    {
        [System.Serializable]
        public class SpawnGroup
        {
            [Header("Prefab")]
            [Tooltip("Deve ter NetworkIdentity + NetworkMonsterEntity.")]
            public GameObject monsterPrefab;

            [Header("─── MODO ZONA ───")]
            public bool      useFixedPoints = false;
            public Transform zoneCenter;
            public float     zoneRadius     = 15f;
            public int       spawnCount     = 3;

            [Header("─── MODO PONTOS FIXOS ───")]
            public Transform[] fixedSpawnPoints;

            [Header("─── PATRULHA ───")]
            [Tooltip("Raio de patrulha por mob. 0 = sentinela (parado).")]
            public float patrolRadius = 12f;

            [Tooltip("Rótulo usado nos logs e Gizmos.")]
            public string groupLabel = "Grupo";
        }

        [SerializeField] private SpawnGroup[] spawnGroups;
        [SerializeField] private bool         logSpawns = true;

        private const int   NAVMESH_ATTEMPTS      = 20;
        private const float NAVMESH_SAMPLE_RADIUS = 3f;

        private void Start()
        {
            if (!NetworkServer.active) return;
            if (spawnGroups == null || spawnGroups.Length == 0)
            {
                Debug.LogWarning("[NetworkMonsterSpawner] Nenhum SpawnGroup configurado.");
                return;
            }
            SpawnAll();
        }

        private void SpawnAll()
        {
            int totalSpawned = 0;

            foreach (var group in spawnGroups)
            {
                if (group == null) continue;
                if (!ValidateGroup(group)) continue;

                totalSpawned += group.useFixedPoints
                    ? SpawnAtFixedPoints(group)
                    : SpawnInZone(group);
            }

            Debug.Log($"[NetworkMonsterSpawner] Total spawnado: {totalSpawned} monstros.");
        }

        // ── Spawn por pontos fixos ─────────────────────────────────────────

        private int SpawnAtFixedPoints(SpawnGroup group)
        {
            if (group.fixedSpawnPoints == null) return 0;

            int count = 0;
            foreach (var point in group.fixedSpawnPoints)
            {
                if (point == null) continue;
                SpawnMonster(group, SnapToNavMesh(point.position));
                count++;
            }
            return count;
        }

        // ── Spawn por zona ─────────────────────────────────────────────────

        private int SpawnInZone(SpawnGroup group)
        {
            if (group.zoneCenter == null)
            {
                Debug.LogWarning($"[NetworkMonsterSpawner] Grupo '{group.groupLabel}': zoneCenter não configurado!");
                return 0;
            }

            int count         = 0;
            var usedPositions = new List<Vector3>();

            for (int i = 0; i < group.spawnCount; i++)
            {
                Vector3? pos = FindSpawnPositionInZone(
                    group.zoneCenter.position, group.zoneRadius, usedPositions);

                if (pos == null)
                {
                    Debug.LogWarning($"[NetworkMonsterSpawner] Grupo '{group.groupLabel}': " +
                                     $"posição não encontrada para mob {i + 1}/{group.spawnCount}.");
                    continue;
                }

                usedPositions.Add(pos.Value);
                SpawnMonster(group, pos.Value);
                count++;
            }

            return count;
        }

        // ── Spawn individual ───────────────────────────────────────────────

        private void SpawnMonster(SpawnGroup group, Vector3 position)
        {
            var mob = Instantiate(group.monsterPrefab, position, Quaternion.identity);
            NetworkServer.Spawn(mob);

            var entity = mob.GetComponent<NetworkMonsterEntity>();
            entity?.SetSpawnData(position, group.patrolRadius);

            if (logSpawns)
                Debug.Log($"[NetworkMonsterSpawner] [{group.groupLabel}] " +
                          $"{mob.name} em {position} | PatrolR:{group.patrolRadius}");
        }

        // ── NavMesh Helpers ────────────────────────────────────────────────

        private Vector3? FindSpawnPositionInZone(
            Vector3 center, float radius, List<Vector3> usedPositions)
        {
            const float MIN_DIST_BETWEEN_MOBS = 2f;

            for (int attempt = 0; attempt < NAVMESH_ATTEMPTS; attempt++)
            {
                Vector2 rand2D    = Random.insideUnitCircle * radius;
                Vector3 candidate = center + new Vector3(rand2D.x, 0f, rand2D.y);

                // Ajuste de altura pelo terreno
                if (Physics.Raycast(candidate + Vector3.up * 20f, Vector3.down,
                                    out RaycastHit hit, 40f))
                    candidate = hit.point;

                if (!NavMesh.SamplePosition(candidate, out NavMeshHit navHit,
                                            NAVMESH_SAMPLE_RADIUS, NavMesh.AllAreas))
                    continue;

                Vector3 pos = navHit.position;

                bool tooClose = false;
                foreach (var used in usedPositions)
                {
                    if (Vector3.Distance(pos, used) < MIN_DIST_BETWEEN_MOBS)
                    { tooClose = true; break; }
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

        // ── Validação ──────────────────────────────────────────────────────

        private bool ValidateGroup(SpawnGroup group)
        {
            if (group.monsterPrefab == null)
            {
                Debug.LogWarning($"[NetworkMonsterSpawner] Grupo '{group.groupLabel}': prefab null.");
                return false;
            }
            if (group.monsterPrefab.GetComponent<NetworkIdentity>() == null)
            {
                Debug.LogError($"[NetworkMonsterSpawner] '{group.monsterPrefab.name}' não tem NetworkIdentity!");
                return false;
            }
            if (group.monsterPrefab.GetComponent<NetworkMonsterEntity>() == null)
            {
                Debug.LogError($"[NetworkMonsterSpawner] '{group.monsterPrefab.name}' não tem NetworkMonsterEntity!");
                return false;
            }
            return true;
        }

        // ── Gizmos ─────────────────────────────────────────────────────────

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            if (spawnGroups == null) return;

            foreach (var group in spawnGroups)
            {
                if (group == null) continue;

                if (!group.useFixedPoints && group.zoneCenter != null)
                {
                    UnityEditor.Handles.color = new Color(0.2f, 0.5f, 1f, 0.15f);
                    UnityEditor.Handles.DrawSolidDisc(
                        group.zoneCenter.position, Vector3.up, group.zoneRadius);

                    UnityEditor.Handles.color = new Color(0.2f, 0.5f, 1f, 0.8f);
                    UnityEditor.Handles.DrawWireDisc(
                        group.zoneCenter.position, Vector3.up, group.zoneRadius);

                    UnityEditor.Handles.color = new Color(1f, 0.85f, 0f, 0.08f);
                    UnityEditor.Handles.DrawSolidDisc(
                        group.zoneCenter.position, Vector3.up, group.patrolRadius);

                    UnityEditor.Handles.color = new Color(1f, 0.85f, 0f, 0.6f);
                    UnityEditor.Handles.DrawWireDisc(
                        group.zoneCenter.position, Vector3.up, group.patrolRadius);

                    UnityEditor.Handles.Label(
                        group.zoneCenter.position + Vector3.up * 0.5f,
                        $"{group.groupLabel} ×{group.spawnCount}");
                }
                else if (group.useFixedPoints && group.fixedSpawnPoints != null)
                {
                    foreach (var pt in group.fixedSpawnPoints)
                    {
                        if (pt == null) continue;
                        Gizmos.color = new Color(1f, 0.3f, 0.3f, 0.8f);
                        Gizmos.DrawSphere(pt.position, 0.4f);

                        if (group.patrolRadius > 0f)
                        {
                            UnityEditor.Handles.color = new Color(1f, 0.85f, 0f, 0.5f);
                            UnityEditor.Handles.DrawWireDisc(
                                pt.position, Vector3.up, group.patrolRadius);
                        }

                        UnityEditor.Handles.Label(pt.position + Vector3.up * 0.6f, group.groupLabel);
                    }
                }
            }
        }
#endif
    }
}