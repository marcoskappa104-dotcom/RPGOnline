using UnityEngine;
using Mirror;
using RPG.Data;
using RPG.Combat;
using RPG.UI;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace RPG.Network
{
    /// <summary>
    /// NetworkInventory v3
    ///
    /// CORREÇÕES v3:
    ///
    ///   BUG-01 — CmdEquipGem/CmdUnequipGem não moviam itens:
    ///     Equipar joia agora REMOVE o item do inventário antes de setar o slot.
    ///     Se já havia uma joia no slot, ela é DEVOLVIDA ao inventário primeiro (swap).
    ///     Desequipar agora ADICIONA a joia de volta ao inventário antes de limpar o slot.
    ///
    ///   BUG-02 — _nextSlotIndex podia colidir após carregar do banco:
    ///     ServerLoadFromDatabase agora garante _nextSlotIndex = max(SlotIndex) + 1
    ///     usando LINQ para evitar colisão com slots já existentes.
    ///
    ///   BUG-17 — OnGemSlotChanged único para 4 SyncVars dificultava debug:
    ///     Hooks separados por slot (OnGemSlotQChanged, W, E, R) que todos chamam
    ///     OnGemLoadoutChanged. Mais rastreável e preparado para otimizações futuras.
    /// </summary>
    [RequireComponent(typeof(NetworkIdentity))]
    public class NetworkInventory : NetworkBehaviour
    {
        // ── SyncList — Inventário ──────────────────────────────────────────
        public readonly SyncList<InventorySlotData> Slots = new SyncList<InventorySlotData>();

        // ── SyncVars — Joias Equipadas (BUG-17: hooks separados) ──────────
        [SyncVar(hook = nameof(OnGemSlotQChanged))] public string GemSlotQ = "";
        [SyncVar(hook = nameof(OnGemSlotWChanged))] public string GemSlotW = "";
        [SyncVar(hook = nameof(OnGemSlotEChanged))] public string GemSlotE = "";
        [SyncVar(hook = nameof(OnGemSlotRChanged))] public string GemSlotR = "";

        // ── Eventos (cliente) ──────────────────────────────────────────────
        public event Action OnInventoryChanged;
        public event Action OnGemLoadoutChanged;

        private int _nextSlotIndex = 0;

        // ── Lifecycle ──────────────────────────────────────────────────────

        public override void OnStartClient()
        {
            Slots.Callback += OnSlotsChanged;
        }

        public override void OnStartLocalPlayer()
        {
            StartCoroutine(BindUIDelayed());
        }

        public override void OnStopClient()
        {
            Slots.Callback -= OnSlotsChanged;
        }

        private IEnumerator BindUIDelayed()
        {
            yield return null;
            yield return null;

            InventoryUI.Instance?.BindInventory(this);
            PowerGemUI.Instance?.BindInventory(this);
            Debug.Log("[NetworkInventory] UIs de inventário vinculadas.");
        }

        // ── Hooks (BUG-17: separados por slot) ────────────────────────────

        private void OnSlotsChanged(SyncList<InventorySlotData>.Operation op,
                                    int index, InventorySlotData oldItem, InventorySlotData newItem)
            => OnInventoryChanged?.Invoke();

        private void OnGemSlotQChanged(string oldVal, string newVal) => OnGemLoadoutChanged?.Invoke();
        private void OnGemSlotWChanged(string oldVal, string newVal) => OnGemLoadoutChanged?.Invoke();
        private void OnGemSlotEChanged(string oldVal, string newVal) => OnGemLoadoutChanged?.Invoke();
        private void OnGemSlotRChanged(string oldVal, string newVal) => OnGemLoadoutChanged?.Invoke();

        // ══════════════════════════════════════════════════════════════════
        // INVENTÁRIO — API do servidor
        // ══════════════════════════════════════════════════════════════════

        [Server]
        public int ServerAddItem(string itemId, int quantity = 1)
        {
            if (string.IsNullOrEmpty(itemId))
            {
                Debug.LogWarning("[NetworkInventory] ServerAddItem: itemId vazio.");
                return -1;
            }

            var db = ItemDatabase.Instance;
            if (db == null || !db.Contains(itemId))
            {
                Debug.LogWarning($"[NetworkInventory] Item '{itemId}' não no ItemDatabase.");
                return -1;
            }

            var slot = new InventorySlotData
            {
                SlotIndex = _nextSlotIndex++,
                ItemId    = itemId,
                Quantity  = quantity
            };

            Slots.Add(slot);
            Debug.Log($"[NetworkInventory] Item adicionado: {itemId} x{quantity} slot:{slot.SlotIndex}");
            return slot.SlotIndex;
        }

        [Server]
        public bool ServerRemoveSlot(int slotIndex)
        {
            for (int i = 0; i < Slots.Count; i++)
            {
                if (Slots[i].SlotIndex == slotIndex)
                {
                    Slots.RemoveAt(i);
                    return true;
                }
            }
            return false;
        }

        [Server]
        public bool ServerRemoveItemById(string itemId)
        {
            for (int i = 0; i < Slots.Count; i++)
            {
                if (Slots[i].ItemId == itemId)
                {
                    Slots.RemoveAt(i);
                    return true;
                }
            }
            return false;
        }

        public bool HasItem(string itemId)
        {
            foreach (var slot in Slots)
                if (slot.ItemId == itemId) return true;
            return false;
        }

        public int FindSlotByItemId(string itemId)
        {
            foreach (var slot in Slots)
                if (slot.ItemId == itemId) return slot.SlotIndex;
            return -1;
        }

        // BUG-02: _nextSlotIndex calculado como max+1 para evitar colisão
        [Server]
        public void ServerLoadFromDatabase(string characterId)
        {
            var db = Managers.DatabaseManager.Instance;
            if (db == null) return;

            Slots.Clear();
            _nextSlotIndex = 0;

            var rows = db.LoadInventory(characterId);
            foreach (var row in rows)
            {
                if (string.IsNullOrEmpty(row.ItemId)) continue;
                var slot = new InventorySlotData
                {
                    SlotIndex = row.SlotIndex >= 0 ? row.SlotIndex : _nextSlotIndex,
                    ItemId    = row.ItemId,
                    Quantity  = row.Quantity
                };
                Slots.Add(slot);
            }

            // BUG-02: garante _nextSlotIndex acima de todos os existentes
            if (Slots.Count > 0)
                _nextSlotIndex = Slots.Max(s => s.SlotIndex) + 1;

            Debug.Log($"[NetworkInventory] {Slots.Count} itens carregados para char:{characterId} | nextSlot:{_nextSlotIndex}");
        }

        [Server]
        public void ServerLoadGemLoadout(string characterId)
        {
            var db = Managers.DatabaseManager.Instance;
            if (db == null) return;

            var loadout = db.LoadGemLoadout(characterId);
            GemSlotQ = loadout.SlotQ ?? "";
            GemSlotW = loadout.SlotW ?? "";
            GemSlotE = loadout.SlotE ?? "";
            GemSlotR = loadout.SlotR ?? "";

            Debug.Log($"[NetworkInventory] Loadout: Q={GemSlotQ} W={GemSlotW} E={GemSlotE} R={GemSlotR}");
        }

        [Server]
        public void ServerSaveAll(string characterId, string username)
        {
            var db = Managers.DatabaseManager.Instance;
            if (db == null) return;

            db.SaveInventory(characterId, username, new List<InventorySlotData>(Slots));
            db.SaveGemLoadout(characterId, new PowerGemLoadout
            {
                SlotQ = GemSlotQ, SlotW = GemSlotW,
                SlotE = GemSlotE, SlotR = GemSlotR
            });
        }

        // ══════════════════════════════════════════════════════════════════
        // JOIAS DO PODER — Commands
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// BUG-01 CORRIGIDO: Equipa joia e REMOVE do inventário.
        /// Se já havia joia no slot, DEVOLVE ao inventário (swap).
        /// </summary>
        [Command]
        public void CmdEquipGem(int skillSlotIndex, int inventorySlotIndex)
        {
            if (skillSlotIndex < 0 || skillSlotIndex > 3) return;

            InventorySlotData? foundSlot = null;
            foreach (var s in Slots)
                if (s.SlotIndex == inventorySlotIndex) { foundSlot = s; break; }

            if (foundSlot == null)
            {
                Debug.LogWarning($"[NetworkInventory] CmdEquipGem: slot {inventorySlotIndex} não encontrado.");
                return;
            }

            var itemData = ItemDatabase.Instance?.GetItem(foundSlot.Value.ItemId);
            if (itemData == null || !itemData.IsPowerGem)
            {
                Debug.LogWarning($"[NetworkInventory] '{foundSlot.Value.ItemId}' não é PowerGem.");
                return;
            }

            // BUG-01: se já há joia no slot, devolve ao inventário primeiro (swap)
            string currentGemId = GetGemItemId(skillSlotIndex);
            if (!string.IsNullOrEmpty(currentGemId))
            {
                ServerAddItem(currentGemId, 1);
                Debug.Log($"[NetworkInventory] Swap: '{currentGemId}' devolvida ao inventário.");
            }

            // BUG-01: remove do inventário antes de equipar
            ServerRemoveSlot(inventorySlotIndex);

            ServerSetGemSlot(skillSlotIndex, foundSlot.Value.ItemId);
            Debug.Log($"[NetworkInventory] '{itemData.DisplayName}' equipada no slot {skillSlotIndex}.");
        }

        /// <summary>
        /// BUG-01 CORRIGIDO: Desequipa joia e DEVOLVE ao inventário.
        /// </summary>
        [Command]
        public void CmdUnequipGem(int skillSlotIndex)
        {
            if (skillSlotIndex < 0 || skillSlotIndex > 3) return;

            string gemId = GetGemItemId(skillSlotIndex);
            if (string.IsNullOrEmpty(gemId))
            {
                Debug.LogWarning($"[NetworkInventory] CmdUnequipGem: slot {skillSlotIndex} já vazio.");
                return;
            }

            // BUG-01: devolve ao inventário antes de limpar o slot
            int newSlot = ServerAddItem(gemId, 1);
            if (newSlot < 0)
            {
                Debug.LogError($"[NetworkInventory] Falha ao devolver '{gemId}' ao inventário! Item perdido.");
                return;
            }

            ServerSetGemSlot(skillSlotIndex, "");
            Debug.Log($"[NetworkInventory] Slot {skillSlotIndex} esvaziado — '{gemId}' devolvida (slot {newSlot}).");
        }

        [Server]
        private void ServerSetGemSlot(int index, string itemId)
        {
            switch (index)
            {
                case 0: GemSlotQ = itemId; break;
                case 1: GemSlotW = itemId; break;
                case 2: GemSlotE = itemId; break;
                case 3: GemSlotR = itemId; break;
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // INVENTÁRIO — Commands do cliente
        // ══════════════════════════════════════════════════════════════════

        [Command]
        public void CmdRemoveItem(int inventorySlotIndex)
        {
            bool removed = ServerRemoveSlot(inventorySlotIndex);
            if (removed)
                Debug.Log($"[NetworkInventory] Item descartado: slot {inventorySlotIndex}");
            else
                Debug.LogWarning($"[NetworkInventory] CmdRemoveItem: slot {inventorySlotIndex} não encontrado.");
        }

        [Command]
        public void CmdUseConsumable(int inventorySlotIndex)
        {
            InventorySlotData? foundSlot = null;
            foreach (var s in Slots)
                if (s.SlotIndex == inventorySlotIndex) { foundSlot = s; break; }
            if (foundSlot == null) return;

            var itemData = ItemDatabase.Instance?.GetItem(foundSlot.Value.ItemId);
            if (itemData == null || !itemData.IsConsumable) return;

            var netPlayer = GetComponent<NetworkPlayer>();
            if (netPlayer == null || netPlayer.Dead) return;

            if (itemData.HealAmount > 0f) netPlayer.ServerApplyHeal(itemData.HealAmount);
            if (itemData.ManaAmount > 0f) netPlayer.ServerRestoreMP(itemData.ManaAmount);

            ServerRemoveSlot(foundSlot.Value.SlotIndex);
            Debug.Log($"[NetworkInventory] Consumível '{itemData.DisplayName}' usado.");
        }

        // ══════════════════════════════════════════════════════════════════
        // API de leitura
        // ══════════════════════════════════════════════════════════════════

        public string GetGemItemId(int skillSlotIndex) => skillSlotIndex switch
        {
            0 => GemSlotQ ?? "",
            1 => GemSlotW ?? "",
            2 => GemSlotE ?? "",
            3 => GemSlotR ?? "",
            _ => ""
        };

        public SkillData GetEquippedSkill(int skillSlotIndex)
        {
            string gemId = GetGemItemId(skillSlotIndex);
            if (string.IsNullOrEmpty(gemId)) return null;
            return ItemDatabase.Instance?.GetItem(gemId)?.EmbeddedSkill;
        }

        public int EquippedGemCount()
        {
            int count = 0;
            for (int i = 0; i < 4; i++)
                if (!string.IsNullOrEmpty(GetGemItemId(i))) count++;
            return count;
        }
    }
}