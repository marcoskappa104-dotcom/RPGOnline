using UnityEngine;
using Mirror;

namespace RPG.Network
{
    /// <summary>
    /// NetworkMonsterSpawner — spawna monstros na rede (server-side only).
    ///
    /// CORREÇÃO: removido o MonsterSpawner offline-only para evitar ambiguidade.
    /// Use APENAS este script em cenas com Mirror ativo.
    /// Para testes offline sem Mirror, veja MonsterSpawner (modo offline).
    ///
    /// CONFIGURAÇÃO:
    ///   1. Adicione este script a um GameObject vazio na GameplayScene
    ///   2. Configure os Spawn Groups (prefab + pontos de spawn)
    ///   3. Certifique-se que os prefabs têm NetworkIdentity
    ///   4. Registre os prefabs em RPGNetworkManager → Registered Spawnable Prefabs
    /// </summary>
    public class NetworkMonsterSpawner : NetworkBehaviour
    {
        [System.Serializable]
        public class SpawnGroup
        {
            [Tooltip("Prefab com NetworkIdentity + NetworkMonsterEntity")]
            public GameObject  monsterPrefab;
            [Tooltip("Transforms que indicam onde spawnar")]
            public Transform[] spawnPoints;
        }

        [SerializeField] private SpawnGroup[] spawnGroups;
        [SerializeField] private bool logSpawns = true;

        public override void OnStartServer()
        {
            int total = 0;
            foreach (var group in spawnGroups)
            {
                if (group.monsterPrefab == null)
                {
                    Debug.LogWarning("[NetworkMonsterSpawner] SpawnGroup com prefab null — ignorado.");
                    continue;
                }

                if (group.monsterPrefab.GetComponent<NetworkIdentity>() == null)
                {
                    Debug.LogError($"[NetworkMonsterSpawner] Prefab '{group.monsterPrefab.name}' " +
                                   "não tem NetworkIdentity! Adicione o componente.");
                    continue;
                }

                if (group.spawnPoints == null || group.spawnPoints.Length == 0)
                {
                    Debug.LogWarning($"[NetworkMonsterSpawner] Prefab '{group.monsterPrefab.name}' " +
                                     "sem spawn points configurados.");
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

            Debug.Log($"[NetworkMonsterSpawner] Total spawnado: {total} monstros.");
        }
    }
}
