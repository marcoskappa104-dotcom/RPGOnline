using UnityEngine;
using System.Collections.Generic;

namespace RPG.Data
{
    /// <summary>
    /// ItemDatabase — Registry singleton de todos os ItemData do jogo.
    ///
    /// SETUP:
    ///   1. Crie um GameObject na GameplayScene chamado "ItemDatabase".
    ///   2. Adicione este componente.
    ///   3. Arraste TODOS os ScriptableObjects de item para a lista 'allItems'.
    ///
    /// COMO FUNCIONA:
    ///   - No servidor e cliente, GetItem(id) retorna o ItemData correto.
    ///   - O NetworkInventory trafega apenas strings (ItemId) pela rede,
    ///     não os ScriptableObjects — assim o tráfego é mínimo.
    ///   - O cliente resolve o ID → ItemData localmente via este database.
    /// </summary>
    public class ItemDatabase : MonoBehaviour
    {
        public static ItemDatabase Instance { get; private set; }

        [Header("Registre TODOS os itens do jogo aqui")]
        [SerializeField] private List<ItemData> allItems = new List<ItemData>();

        private readonly Dictionary<string, ItemData> _lookup = new Dictionary<string, ItemData>();

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
            BuildLookup();
        }

        private void BuildLookup()
        {
            _lookup.Clear();
            foreach (var item in allItems)
            {
                if (item == null) continue;
                if (string.IsNullOrEmpty(item.ItemId))
                {
                    Debug.LogError($"[ItemDatabase] Item '{item.name}' tem ItemId vazio!");
                    continue;
                }
                if (_lookup.ContainsKey(item.ItemId))
                {
                    Debug.LogError($"[ItemDatabase] ID duplicado: '{item.ItemId}' em '{item.name}'!");
                    continue;
                }
                _lookup[item.ItemId] = item;
            }
            Debug.Log($"[ItemDatabase] {_lookup.Count} itens registrados.");
        }

        /// <summary>Retorna null se o item não existir no database.</summary>
        public ItemData GetItem(string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) return null;
            _lookup.TryGetValue(itemId, out var item);
            return item;
        }

        public bool Contains(string itemId) => _lookup.ContainsKey(itemId);

        public List<ItemData> GetAllItems() => new List<ItemData>(allItems);

        public List<ItemData> GetItemsByType(ItemType type)
        {
            var result = new List<ItemData>();
            foreach (var item in allItems)
                if (item != null && item.Type == type)
                    result.Add(item);
            return result;
        }

        /// <summary>
        /// Sorteia um ItemId com base nos pesos de drop da lista fornecida.
        /// Retorna null se a lista estiver vazia ou todos os pesos forem 0.
        /// </summary>
        public static string RollDrop(List<ItemData> pool)
        {
            if (pool == null || pool.Count == 0) return null;

            int totalWeight = 0;
            foreach (var item in pool)
                if (item != null) totalWeight += item.DropWeight;

            if (totalWeight <= 0) return null;

            int roll = Random.Range(0, totalWeight);
            int acc  = 0;
            foreach (var item in pool)
            {
                if (item == null) continue;
                acc += item.DropWeight;
                if (roll < acc) return item.ItemId;
            }
            return null;
        }
    }
}