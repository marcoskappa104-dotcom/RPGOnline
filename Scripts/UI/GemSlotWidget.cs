using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;
using RPG.Data;

namespace RPG.UI
{
    /// <summary>
    /// GemSlotWidget — componente visual de um slot de Joia do Poder (Q/W/E/R).
    ///
    /// IMPORTANTE: Este arquivo deve existir SEPARADO do PowerGemUI.cs.
    /// Embora o código também apareça no PowerGemUI.cs (para referência),
    /// o Unity exige um arquivo .cs próprio para MonoBehaviours usados em prefabs.
    ///
    /// PREFAB DO SLOT (hierarquia sugerida):
    ///   GemSlot_Q (Button + GemSlotWidget)
    ///     ├── SlotBackground   (Image — fundo cinza/azul conforme estado)
    ///     ├── GemIcon          (Image — ícone da joia, desativado se vazio)
    ///     ├── HotkeyLabel      (TMP_Text — ex: "[Q]")
    ///     ├── GemNameLabel     (TMP_Text — nome da joia ou "Vazio")
    ///     ├── SelectionBorder  (Image — borda amarela ao selecionar, inativo por padrão)
    ///     └── HighlightOverlay (Image — sobreposição dourada no modo equip)
    ///
    /// SETUP no PowerGemUI Inspector:
    ///   Arraste os 4 GameObjects de slot para os campos slotQ / slotW / slotE / slotR.
    /// </summary>
    public class GemSlotWidget : MonoBehaviour,
        IPointerEnterHandler,
        IPointerExitHandler,
        IPointerClickHandler
    {
        [Header("Referências visuais")]
        [SerializeField] private Image    background;
        [SerializeField] private Image    gemIcon;
        [SerializeField] private TMP_Text hotkeyLabel;
        [SerializeField] private TMP_Text gemNameLabel;
        [SerializeField] private Image    selectionBorder;
        [SerializeField] private Image    highlightOverlay;

        [Header("Cores")]
        [SerializeField] private Color emptyColor     = new Color(0.12f, 0.12f, 0.15f, 0.9f);
        [SerializeField] private Color filledColor    = new Color(0.10f, 0.20f, 0.30f, 0.95f);
        [SerializeField] private Color highlightColor = new Color(1f, 0.85f, 0.2f, 0.25f);

        // ── Callbacks (atribuídos pelo PowerGemUI) ─────────────────────────
        public System.Action OnClicked;
        public System.Action OnHoverEnter;
        public System.Action OnHoverExit;

        // ── Estado interno ─────────────────────────────────────────────────
        private bool     _hasGem    = false;
        private bool     _selected  = false;
        private bool     _highlight = false;

        // ── Lifecycle ──────────────────────────────────────────────────────

        private void Awake()
        {
            // Garante estado inicial limpo
            SetGem(null, null);
            SetSelected(false);
            SetHighlight(false);
        }

        // ── API pública ────────────────────────────────────────────────────

        /// <summary>Define o texto do hotkey visível no slot (ex: "[Q]").</summary>
        public void SetHotkeyLabel(string label)
        {
            if (hotkeyLabel != null)
                hotkeyLabel.text = label;
        }

        /// <summary>
        /// Atualiza o visual do slot com os dados do item equipado.
        /// Passe item = null e itemId = null para limpar o slot.
        /// </summary>
        public void SetGem(ItemData item, string itemId)
        {
            _hasGem = item != null && !string.IsNullOrEmpty(itemId);

            // Fundo
            if (background != null)
                background.color = _hasGem ? filledColor : emptyColor;

            // Ícone
            if (gemIcon != null)
            {
                gemIcon.gameObject.SetActive(_hasGem);
                if (_hasGem && item.Icon != null)
                    gemIcon.sprite = item.Icon;
            }

            // Nome
            if (gemNameLabel != null)
            {
                if (_hasGem)
                {
                    gemNameLabel.text  = item.DisplayName;
                    gemNameLabel.color = item.RarityColor;
                }
                else
                {
                    gemNameLabel.text  = "<color=#555>Vazio</color>";
                    gemNameLabel.color = Color.white;
                }
            }
        }

        /// <summary>Ativa/desativa a borda de seleção.</summary>
        public void SetSelected(bool selected)
        {
            _selected = selected;
            if (selectionBorder != null)
                selectionBorder.gameObject.SetActive(selected);
        }

        /// <summary>Ativa/desativa o overlay de destaque (usado no modo equip).</summary>
        public void SetHighlight(bool highlight)
        {
            _highlight = highlight;
            if (highlightOverlay != null)
            {
                highlightOverlay.gameObject.SetActive(highlight);
                if (highlight)
                    highlightOverlay.color = highlightColor;
            }
        }

        /// <summary>Retorna true se este slot tem uma joia equipada.</summary>
        public bool HasGem => _hasGem;

        // ── Eventos de ponteiro ────────────────────────────────────────────

        public void OnPointerClick(PointerEventData eventData)
            => OnClicked?.Invoke();

        public void OnPointerEnter(PointerEventData eventData)
            => OnHoverEnter?.Invoke();

        public void OnPointerExit(PointerEventData eventData)
            => OnHoverExit?.Invoke();
    }
}