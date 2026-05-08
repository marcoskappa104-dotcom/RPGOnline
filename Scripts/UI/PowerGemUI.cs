using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Mirror;
using RPG.Data;
using RPG.Network;

namespace RPG.UI
{
    /// <summary>
    /// PowerGemUI v2 — Janela de encaixe das Joias do Poder (tecla P).
    ///
    /// MUDANÇAS v2:
    ///   - GemSlotWidget movido para arquivo separado GemSlotWidget.cs.
    ///     (O Unity exige arquivo próprio para MonoBehaviours usados em prefabs.)
    ///   - TryBindInventory não é mais chamado no Update() — agora usa evento
    ///     OnStartLocalPlayer via NetworkInventory para evitar custo por frame.
    ///   - Close() agora também esconde o tooltip para evitar tooltip preso.
    ///   - OpenForEquip() verifica se o item ainda existe no inventário antes
    ///     de abrir, evitando modo equip com joia inválida.
    ///
    /// FUNCIONALIDADES:
    ///   - 4 slots visuais: Q, W, E, R — mostram joia equipada ou slot vazio.
    ///   - Modo "equip": aberto pelo InventoryUI ao clicar em uma joia.
    ///     O jogador clica no slot desejado (Q/W/E/R) para equipar.
    ///   - Modo "browse": aberto pela tecla P para ver o loadout atual.
    ///     Clicar em um slot equipado abre opção de desequipar.
    ///   - Tooltip ao passar o mouse (via ItemTooltipUI).
    ///
    /// SETUP DA CENA (hierarquia sugerida):
    ///   PowerGemCanvas (Canvas ScreenSpace-Overlay, Sort Order 55)
    ///     └── PowerGemPanel (Panel + PowerGemUI)
    ///           ├── Header
    ///           │   ├── TitleText ("Joias do Poder")
    ///           │   └── CloseButton
    ///           ├── InstructionText (TMP_Text — muda conforme modo)
    ///           ├── SlotsRow (horizontal layout)
    ///           │   ├── GemSlot_Q  (Button + GemSlotWidget)
    ///           │   ├── GemSlot_W  (Button + GemSlotWidget)
    ///           │   ├── GemSlot_E  (Button + GemSlotWidget)
    ///           │   └── GemSlot_R  (Button + GemSlotWidget)
    ///           └── UnequipButton (só aparece quando slot está selecionado)
    /// </summary>
    public class PowerGemUI : MonoBehaviour
    {
        public static PowerGemUI Instance { get; private set; }

        [Header("Painel raiz")]
        [SerializeField] private GameObject panel;
        [SerializeField] private Button     closeButton;
        [SerializeField] private TMP_Text   titleText;
        [SerializeField] private TMP_Text   instructionText;

        [Header("Slots de Joia (ordem: Q, W, E, R)")]
        [SerializeField] private GemSlotWidget slotQ;
        [SerializeField] private GemSlotWidget slotW;
        [SerializeField] private GemSlotWidget slotE;
        [SerializeField] private GemSlotWidget slotR;

        [Header("Ações")]
        [SerializeField] private Button   unequipButton;
        [SerializeField] private TMP_Text unequipButtonLabel;

        // ── Estado ─────────────────────────────────────────────────────────
        private NetworkInventory  _inventory;
        private bool              _isOpen    = false;

        // Modo equip: quando vem do InventoryUI com uma joia selecionada
        private bool              _equipMode = false;
        private InventorySlotData _pendingGemSlot;

        // Slot selecionado no modo browse (para desequipar)
        private int _selectedGemSlotIndex = -1;

        private static readonly string[] SlotNames  = { "Q", "W", "E", "R" };
        private static readonly string[] SlotLabels = { "[Q]", "[W]", "[E]", "[R]" };

