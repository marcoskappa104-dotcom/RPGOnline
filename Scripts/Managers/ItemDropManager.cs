using UnityEngine;
using Mirror;
using RPG.Data;
using System.Collections.Generic;

namespace RPG.Managers
{
    /// <summary>
    /// ItemDropManager v2
    ///
    /// CORREÇÕES v2:
    ///
    ///   BUG-07 — Memory leak: GameObject instanciado antes de checar ItemDatabase:
    ///     O código original chamava Instantiate() antes de verificar se o item
    ///     existia no banco. Se ItemDatabase fosse null ou o item não existisse,
    ///     o GameObject ficava na cena sem ser destruído → memory leak no servidor.
    ///     SOLUÇÃO: toda validação é feita ANTES de qualquer Instantiate().
    ///
    ///   Todas as correções v1 mantidas (NetworkServer.active check, scatter).
    /// </summary>
    public class ItemDropManager : MonoBehaviour
    {
        public static ItemDropManager Instance { get; private set; }

        [Header("Prefab do Item no Mundo")]
        [Tooltip("Deve ter NetworkIdentity + WorldItem.")]
        [SerializeField] private GameObject worldItemPrefab;

        [Header("Tabela de Drop Global (fallback)")]
        [Tooltip("Itens que qualquer monstro pode dropar se não tiver tabela própria.")]
        [SerializeField] private List<ItemData> globalDropTable = new List<ItemData>();

        [Header("Configuração")]
        [SerializeField] private float spawnHeightOffset = 0.3f;
        [SerializeField] private float dropScatterRadius = 0.8f;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        /// <summary>
        /// Sorteia e spawna um drop para o monstro morto.
        ///
        /// dropChance: 0-100. Probabilidade de dropar alguma coisa.
        /// customDropTable: tabela específica do monstro. Se null, usa a global.
        /// guaranteedDrops: itens garantidos (ex: quest drops).
        /// </summary>
        [Server]
        public void ServerSpawnDrop(
            Vector3          position,
            float            dropChance      = 50f,
            List<ItemData>   customDropTable = null,
            List<string>     guaranteedDrops = null)
        {
            if (!NetworkServer.active) return;

            if (worldItemPrefab == null)
            {
                Debug.LogWarning("[ItemDropManager] worldItemPrefab não configurado!");
                return;
            }

            // Drops garantidos (independente de chance)
            if (guaranteedDrops != null)
            {
                for (int i = 0; i < guaranteedDrops.Count; i++)
                {
                    Vector3 pos = ScatterPosition(position, i);
                    SpawnWorldItem(pos, guaranteedDrops[i]);
                }
            }

            // Drop aleatório baseado em chance
            if (Random.Range(0f, 100f) > dropChance) return;

            var table = (customDropTable != null && customDropTable.Count > 0)
                ? customDropTable
                : globalDropTable;

            string droppedId = ItemDatabase.RollDrop(table);
            if (!string.IsNullOrEmpty(droppedId))
            {
                Vector3 pos = ScatterPosition(position, guaranteedDrops?.Count ?? 0);
                SpawnWorldItem(pos, droppedId);
            }
        }

        /// <summary>
        /// BUG-07 CORRIGIDO: verifica ItemDatabase ANTES de Instantiate.
        /// Evita memory leak de GameObjects órfãos no servidor quando o
        /// banco de itens não está carregado ou o item não existe.
        /// </summary>
        [Server]
        private void SpawnWorldItem(Vector3 position, string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) return;

            // CORREÇÃO: check completo ANTES de qualquer alocação de objeto
            if (ItemDatabase.Instance == null)
            {
                Debug.LogWarning($"[ItemDropManager] ItemDatabase.Instance é null. Drop '{itemId}' ignorado.");
                return;
            }

            if (!ItemDatabase.Instance.Contains(itemId))
            {
                Debug.LogWarning($"[ItemDropManager] Item '{itemId}' não está no ItemDatabase. Drop ignorado.");
                return;
            }

            // Só agora instancia — todas as validações passaram
            var go   = Instantiate(worldItemPrefab, position, Quaternion.identity);
            var item = go.GetComponent<RPG.Network.WorldItem>();

            if (item == null)
            {
                Debug.LogError("[ItemDropManager] worldItemPrefab não tem WorldItem component!");
                Destroy(go); // destrói para não vazar
                return;
            }

            item.ServerInitialize(itemId);
            NetworkServer.Spawn(go);
            Debug.Log($"[ItemDropManager] Drop spawnado: {itemId} em {position}");
        }

        private Vector3 ScatterPosition(Vector3 center, int index)
        {
            if (index == 0) return center + Vector3.up * spawnHeightOffset;

            float angle = index * 137.5f * Mathf.Deg2Rad; // ângulo dourado
            float r     = dropScatterRadius * (0.5f + 0.5f * (index % 3) / 3f);
            return new Vector3(
                center.x + Mathf.Cos(angle) * r,
                center.y + spawnHeightOffset,
                center.z + Mathf.Sin(angle) * r);
        }
    }
}
