using System;
using Mirror;

namespace RPG.Data
{
    /// <summary>
    /// InventorySlotData — representa um slot no inventário do jogador.
    ///
    /// Trafega pela rede via Mirror SyncList.
    /// Apenas ItemId (string) é enviado — o cliente resolve o ItemData
    /// localmente via ItemDatabase.
    ///
    /// slotIndex: índice único e estável do slot (nunca muda após atribuído).
    /// Isso permite referenciar itens de forma segura (ex: "equipe o slot 5").
    /// </summary>
    [Serializable]
    public struct InventorySlotData : NetworkMessage
    {
        /// <summary>Índice único do slot no inventário deste jogador.</summary>
        public int    SlotIndex;

        /// <summary>ID do item (referência ao ItemDatabase).</summary>
        public string ItemId;

        /// <summary>Quantidade (para stackáveis futuros; padrão = 1).</summary>
        public int    Quantity;

        public bool IsEmpty => string.IsNullOrEmpty(ItemId);

        public static InventorySlotData Empty(int slotIndex) => new InventorySlotData
        {
            SlotIndex = slotIndex,
            ItemId    = "",
            Quantity  = 0
        };
    }

    /// <summary>
    /// PowerGemLoadout — os 4 slots de Joia do Poder (Q, W, E, R).
    /// Sincronizado via SyncVars no NetworkInventory.
    /// </summary>
    [Serializable]
    public struct PowerGemLoadout : NetworkMessage
    {
        /// <summary>ItemId da Joia equipada no slot Q (índice 0).</summary>
        public string SlotQ;
        /// <summary>ItemId da Joia equipada no slot W (índice 1).</summary>
        public string SlotW;
        /// <summary>ItemId da Joia equipada no slot E (índice 2).</summary>
        public string SlotE;
        /// <summary>ItemId da Joia equipada no slot R (índice 3).</summary>
        public string SlotR;

        public string GetSlot(int index) => index switch
        {
            0 => SlotQ ?? "",
            1 => SlotW ?? "",
            2 => SlotE ?? "",
            3 => SlotR ?? "",
            _ => ""
        };

        public PowerGemLoadout WithSlot(int index, string itemId)
        {
            var copy = this;
            switch (index)
            {
                case 0: copy.SlotQ = itemId; break;
                case 1: copy.SlotW = itemId; break;
                case 2: copy.SlotE = itemId; break;
                case 3: copy.SlotR = itemId; break;
            }
            return copy;
        }
    }
}