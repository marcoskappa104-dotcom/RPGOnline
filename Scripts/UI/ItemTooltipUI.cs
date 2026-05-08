using UnityEngine;
using UnityEngine.UI;
using TMPro;
using RPG.Data;

namespace RPG.UI
{
    /// <summary>
    /// ItemTooltipUI v1 — Tooltip flutuante com detalhes do item.
    ///
    /// Aparece ao passar o mouse sobre um slot de inventário ou slot de joia.
    /// Segue o cursor com um offset para não ficar sob o ponteiro.
    ///
    /// SETUP:
    ///   1. Crie um Canvas (ScreenSpace-Overlay) chamado "TooltipCanvas" com
    ///      Sort Order alto (ex: 100) para ficar sobre tudo.
    ///   2. Adicione um Panel filho chamado "TooltipPanel" com este componente.
    ///   3. Configure as referências no Inspector.
    ///   4. O painel começa INATIVO — só aparece quando Show() é chamado.
    ///
    /// PREFAB HIERARQUIA:
    ///   TooltipPanel (RectTransform + CanvasGroup + ItemTooltipUI)
    ///     ├── ItemName      (TMP_Text — nome em cor da raridade)
    ///     ├── ItemRarity    (TMP_Text — "Raro", "Épico" etc)
    ///     ├── ItemType      (TMP_Text — "Joia do Poder", "Consumível" etc)
    ///     ├── Divider       (Image — linha separadora)
    ///     ├── Description   (TMP_Text — descrição do item)
    ///     ├── SkillInfo     (GameObject — só visível para PowerGem)
    ///     │   ├── SkillName (TMP_Text)
    ///     │   └── SkillStats(TMP_Text — cooldown, mana, range)
    ///     └── ConsumableInfo(GameObject — só visível para Consumable)
    ///         └── ConsumableStats (TMP_Text — heal, mana, duração)
    /// </summary>
    public class ItemTooltipUI : MonoBehaviour
    {
        public static ItemTooltipUI Instance { get; private set; }

        [Header("Painel raiz")]
        [SerializeField] private RectTransform tooltipRect;
        [SerializeField] private CanvasGroup   canvasGroup;

        [Header("Textos")]
        [SerializeField] private TMP_Text itemNameText;
        [SerializeField] private TMP_Text itemRarityText;
        [SerializeField] private TMP_Text itemTypeText;
        [SerializeField] private TMP_Text descriptionText;

        [Header("Seção PowerGem")]
        [SerializeField] private GameObject gemSection;
        [SerializeField] private TMP_Text   gemSkillNameText;
        [SerializeField] private TMP_Text   gemSkillStatsText;

        [Header("Seção Consumível")]
        [SerializeField] private GameObject consumableSection;
        [SerializeField] private TMP_Text   consumableStatsText;

        [Header("Comportamento")]
        [SerializeField] private Vector2 offset         = new Vector2(15f, -10f);
        [SerializeField] private float   edgePadding    = 10f;

        private Canvas _canvas;
        private bool   _visible = false;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;

            _canvas = GetComponentInParent<Canvas>();
            Hide();
        }

        private void Update()
        {
            if (_visible) FollowCursor();
        }

        // ── API pública ────────────────────────────────────────────────────

        public void Show(ItemData item)
        {
            if (item == null) { Hide(); return; }

            PopulateContent(item);

            if (canvasGroup != null)
            {
                canvasGroup.alpha          = 1f;
                canvasGroup.blocksRaycasts = false;
            }

            gameObject.SetActive(true);
            _visible = true;

            FollowCursor();
        }

        public void Hide()
        {
            _visible = false;
            gameObject.SetActive(false);
        }

        // ── Conteúdo ───────────────────────────────────────────────────────

        private void PopulateContent(ItemData item)
        {
            // Nome com cor da raridade
            if (itemNameText != null)
            {
                itemNameText.text  = item.DisplayName;
                itemNameText.color = item.RarityColor;
            }

            // Raridade
            if (itemRarityText != null)
            {
                itemRarityText.text  = item.RarityDisplayName;
                itemRarityText.color = item.RarityColor;
            }

            // Tipo
            if (itemTypeText != null)
            {
                itemTypeText.text = item.Type switch
                {
                    ItemType.PowerGem   => "✦ Joia do Poder",
                    ItemType.Equipment  => "⚔ Equipamento",
                    ItemType.Consumable => "⬡ Consumível",
                    ItemType.Misc       => "◈ Miscelânea",
                    _                   => item.Type.ToString()
                };
            }

            // Descrição
            if (descriptionText != null)
                descriptionText.text = item.Description;

            // Seção PowerGem
            bool isGem = item.IsPowerGem && item.EmbeddedSkill != null;
            if (gemSection != null) gemSection.SetActive(isGem);

            if (isGem && item.EmbeddedSkill != null)
            {
                var skill = item.EmbeddedSkill;

                if (gemSkillNameText  != null)
                    gemSkillNameText.text = $"⚡ {skill.Name}";

                if (gemSkillStatsText != null)
                {
                    gemSkillStatsText.text =
                        $"Tipo: {SkillTypeDisplay(skill.Type)}\n" +
                        $"Alcance: {skill.Range:0.0}m\n" +
                        $"Custo MP: {skill.ManaCost:0}\n" +
                        $"Cooldown: {skill.Cooldown:0.0}s\n" +
                        $"Mult. ATK: {skill.AtkMultiplier:0.00}x";
                }
            }

            // Seção Consumível
            bool isConsumable = item.IsConsumable;
            if (consumableSection != null) consumableSection.SetActive(isConsumable);

            if (isConsumable && consumableStatsText != null)
            {
                string stats = "";
                if (item.HealAmount  > 0f) stats += $"❤ Recupera {item.HealAmount:0} HP\n";
                if (item.ManaAmount  > 0f) stats += $"✦ Recupera {item.ManaAmount:0} MP\n";
                if (item.BuffDuration > 0f) stats += $"⏱ Duração: {item.BuffDuration:0}s\n";
                consumableStatsText.text = stats.TrimEnd();
            }
        }

        // ── Posicionamento ─────────────────────────────────────────────────

        private void FollowCursor()
        {
            if (tooltipRect == null || _canvas == null) return;

            Vector2 mousePos = Input.mousePosition;

            // Converte para espaço do canvas
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                _canvas.transform as RectTransform,
                mousePos, _canvas.worldCamera,
                out Vector2 localPoint);

            Vector2 targetPos = localPoint + offset;

            // Clamp para não sair da tela
            var canvasRect = _canvas.transform as RectTransform;
            if (canvasRect != null)
            {
                float halfW = tooltipRect.rect.width  * 0.5f;
                float halfH = tooltipRect.rect.height * 0.5f;
                float maxX  = canvasRect.rect.width  * 0.5f - halfW - edgePadding;
                float maxY  = canvasRect.rect.height * 0.5f - halfH - edgePadding;
                float minX  = -maxX;
                float minY  = -maxY;

                targetPos.x = Mathf.Clamp(targetPos.x, minX, maxX);
                targetPos.y = Mathf.Clamp(targetPos.y, minY, maxY);
            }

            tooltipRect.anchoredPosition = targetPos;
        }

        // ── Helpers ────────────────────────────────────────────────────────

        private static string SkillTypeDisplay(Combat.SkillType type) => type switch
        {
            Combat.SkillType.Physical => "Físico",
            Combat.SkillType.Magical  => "Mágico",
            Combat.SkillType.Heal     => "Cura",
            Combat.SkillType.Buff     => "Buff",
            _ => type.ToString()
        };
    }
}