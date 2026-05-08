using UnityEngine;
using RPG.Combat;

namespace RPG.Data
{
    public enum ItemType
    {
        PowerGem,       // Joia do Poder — concede uma skill
        Equipment,      // Armadura, arma, etc (futuro)
        Consumable,     // Poção, comida, etc
        Misc            // Materiais, quest items, etc
    }

    public enum ItemRarity
    {
        Common,
        Uncommon,
        Rare,
        Epic,
        Legendary
    }

    /// <summary>
    /// ItemData — ScriptableObject que define um item do jogo.
    ///
    /// Para criar um item:
    ///   Assets → Create → RPG → Item Data
    ///
    /// PowerGem: preencha embeddedSkill com os dados da skill.
    /// O ItemId deve ser único e estável (usado no banco de dados).
    /// </summary>
    [CreateAssetMenu(menuName = "RPG/Item Data", fileName = "Item_New")]
    public class ItemData : ScriptableObject
    {
        [Header("Identificação")]
        [Tooltip("ID único e estável — NUNCA altere após criar personagens com este item.")]
        public string ItemId = "item_001";

        public string DisplayName = "Item";

        [TextArea(2, 4)]
        public string Description = "Descrição do item.";

        public ItemType   Type   = ItemType.Misc;
        public ItemRarity Rarity = ItemRarity.Common;

        [Header("Visual")]
        public Sprite Icon;

        [Header("Drop")]
        [Tooltip("Peso de drop relativo. 100 = comum, 1 = rarissimo.")]
        [Range(0, 100)]
        public int DropWeight = 10;

        [Header("PowerGem — preencha apenas se Type == PowerGem")]
        [Tooltip("Dados da skill concedida por esta joia.")]
        public SkillData EmbeddedSkill;

        [Header("Consumable — preencha apenas se Type == Consumable")]
        public float HealAmount  = 0f;
        public float ManaAmount  = 0f;
        public float BuffDuration = 0f;

        // ── Helpers ────────────────────────────────────────────────────────

        public bool IsPowerGem  => Type == ItemType.PowerGem;
        public bool IsConsumable => Type == ItemType.Consumable;

        public Color RarityColor => Rarity switch
        {
            ItemRarity.Common    => new Color(0.8f, 0.8f, 0.8f),
            ItemRarity.Uncommon  => new Color(0.3f, 0.8f, 0.3f),
            ItemRarity.Rare      => new Color(0.2f, 0.5f, 1.0f),
            ItemRarity.Epic      => new Color(0.7f, 0.2f, 0.9f),
            ItemRarity.Legendary => new Color(1.0f, 0.6f, 0.1f),
            _                    => Color.white
        };

        public string RarityDisplayName => Rarity switch
        {
            ItemRarity.Common    => "Comum",
            ItemRarity.Uncommon  => "Incomum",
            ItemRarity.Rare      => "Raro",
            ItemRarity.Epic      => "Épico",
            ItemRarity.Legendary => "Lendário",
            _                    => Rarity.ToString()
        };
    }
}