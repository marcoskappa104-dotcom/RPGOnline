using UnityEngine;
using Mirror;
using RPG.Data;
using System.Collections.Generic;

namespace RPG.Managers
{
    /// <summary>
    /// ItemDropManager v1 — Gerencia o spawn de itens no chão quando monstros morrem.
    ///
    /// SETUP:
    ///   1. Crie um GameObject na GameplayScene chamado "ItemDropManager".
    ///   2. Adicione este componente.
    ///   3. Configure o worldItemPrefab (prefab com NetworkIdentity + WorldItem).
    ///   4. Configure a dropTable com os itens que podem dropar.
    ///
    /// INTEGRAÇÃO COM NetworkMonsterEntity:
    ///   Na ServerDie() do monstro, chame:
    ///     ItemDropManager.Instance?.ServerSpawnDrop(transform.position, dropTable);
    ///
    ///   Cada monstro pode ter sua própria dropTable configurada no prefab,
    ///   ou usar a tabela global do ItemDropManager.
    ///
    /// DROP SYSTEM:
    ///   - Cada monstro tem uma chance de dropar (dropChance 0-100%).
    ///   - Se o roll passar, sorteia um item da dropTable por peso (DropWeight).
    ///   - Itens de raridade mais alta têm DropWeight menor.
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
        [Tooltip("Offset vertical do spawn (para o item aparecer acima do chão).")]
        [SerializeField] private float spawnHeightOffset = 0.3f;

        [Tooltip("Raio de dispersão dos drops (múltiplos itens ficam espalhados).")]
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
        /// extraDrops: itens garantidos (ex: quest drops). Pode ser vazio.
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

        [Server]
        private void SpawnWorldItem(Vector3 position, string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) return;
            if (ItemDatabase.Instance == null || !ItemDatabase.Instance.Contains(itemId))
            {
                Debug.LogWarning($"[ItemDropManager] Item '{itemId}' não está no ItemDatabase.");
                return;
            }

            var go   = Instantiate(worldItemPrefab, position, Quaternion.identity);
            var item = go.GetComponent<RPG.Network.WorldItem>();
            if (item == null)
            {
                Debug.LogError("[ItemDropManager] worldItemPrefab não tem WorldItem component!");
                Destroy(go);
                return;
            }

            item.ServerInitialize(itemId);
            NetworkServer.Spawn(go);
            Debug.Log($"[ItemDropManager] Drop spawnado: {itemId} em {position}");
        }

        private Vector3 ScatterPosition(Vector3 center, int index)
        {
            if (index == 0) return center + Vector3.up * spawnHeightOffset;

            float angle = index * 137.5f * Mathf.Deg2Rad; // ângulo dourado para dispersão natural
            float r     = dropScatterRadius * (0.5f + 0.5f * (index % 3) / 3f);
            return new Vector3(
                center.x + Mathf.Cos(angle) * r,
                center.y + spawnHeightOffset,
                center.z + Mathf.Sin(angle) * r);
        }
    }
}