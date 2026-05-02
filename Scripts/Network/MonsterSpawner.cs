using UnityEngine;
using Mirror;

namespace RPG.Network
{
    /// <summary>
    /// MonsterSpawner unificado.
    /// - Se o servidor Mirror estiver ativo: spawna via NetworkServer.Spawn()
    /// - Se for modo offline (sem Mirror): spawna normalmente com Instantiate
    /// 
    /// IMPORTANTE: Use sempre o NetworkMonsterPrefab (que tem NetworkIdentity).
    /// O MonsterEntity offline NÃO deve ser usado junto com Mirror.
    /// </summary>
    public class MonsterSpawner : MonoBehaviour
    {
        [System.Serializable]
        public class SpawnGroup
        {
            public GameObject  monsterPrefab; // prefab com NetworkIdentity
            public Transform[] spawnPoints;
        }

        [SerializeField] private SpawnGroup[] spawnGroups;

        private void Start()
        {
            if (NetworkServer.active)
            {
                // Modo online: servidor spawna e replica para clientes
                Debug.Log("[MonsterSpawner] Servidor ativo — spawnando via NetworkServer.");
                SpawnOnline();
            }
            else if (!NetworkClient.active)
            {
                // Modo completamente offline (sem Mirror rodando)
                Debug.Log("[MonsterSpawner] Modo offline — spawnando localmente.");
                SpawnOffline();
            }
            else
            {
                // É cliente online — servidor já spawnará os monstros
                Debug.Log("[MonsterSpawner] Cliente conectado — aguardando monstros do servidor.");
            }
        }

        private void SpawnOnline()
        {
            foreach (var group in spawnGroups)
            {
                if (group.monsterPrefab == null) continue;
                // Verifica se o prefab tem NetworkIdentity
                if (group.monsterPrefab.GetComponent<NetworkIdentity>() == null)
                {
                    Debug.LogError($"[MonsterSpawner] Prefab '{group.monsterPrefab.name}' " +
                                   "não tem NetworkIdentity! Adicione ou use o NetworkMonsterPrefab.");
                    continue;
                }
                foreach (var sp in group.spawnPoints)
                {
                    var mob = Instantiate(group.monsterPrefab, sp.position, sp.rotation);
                    NetworkServer.Spawn(mob);
                    Debug.Log($"[MonsterSpawner] Spawnado (online): {mob.name} em {sp.position}");
                }
            }
        }

        private void SpawnOffline()
        {
            foreach (var group in spawnGroups)
            {
                if (group.monsterPrefab == null) continue;
                foreach (var sp in group.spawnPoints)
                {
                    var mob = Instantiate(group.monsterPrefab, sp.position, sp.rotation);
                    Debug.Log($"[MonsterSpawner] Spawnado (offline): {mob.name} em {sp.position}");
                }
            }
        }
    }
}