        // ── Lifecycle ──────────────────────────────────────────────────────

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        private void Start()
        {
            if (panel != null) panel.SetActive(false);

            if (closeButton   != null) closeButton.onClick.AddListener(Close);
            if (unequipButton != null)
            {
                unequipButton.onClick.AddListener(OnUnequipClicked);
                unequipButton.gameObject.SetActive(false);
            }

            // Configura callbacks dos slots
            SetupSlotWidget(slotQ, 0);
            SetupSlotWidget(slotW, 1);
            SetupSlotWidget(slotE, 2);
            SetupSlotWidget(slotR, 3);

            // Tenta vincular se o player já existe (modo Host/Editor)
            TryBindInventory();
        }

        private void SetupSlotWidget(GemSlotWidget widget, int slotIndex)
        {
            if (widget == null) return;
            widget.SetHotkeyLabel(SlotLabels[slotIndex]);
            widget.OnClicked    = () => OnGemSlotClicked(slotIndex);
            widget.OnHoverEnter = () => OnGemSlotHoverEnter(slotIndex);
            widget.OnHoverExit  = () => ItemTooltipUI.Instance?.Hide();
        }

        // ── Vínculo com NetworkInventory ───────────────────────────────────

        /// <summary>
        /// Chamado pelo NetworkInventory.OnStartLocalPlayer (via BindUIDelayed)
        /// e pelo UIManager.BindLocalPlayer. Evita chamada em Update().
        /// </summary>
        public void BindInventory(NetworkInventory inventory)
        {
            if (inventory == null || _inventory == inventory) return;

            if (_inventory != null)
                _inventory.OnGemLoadoutChanged -= OnLoadoutChanged;

            _inventory = inventory;
            _inventory.OnGemLoadoutChanged += OnLoadoutChanged;

            if (_isOpen) RefreshSlots();
            Debug.Log("[PowerGemUI] Vinculado ao NetworkInventory.");
        }

        private void TryBindInventory()
        {
            if (_inventory != null) return;
            if (NetworkClient.localPlayer == null) return;

            var inv = NetworkClient.localPlayer.GetComponent<NetworkInventory>();
            if (inv != null) BindInventory(inv);
        }

        private void OnLoadoutChanged()
        {
            if (_isOpen) RefreshSlots();
        }

        // ── Abrir / Fechar ─────────────────────────────────────────────────

        public void Toggle()
        {
            if (_isOpen) Close();
            else         OpenBrowse();
        }

        /// <summary>Abre em modo "visualizar/desequipar".</summary>
        public void OpenBrowse()
        {
            TryBindInventory();
            _equipMode            = false;
            _pendingGemSlot       = default;
            _selectedGemSlotIndex = -1;

            if (titleText       != null) titleText.text       = "Joias do Poder";
            if (instructionText != null) instructionText.text = "Clique em um slot para desequipar a joia.";

            _isOpen = true;
            if (panel         != null) panel.SetActive(true);
            if (unequipButton != null) unequipButton.gameObject.SetActive(false);
            RefreshSlots();
        }

        /// <summary>
        /// Abre em modo "equipar" — chamado pelo InventoryUI com a joia selecionada.
        /// </summary>
        public void OpenForEquip(InventorySlotData gemSlotData)
        {
            TryBindInventory();

            // Valida que o item ainda existe no inventário
            if (_inventory != null)
            {
                bool found = false;
                foreach (var s in _inventory.Slots)
                    if (s.SlotIndex == gemSlotData.SlotIndex) { found = true; break; }
                if (!found)
                {
                    Debug.LogWarning("[PowerGemUI] OpenForEquip: slot não encontrado no inventário.");
                    return;
                }
            }

            _equipMode            = true;
            _pendingGemSlot       = gemSlotData;
            _selectedGemSlotIndex = -1;

            var itemData = ItemDatabase.Instance?.GetItem(gemSlotData.ItemId);
            string gemName = itemData?.DisplayName ?? "Joia";

            if (titleText       != null) titleText.text       = "Equipar Joia";
            if (instructionText != null) instructionText.text =
                $"Escolha o slot para equipar:\n<color=#FFD700>{gemName}</color>";

            _isOpen = true;
            if (panel         != null) panel.SetActive(true);
            if (unequipButton != null) unequipButton.gameObject.SetActive(false);
            RefreshSlots();
            HighlightAllSlots(true);
        }

