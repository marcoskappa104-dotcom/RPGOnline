using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;
using RPG.Data;

namespace RPG.UI
{
    /// <summary>
    /// InventorySlotUI v1 — Slot visual do inventário.
    ///
    /// Responsável por:
    ///   - Exibir ícone e quantidade do item.
    ///   - Mostrar tooltip ao passar o mouse (hover).
    ///   - Notificar o InventoryUI ao clicar (seleção/uso/equip).
    ///   - Destacar visualmente quando selecionado.
    ///
    /// PREFAB SUGERIDO (hierarquia):
    ///   InventorySlot (Button + InventorySlotUI)
    ///     ├── Background (Image — fundo do slot, muda cor por raridade)
    ///     ├── Icon (Image — ícone do item)
    ///     ├── QuantityText (TMP_Text — "x5", oculto se qty == 1)
    ///     └── SelectionBorder (Image — borda de seleção, inativo por padrão)
    /// </summary>
    public class InventorySlotUI : MonoBehaviour,
        IPointerEnterHandler, IPointerExitHandler, IPointerClickHandler
    {
        [Header("Referências visuais")]
        [SerializeField] private Image    iconImage;
        [SerializeField] private Image    backgroundImage;
        [SerializeField] private Image    selectionBorder;
        [SerializeField] private TMP_Text quantityText;

        [Header("Cores de fundo por raridade")]
        [SerializeField] private Color colorEmpty    = new Color(0.15f, 0.15f, 0.15f, 0.8f);
        [SerializeField] private Color colorCommon   = new Color(0.20f, 0.20f, 0.20f, 0.9f);
        [SerializeField] private Color colorUncommon = new Color(0.10f, 0.25f, 0.10f, 0.9f);
        [SerializeField] private Color colorRare     = new Color(0.10f, 0.15f, 0.30f, 0.9f);
        [SerializeField] private Color colorEpic     = new Color(0.20f, 0.08f, 0.28f, 0.9f);
        [SerializeField] private Color colorLegendary= new Color(0.30f, 0.18f, 0.05f, 0.9f);

        // ── Dados do slot ──────────────────────────────────────────────────
        private InventorySlotData _slotData;
        private ItemData          _itemData;
        private bool              _isEmpty = true;
        private bool              _selected = false;

        // ── Callback para o InventoryUI ────────────────────────────────────
        public System.Action<InventorySlotUI> OnSlotClicked;
        public System.Action<InventorySlotUI> OnSlotHoverEnter;
        public System.Action<InventorySlotUI> OnSlotHoverExit;

        // ── Getters ────────────────────────────────────────────────────────
        public InventorySlotData SlotData => _slotData;
        public ItemData          ItemData => _itemData;
        public bool              IsEmpty  => _isEmpty;

        // ── Setup ──────────────────────────────────────────────────────────

        /// <summary>Popula o slot com dados de um item.</summary>
        public void Setup(InventorySlotData slotData, ItemData itemData)
        {
            _slotData = slotData;
            _itemData = itemData;
            _isEmpty  = itemData == null || string.IsNullOrEmpty(slotData.ItemId);

            RefreshVisual();
        }

        /// <summary>Esvazia o slot (sem item).</summary>
        public void SetEmpty(int slotIndex = -1)
        {
            _slotData = InventorySlotData.Empty(slotIndex);
            _itemData = null;
            _isEmpty  = true;
            RefreshVisual();
        }

        // ── Visual ─────────────────────────────────────────────────────────

        private void RefreshVisual()
        {
            if (_isEmpty)
            {
                if (iconImage      != null) { iconImage.sprite = null; iconImage.enabled = false; }
                if (quantityText   != null) quantityText.gameObject.SetActive(false);
                if (backgroundImage!= null) backgroundImage.color = colorEmpty;
                SetSelected(false);
                return;
            }

            // Ícone
            if (iconImage != null)
            {
                iconImage.enabled = true;
                iconImage.sprite  = _itemData.Icon;
                iconImage.color   = _itemData.Icon != null ? Color.white : new Color(1f,1f,1f,0.3f);
            }

            // Quantidade (só mostra se > 1)
            if (quantityText != null)
            {
                bool showQty = _slotData.Quantity > 1;
                quantityText.gameObject.SetActive(showQty);
                if (showQty) quantityText.text = $"x{_slotData.Quantity}";
            }

            // Cor de fundo por raridade
            if (backgroundImage != null)
            {
                backgroundImage.color = _itemData.Rarity switch
                {
                    ItemRarity.Common    => colorCommon,
                    ItemRarity.Uncommon  => colorUncommon,
                    ItemRarity.Rare      => colorRare,
                    ItemRarity.Epic      => colorEpic,
                    ItemRarity.Legendary => colorLegendary,
                    _                    => colorCommon
                };
            }
        }

        // ── Seleção ────────────────────────────────────────────────────────

        public void SetSelected(bool selected)
        {
            _selected = selected;
            if (selectionBorder != null)
                selectionBorder.gameObject.SetActive(selected);
        }

        // ── Eventos de ponteiro ────────────────────────────────────────────

        public void OnPointerEnter(PointerEventData eventData)
        {
            if (!_isEmpty)
                OnSlotHoverEnter?.Invoke(this);
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            OnSlotHoverExit?.Invoke(this);
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (!_isEmpty)
                OnSlotClicked?.Invoke(this);
        }
    }
}