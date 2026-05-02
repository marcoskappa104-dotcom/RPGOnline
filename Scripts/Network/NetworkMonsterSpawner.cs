using UnityEngine;
using Mirror;

namespace RPG.Network
{
    public class NetworkMonsterSpawner : MonoBehaviour  // ← era NetworkBehaviour
    {
        [System.Serializable]
        public class SpawnGroup
        {
            public GameObject  monsterPrefab;
            public Transform[] spawnPoints;
        }

        [SerializeField] private SpawnGroup[] spawnGroups;
        [SerializeField] private bool logSpawns = true;

        private void Start()
        {
            // Só o servidor spawna monstros
            if (!NetworkServer.active) return;
            SpawnAll();
        }

        private void SpawnAll()
        {
            int total = 0;
            foreach (var group in spawnGroups)
            {
                if (group.monsterPrefab == null)
                {
                    Debug.LogWarning("[NetworkMonsterSpawner] Prefab null — ignorado.");
                    continue;
                }

                if (group.monsterPrefab.GetComponent<NetworkIdentity>() == null)
                {
                    Debug.LogError($"[NetworkMonsterSpawner] '{group.monsterPrefab.name}' " +
                                   "não tem NetworkIdentity! Use o NetworkMonsterEntity prefab.");
                    continue;
                }

                if (group.spawnPoints == null || group.spawnPoints.Length == 0)
                {
                    Debug.LogWarning($"[NetworkMonsterSpawner] '{group.monsterPrefab.name}' sem spawn points.");
                    continue;
                }

                foreach (var sp in group.spawnPoints)
                {
                    if (sp == null) continue;
                    var mob = Instantiate(group.monsterPrefab, sp.position, sp.rotation);
                    NetworkServer.Spawn(mob);
                    total++;

                    if (logSpawns)
                        Debug.Log($"[NetworkMonsterSpawner] Spawnado: {mob.name} em {sp.position}");
                }
            }

            Debug.Log($"[NetworkMonsterSpawner] Total: {total} monstros spawnados.");
        }
    }
}