        public void Close()
        {
            _isOpen               = false;
            _equipMode            = false;
            _selectedGemSlotIndex = -1;
            HighlightAllSlots(false);

            if (panel         != null) panel.SetActive(false);
            if (unequipButton != null) unequipButton.gameObject.SetActive(false);

            // Garante que o tooltip feche junto
            ItemTooltipUI.Instance?.Hide();
        }

        // ── Refresh visual ─────────────────────────────────────────────────

        private void RefreshSlots()
        {
            if (_inventory == null) return;

            RefreshSlotWidget(slotQ, 0);
            RefreshSlotWidget(slotW, 1);
            RefreshSlotWidget(slotE, 2);
            RefreshSlotWidget(slotR, 3);
        }

        private void RefreshSlotWidget(GemSlotWidget widget, int slotIndex)
        {
            if (widget == null || _inventory == null) return;

            string gemId   = _inventory.GetGemItemId(slotIndex);
            bool   isEmpty = string.IsNullOrEmpty(gemId);
            var    item    = isEmpty ? null : ItemDatabase.Instance?.GetItem(gemId);

            widget.SetGem(item, isEmpty ? null : gemId);
            widget.SetSelected(slotIndex == _selectedGemSlotIndex);
        }

        private void HighlightAllSlots(bool highlight)
        {
            slotQ?.SetHighlight(highlight);
            slotW?.SetHighlight(highlight);
            slotE?.SetHighlight(highlight);
            slotR?.SetHighlight(highlight);
        }

        // ── Eventos de slot ────────────────────────────────────────────────

        private void OnGemSlotClicked(int slotIndex)
        {
            if (_inventory == null) return;

            if (_equipMode)
            {
                // Equipa a joia pendente neste slot
                _inventory.CmdEquipGem(slotIndex, _pendingGemSlot.SlotIndex);
                HighlightAllSlots(false);
                Close();
                UIManager.Instance?.ShowMessage($"Joia equipada no slot {SlotNames[slotIndex]}!");
            }
            else
            {
                // Modo browse: seleciona para desequipar
                string gemId  = _inventory.GetGemItemId(slotIndex);
                bool   hasGem = !string.IsNullOrEmpty(gemId);

                if (hasGem)
                {
                    _selectedGemSlotIndex = slotIndex;
                    RefreshSlots();

                    if (unequipButton != null)
                    {
                        unequipButton.gameObject.SetActive(true);
                        if (unequipButtonLabel != null)
                            unequipButtonLabel.text = $"Retirar joia do slot {SlotNames[slotIndex]}";
                    }
                }
                else
                {
                    _selectedGemSlotIndex = -1;
                    RefreshSlots();
                    if (unequipButton != null) unequipButton.gameObject.SetActive(false);
                }
            }
        }

        private void OnGemSlotHoverEnter(int slotIndex)
        {
            if (_inventory == null) return;
            string gemId = _inventory.GetGemItemId(slotIndex);
            if (string.IsNullOrEmpty(gemId)) return;
            var item = ItemDatabase.Instance?.GetItem(gemId);
            if (item != null) ItemTooltipUI.Instance?.Show(item);
        }

        private void OnUnequipClicked()
        {
            if (_selectedGemSlotIndex < 0 || _inventory == null) return;
            _inventory.CmdUnequipGem(_selectedGemSlotIndex);
            UIManager.Instance?.ShowMessage($"Joia removida do slot {SlotNames[_selectedGemSlotIndex]}.");
            _selectedGemSlotIndex = -1;
            if (unequipButton != null) unequipButton.gameObject.SetActive(false);
            RefreshSlots();
        }

        private void OnDestroy()
        {
            if (_inventory != null)
                _inventory.OnGemLoadoutChanged -= OnLoadoutChanged;
        }
    }
}