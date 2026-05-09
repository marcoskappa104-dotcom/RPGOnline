using UnityEngine;
using UnityEngine.AI;
using Mirror;
using System.Collections;
using System.Collections.Generic;

namespace RPG.Network
{
    /// <summary>
    /// NetworkMonsterSpawner v2 — Corrige monstros não aparecendo.
    ///
    /// PROBLEMAS CORRIGIDOS:
    ///   1. Spawn ocorria antes do NavMesh estar pronto → "Failed to create agent
    ///      because there is no valid NavMesh". Agora aguarda NavMesh via coroutine.
    ///   2. "Did not find target for sync message" — os monstros spawnavam e o
    ///      cliente recebia EntityStateMessages antes do SpawnMessage chegar
    ///      (race condition UDP). Corrigido com pequeno delay pós-spawn.
    ///   3. Adicionado log de diagnóstico para indicar claramente quando NavMesh
    ///      não está baked na cena.
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

        [Header("Configuração de Timing")]
        [Tooltip("Segundos de espera para garantir que o NavMesh esteja pronto. " +
                 "Aumente se ainda aparecer 'Failed to create agent'.")]
        [SerializeField] private float navMeshWaitTimeout = 8f;

        [Tooltip("Delay entre spawns individuais (evita flood de mensagens de rede)")]
        [SerializeField] private float spawnDelay = 0.05f;

        private const int   NAVMESH_ATTEMPTS      = 20;
        private const float NAVMESH_SAMPLE_RADIUS = 3f;

        private void Start()
        {
            // Só executa no servidor
            if (!NetworkServer.active) return;

            if (spawnGroups == null || spawnGroups.Length == 0)
            {
                Debug.LogWarning("[NetworkMonsterSpawner] Nenhum SpawnGroup configurado.");
                return;
            }

            StartCoroutine(SpawnWhenNavMeshReady());
        }

        /// <summary>
        /// Aguarda o NavMesh estar disponível antes de iniciar o spawn.
        /// Resolve o erro "Failed to create agent because there is no valid NavMesh".
        /// </summary>
        private IEnumerator SpawnWhenNavMeshReady()
        {
            Debug.Log("[NetworkMonsterSpawner] Aguardando NavMesh...");

            float elapsed = 0f;
            bool navMeshReady = false;

            // Tenta encontrar qualquer ponto válido no NavMesh
            while (elapsed < navMeshWaitTimeout)
            {
                // Testa vários pontos para garantir que o NavMesh está carregado
                if (NavMesh.SamplePosition(Vector3.zero, out _, 200f, NavMesh.AllAreas) ||
                    IsNavMeshAvailableNearGroups())
                {
                    navMeshReady = true;
                    break;
                }

                elapsed += 0.2f;
                yield return new WaitForSeconds(0.2f);
            }

            if (!navMeshReady)
            {
                Debug.LogError("[NetworkMonsterSpawner] ERRO CRÍTICO: NavMesh não encontrado após " +
                               $"{navMeshWaitTimeout}s! Verifique:\n" +
                               "1. A GameplayScene tem NavMesh baked? (Window → AI → Navigation → Bake)\n" +
                               "2. O Terrain/Chão tem o layer correto para o NavMesh?\n" +
                               "3. Os monstros estão sendo spawnados na GameplayScene correta?");
                yield break;
            }

            Debug.Log($"[NetworkMonsterSpawner] NavMesh pronto após {elapsed:0.0}s. Iniciando spawn...");

            yield return SpawnAllWithDelay();
        }

        private bool IsNavMeshAvailableNearGroups()
        {
            foreach (var group in spawnGroups)
            {
                if (group == null) continue;

                Vector3 testPos = Vector3.zero;
                if (!group.useFixedPoints && group.zoneCenter != null)
                    testPos = group.zoneCenter.position;
                else if (group.useFixedPoints &&
                         group.fixedSpawnPoints != null &&
                         group.fixedSpawnPoints.Length > 0 &&
                         group.fixedSpawnPoints[0] != null)
                    testPos = group.fixedSpawnPoints[0].position;

                if (NavMesh.SamplePosition(testPos, out _, 20f, NavMesh.AllAreas))
                    return true;
            }
            return false;
        }

        private IEnumerator SpawnAllWithDelay()
        {
            int totalSpawned = 0;

            foreach (var group in spawnGroups)
            {
                if (group == null) continue;
                if (!ValidateGroup(group)) continue;

                if (group.useFixedPoints)
                {
                    if (group.fixedSpawnPoints == null) continue;
                    foreach (var point in group.fixedSpawnPoints)
                    {
                        if (point == null) continue;
                        SpawnMonster(group, SnapToNavMesh(point.position));
                        totalSpawned++;
                        if (spawnDelay > 0f)
                            yield return new WaitForSeconds(spawnDelay);
                    }
                }
                else
                {
                    if (group.zoneCenter == null)
                    {
                        Debug.LogWarning($"[NetworkMonsterSpawner] Grupo '{group.groupLabel}': " +
                                         "zoneCenter não configurado!");
                        continue;
                    }

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
                        totalSpawned++;

                        if (spawnDelay > 0f)
                            yield return new WaitForSeconds(spawnDelay);
                    }
                }
            }

            Debug.Log($"[NetworkMonsterSpawner] Total spawnado: {totalSpawned} monstros.");
        }

        // ── Spawn individual ───────────────────────────────────────────────

        private void SpawnMonster(SpawnGroup group, Vector3 position)
        {
            var mob = Instantiate(group.monsterPrefab, position, Quaternion.identity);

            // Configura o SpawnData ANTES de chamar NetworkServer.Spawn
            // para que OnStartServer() do monstro já tenha a homePosition correta
            var entity = mob.GetComponent<NetworkMonsterEntity>();
            entity?.SetSpawnData(position, group.patrolRadius);

            NetworkServer.Spawn(mob);

            if (logSpawns)
                Debug.Log($"[NetworkMonsterSpawner] [{group.groupLabel}] " +
                          $"{mob.name} em ({position.x:0.00}, {position.y:0.00}, {position.z:0.00}) " +
                          $"| PatrolR:{group.patrolRadius}");
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

            Debug.LogWarning($"[NetworkMonsterSpawner] Ponto fixo {position} não está no NavMesh. " +
                             "Usando posição original.");
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
                Debug.LogError($"[NetworkMonsterSpawner] '{group.monsterPrefab.name}' não tem NetworkIdentity! " +
                               "O prefab de monstro precisa de NetworkIdentity para funcionar com Mirror.");
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
