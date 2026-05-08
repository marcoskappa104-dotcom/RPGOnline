using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Mirror;
using RPG.Data;
using RPG.Network;
using System.Collections.Generic;

namespace RPG.UI
{
    /// <summary>
    /// InventoryUI v1 — Janela de inventário do jogador.
    ///
    /// FUNCIONALIDADES:
    ///   - Grid dinâmico de slots (sem limite).
    ///   - Slot selecionável com painel de ação (Usar / Equipar Joia / Descartar).
    ///   - Tooltip ao passar o mouse (via ItemTooltipUI).
    ///   - Tecla I (ou botão X) fecha a janela.
    ///   - Atualiza automaticamente quando o SyncList muda (OnInventoryChanged).
    ///
    /// SETUP DA CENA (hierarquia sugerida):
    ///   InventoryCanvas (Canvas ScreenSpace-Overlay, Sort Order 50)
    ///     └── InventoryPanel (Panel + InventoryUI)
    ///           ├── Header
    ///           │   ├── TitleText ("Inventário")
    ///           │   └── CloseButton (X)
    ///           ├── ScrollView
    ///           │   └── Viewport
    ///           │       └── Content (GridLayoutGroup + ContentSizeFitter)
    ///           └── ActionPanel (aparece ao selecionar item)
    ///               ├── ActionItemName (TMP_Text)
    ///               ├── UseButton   ("Usar" — para consumíveis)
    ///               ├── EquipButton ("Equipar Joia" — abre PowerGemUI)
    ///               └── DiscardButton ("Descartar")
    ///
    /// GRID LAYOUT GROUP settings:
    ///   Cell Size: 60x60   Spacing: 4x4   Constraint: Fixed Column Count = 6
    /// </summary>
    public class InventoryUI : MonoBehaviour
    {
        public static InventoryUI Instance { get; private set; }

        [Header("Painel raiz")]
        [SerializeField] private GameObject    inventoryPanel;
        [SerializeField] private Button        closeButton;
        [SerializeField] private TMP_Text      titleText;
        [SerializeField] private TMP_Text      itemCountText;

        [Header("Grid de slots")]
        [SerializeField] private Transform     slotsContainer;
        [SerializeField] private GameObject    slotPrefab;

        [Header("Painel de ação (ativo ao selecionar item)")]
        [SerializeField] private GameObject    actionPanel;
        [SerializeField] private TMP_Text      actionItemNameText;
        [SerializeField] private TMP_Text      actionItemDescText;
        [SerializeField] private Image         actionItemIcon;
        [SerializeField] private Button        useButton;
        [SerializeField] private Button        equipGemButton;
        [SerializeField] private Button        discardButton;
        [SerializeField] private TMP_Text      useButtonLabel;

        // ── Estado ─────────────────────────────────────────────────────────
        private NetworkInventory           _inventory;
        private bool                       _isOpen = false;
        private InventorySlotUI            _selectedSlot;
        private List<InventorySlotUI>      _slotPool = new List<InventorySlotUI>();

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        private void Start()
        {
            if (inventoryPanel != null) inventoryPanel.SetActive(false);
            if (actionPanel    != null) actionPanel.SetActive(false);

            if (closeButton    != null) closeButton.onClick.AddListener(Close);
            if (useButton      != null) useButton.onClick.AddListener(OnUseClicked);
            if (equipGemButton != null) equipGemButton.onClick.AddListener(OnEquipGemClicked);
            if (discardButton  != null) discardButton.onClick.AddListener(OnDiscardClicked);

            if (titleText != null) titleText.text = "Inventário";

            // Tenta vincular ao inventário do player local
            TryBindInventory();
        }

        // ── Vínculo com NetworkInventory ───────────────────────────────────

        public void BindInventory(NetworkInventory inventory)
        {
            if (inventory == null) return;
            if (_inventory == inventory) return;

            if (_inventory != null)
                _inventory.OnInventoryChanged -= OnInventoryChanged;

            _inventory = inventory;
            _inventory.OnInventoryChanged += OnInventoryChanged;

            if (_isOpen) RefreshAll();
            Debug.Log("[InventoryUI] Vinculado ao NetworkInventory.");
        }

        private void TryBindInventory()
        {
            if (_inventory != null) return;
            if (NetworkClient.localPlayer == null) return;

            var inv = NetworkClient.localPlayer.GetComponent<NetworkInventory>();
            if (inv != null) BindInventory(inv);
        }

        private void Update()
        {
            // Tenta vincular se ainda não vinculou
            if (_inventory == null) TryBindInventory();
        }

        private void OnInventoryChanged()
        {
            if (_isOpen) RefreshAll();
        }

        // ── Abrir / Fechar ─────────────────────────────────────────────────

        public void Toggle() { if (_isOpen) Close(); else Open(); }

        public void Open()
        {
            TryBindInventory();
            _isOpen = true;
            if (inventoryPanel != null) inventoryPanel.SetActive(true);
            RefreshAll();
        }

        public void Close()
        {
            _isOpen = false;
            if (inventoryPanel != null) inventoryPanel.SetActive(false);
            if (actionPanel    != null) actionPanel.SetActive(false);
            ItemTooltipUI.Instance?.Hide();
            DeselectAll();
        }

