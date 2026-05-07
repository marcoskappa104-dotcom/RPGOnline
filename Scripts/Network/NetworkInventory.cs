using UnityEngine;
using Mirror;
using RPG.Data;
using RPG.Combat;
using System;
using System.Collections;
using System.Collections.Generic;

namespace RPG.Network
{
    /// <summary>
    /// NetworkInventory v2 — Inventário e Joias do Poder server-authoritative.
    ///
    /// ARQUITETURA:
    ///   - Inventário: SyncList de InventorySlotData (ItemId + Quantity).
    ///     Sem limite de slots. Sem peso. Itens adicionados/removidos apenas no servidor.
    ///
    ///   - Joias do Poder: 4 SyncVars (GemSlotQ/W/E/R).
    ///     Ao equipar uma joia, o SkillSystem lê os dados via GetEquippedSkill(index).
    ///
    ///   - Persistência: ServerSaveAll chama DatabaseManager para salvar inventário
    ///     e loadout de joias. Chamado pelo NetworkPlayer.ServerSaveCharacter.
    ///
    /// MUDANÇAS v2:
    ///   - CmdRemoveItem adicionado (usado pelo InventoryUI ao descartar).
    ///   - OnStartLocalPlayer vincula InventoryUI e PowerGemUI automaticamente.
    /// </summary>
    [RequireComponent(typeof(NetworkIdentity))]
    public class NetworkInventory : NetworkBehaviour
    {
        // ── SyncList — Inventário ──────────────────────────────────────────
        public readonly SyncList<InventorySlotData> Slots = new SyncList<InventorySlotData>();

        // ── SyncVars — Joias Equipadas ────────────────────────────────────
        [SyncVar(hook = nameof(OnGemSlotChanged))] public string GemSlotQ = "";
        [SyncVar(hook = nameof(OnGemSlotChanged))] public string GemSlotW = "";
        [SyncVar(hook = nameof(OnGemSlotChanged))] public string GemSlotE = "";
        [SyncVar(hook = nameof(OnGemSlotChanged))] public string GemSlotR = "";

        // ── Eventos (cliente) ──────────────────────────────────────────────
        /// <summary>Disparado quando o inventário muda (add/remove item).</summary>
        public event Action OnInventoryChanged;
        /// <summary>Disparado quando qualquer slot de joia muda.</summary>
        public event Action OnGemLoadoutChanged;

        private int _nextSlotIndex = 0;

        // ── Lifecycle ──────────────────────────────────────────────────────

        public override void OnStartClient()
        {
            Slots.Callback += OnSlotsChanged;
        }

        public override void OnStartLocalPlayer()
        {
            // Vincula as UIs de inventário e joias depois que tudo carregou
            StartCoroutine(BindUIDelayed());
        }

        public override void OnStopClient()
        {
            Slots.Callback -= OnSlotsChanged;
        }

        private IEnumerator BindUIDelayed()
        {
            // Aguarda 2 frames para garantir que os UIs já iniciaram
            yield return null;
            yield return null;

            InventoryUI.Instance?.BindInventory(this);
            PowerGemUI.Instance?.BindInventory(this);
            Debug.Log("[NetworkInventory] UIs de inventário vinculadas.");
        }

        // ── Hooks ──────────────────────────────────────────────────────────

        private void OnSlotsChanged(SyncList<InventorySlotData>.Operation op,
                                    int index, InventorySlotData oldItem, InventorySlotData newItem)
        {
            OnInventoryChanged?.Invoke();
        }

        private void OnGemSlotChanged(string oldVal, string newVal)
        {
            OnGemLoadoutChanged?.Invoke();
        }

        // ══════════════════════════════════════════════════════════════════
        // INVENTÁRIO — API do servidor
        // ══════════════════════════════════════════════════════════════════

        /// <summary>Adiciona item ao inventário. Retorna SlotIndex criado ou -1.</summary>
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

        /// <summary>Remove slot pelo SlotIndex. Retorna true se removido.</summary>
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

        /// <summary>Remove primeira ocorrência do itemId. Retorna true se removido.</summary>
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

        /// <summary>Carrega inventário do banco (chamado no ServerInitialize).</summary>
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
                if (slot.SlotIndex >= _nextSlotIndex)
                    _nextSlotIndex = slot.SlotIndex + 1;
                Slots.Add(slot);
            }

            Debug.Log($"[NetworkInventory] {Slots.Count} itens carregados para char:{characterId}");
        }

        /// <summary>Carrega loadout de joias do banco (chamado no ServerInitialize).</summary>
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

        /// <summary>Salva inventário e loadout (chamado pelo ServerSaveCharacter).</summary>
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

        /// <summary>Equipa joia no slot de skill (0=Q, 1=W, 2=E, 3=R).</summary>
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

            ServerSetGemSlot(skillSlotIndex, foundSlot.Value.ItemId);
            Debug.Log($"[NetworkInventory] '{itemData.DisplayName}' → slot {skillSlotIndex}.");
        }

        /// <summary>Remove joia de um slot de skill (0=Q, 1=W, 2=E, 3=R).</summary>
        [Command]
        public void CmdUnequipGem(int skillSlotIndex)
        {
            if (skillSlotIndex < 0 || skillSlotIndex > 3) return;
            ServerSetGemSlot(skillSlotIndex, "");
            Debug.Log($"[NetworkInventory] Slot {skillSlotIndex} esvaziado.");
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

        /// <summary>Remove (descarta) um item do inventário pelo SlotIndex.</summary>
        [Command]
        public void CmdRemoveItem(int inventorySlotIndex)
        {
            bool removed = ServerRemoveSlot(inventorySlotIndex);
            if (removed)
                Debug.Log($"[NetworkInventory] Item descartado: slot {inventorySlotIndex}");
            else
                Debug.LogWarning($"[NetworkInventory] CmdRemoveItem: slot {inventorySlotIndex} não encontrado.");
        }

        /// <summary>Usa um consumível do inventário pelo SlotIndex.</summary>
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
        // API de leitura — cliente e servidor
        // ══════════════════════════════════════════════════════════════════

        /// <summary>Retorna ItemId da joia no slot (0-3). Vazio = sem joia.</summary>
        public string GetGemItemId(int skillSlotIndex) => skillSlotIndex switch
        {
            0 => GemSlotQ ?? "",
            1 => GemSlotW ?? "",
            2 => GemSlotE ?? "",
            3 => GemSlotR ?? "",
            _ => ""
        };

        /// <summary>Retorna SkillData da joia equipada no slot. Null se vazio.</summary>
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