        // ── Refresh ────────────────────────────────────────────────────────

        private void RefreshAll()
        {
            if (_inventory == null) return;

            var slots = new List<InventorySlotData>(_inventory.Slots);

            // Ajusta o pool de slots UI
            EnsurePoolSize(slots.Count);

            // Esconde todos primeiro
            for (int i = 0; i < _slotPool.Count; i++)
                _slotPool[i].gameObject.SetActive(false);

            // Popula com dados reais
            for (int i = 0; i < slots.Count; i++)
            {
                var slotData = slots[i];
                var itemData = ItemDatabase.Instance?.GetItem(slotData.ItemId);

                _slotPool[i].gameObject.SetActive(true);
                _slotPool[i].Setup(slotData, itemData);
            }

            // Contador
            if (itemCountText != null)
                itemCountText.text = $"{slots.Count} iten{(slots.Count != 1 ? "s" : "")}";

            // Se o slot selecionado não existe mais, limpa
            if (_selectedSlot != null && _selectedSlot.IsEmpty)
            {
                DeselectAll();
                if (actionPanel != null) actionPanel.SetActive(false);
            }
        }

        private void EnsurePoolSize(int requiredCount)
        {
            if (slotsContainer == null || slotPrefab == null) return;

            while (_slotPool.Count < requiredCount)
            {
                var go   = Instantiate(slotPrefab, slotsContainer);
                var slot = go.GetComponent<InventorySlotUI>();

                if (slot == null)
                {
                    Debug.LogError("[InventoryUI] slotPrefab não tem InventorySlotUI!");
                    Destroy(go);
                    break;
                }

                slot.OnSlotClicked    += OnSlotClicked;
                slot.OnSlotHoverEnter += OnSlotHoverEnter;
                slot.OnSlotHoverExit  += OnSlotHoverExit;

                _slotPool.Add(slot);
            }
        }

        // ── Eventos de slot ────────────────────────────────────────────────

        private void OnSlotClicked(InventorySlotUI slot)
        {
            if (slot == null || slot.IsEmpty) return;

            // Deseleciona anterior
            DeselectAll();

            // Seleciona novo
            _selectedSlot = slot;
            slot.SetSelected(true);

            ShowActionPanel(slot.ItemData, slot.SlotData);
        }

        private void OnSlotHoverEnter(InventorySlotUI slot)
        {
            if (slot == null || slot.IsEmpty) return;
            ItemTooltipUI.Instance?.Show(slot.ItemData);
        }

        private void OnSlotHoverExit(InventorySlotUI slot)
        {
            ItemTooltipUI.Instance?.Hide();
        }

        private void DeselectAll()
        {
            if (_selectedSlot != null)
            {
                _selectedSlot.SetSelected(false);
                _selectedSlot = null;
            }
        }

        // ── Painel de ação ─────────────────────────────────────────────────

        private void ShowActionPanel(ItemData itemData, InventorySlotData slotData)
        {
            if (actionPanel == null) return;
            actionPanel.SetActive(true);

            if (actionItemNameText != null)
            {
                actionItemNameText.text  = itemData.DisplayName;
                actionItemNameText.color = itemData.RarityColor;
            }

            if (actionItemDescText != null)
                actionItemDescText.text = itemData.Description;

            if (actionItemIcon != null)
            {
                actionItemIcon.sprite  = itemData.Icon;
                actionItemIcon.enabled = itemData.Icon != null;
            }

            // Configura botões conforme o tipo do item
            bool isConsumable = itemData.IsConsumable;
            bool isGem        = itemData.IsPowerGem;

            if (useButton != null)
            {
                useButton.gameObject.SetActive(isConsumable);
                if (useButtonLabel != null)
                    useButtonLabel.text = "Usar";
            }

            if (equipGemButton != null)
                equipGemButton.gameObject.SetActive(isGem);

            if (discardButton != null)
                discardButton.gameObject.SetActive(true);
        }

        // ── Ações ──────────────────────────────────────────────────────────

        private void OnUseClicked()
        {
            if (_selectedSlot == null || _selectedSlot.IsEmpty) return;
            if (_inventory == null) return;

            var slotData = _selectedSlot.SlotData;
            _inventory.CmdUseConsumable(slotData.SlotIndex);

            DeselectAll();
            if (actionPanel != null) actionPanel.SetActive(false);
        }

        private void OnEquipGemClicked()
        {
            if (_selectedSlot == null || _selectedSlot.IsEmpty) return;
            if (!_selectedSlot.ItemData.IsPowerGem) return;

            // Abre a janela de joias passando o slot de inventário selecionado
            PowerGemUI.Instance?.OpenForEquip(_selectedSlot.SlotData);
            Close();
        }

        private void OnDiscardClicked()
        {
            if (_selectedSlot == null || _selectedSlot.IsEmpty) return;
            if (_inventory == null) return;

            // Por segurança: apenas remove do inventário no servidor
            // (numa versão futura: adicionar confirmação)
            _inventory.CmdRemoveItem(_selectedSlot.SlotData.SlotIndex);

            DeselectAll();
            if (actionPanel != null) actionPanel.SetActive(false);
        }

        private void OnDestroy()
        {
            if (_inventory != null)
                _inventory.OnInventoryChanged -= OnInventoryChanged;
        }
    }
}