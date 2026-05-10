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
    /// InventoryUI v3
    ///
    /// CORREÇÕES v3:
    ///
    ///   BUG-24 — OnDestroy só cancelava InvokeRepeating se _bindRetrying==true:
    ///     Se StopBindRetry() fosse chamado com a flag false por qualquer razão,
    ///     o InvokeRepeating continuava rodando mesmo após o objeto ser destruído.
    ///     SOLUÇÃO: OnDestroy() sempre chama CancelInvoke(nameof(RetryBind))
    ///     independente de _bindRetrying, garantindo limpeza completa.
    ///
    ///   Todas as correções v2 mantidas:
    ///     - TryBindInventory() sem polling no Update().
    ///     - RefreshAll() copia SyncList uma vez para evitar enumeração dupla.
    ///     - EnsurePoolSize() só expande, não destrói.
    ///     - DeselectAll() limpa _selectedSlot antes de SetSelected(false).
    /// </summary>
    public class InventoryUI : MonoBehaviour
    {
        public static InventoryUI Instance { get; private set; }

        [Header("Painel raiz")]
        [SerializeField] private GameObject inventoryPanel;
        [SerializeField] private Button     closeButton;
        [SerializeField] private TMP_Text   titleText;
        [SerializeField] private TMP_Text   itemCountText;

        [Header("Grid de slots")]
        [SerializeField] private Transform  slotsContainer;
        [SerializeField] private GameObject slotPrefab;

        [Header("Painel de ação (ativo ao selecionar item)")]
        [SerializeField] private GameObject actionPanel;
        [SerializeField] private TMP_Text   actionItemNameText;
        [SerializeField] private TMP_Text   actionItemDescText;
        [SerializeField] private Image      actionItemIcon;
        [SerializeField] private Button     useButton;
        [SerializeField] private Button     equipGemButton;
        [SerializeField] private Button     discardButton;
        [SerializeField] private TMP_Text   useButtonLabel;

        // ── Estado ─────────────────────────────────────────────────────────
        private NetworkInventory              _inventory;
        private bool                          _isOpen    = false;
        private InventorySlotUI               _selectedSlot;
        private readonly List<InventorySlotUI> _slotPool = new List<InventorySlotUI>();
        private bool                          _bindRetrying = false;

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

            if (!TryBindInventory())
                StartBindRetry();
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

            StopBindRetry();

            if (_isOpen) RefreshAll();
            Debug.Log("[InventoryUI] Vinculado ao NetworkInventory.");
        }

        private bool TryBindInventory()
        {
            if (_inventory != null) return true;
            if (NetworkClient.localPlayer == null) return false;

            var inv = NetworkClient.localPlayer.GetComponent<NetworkInventory>();
            if (inv == null) return false;

            BindInventory(inv);
            return true;
        }

        private void StartBindRetry()
        {
            if (_bindRetrying) return;
            _bindRetrying = true;
            InvokeRepeating(nameof(RetryBind), 0.5f, 0.5f);
        }

        private void StopBindRetry()
        {
            _bindRetrying = false;
            CancelInvoke(nameof(RetryBind));
        }

        private void RetryBind()
        {
            if (TryBindInventory())
                StopBindRetry();
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

            EnsurePoolSize(slots.Count);

            for (int i = 0; i < _slotPool.Count; i++)
                _slotPool[i].gameObject.SetActive(false);

            for (int i = 0; i < slots.Count; i++)
            {
                var slotData = slots[i];
                var itemData = ItemDatabase.Instance?.GetItem(slotData.ItemId);

                _slotPool[i].gameObject.SetActive(true);
                _slotPool[i].Setup(slotData, itemData);
            }

            if (itemCountText != null)
                itemCountText.text = $"{slots.Count} iten{(slots.Count != 1 ? "s" : "")}";

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

            DeselectAll();

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
            if (_selectedSlot == null) return;
            var toDeselect = _selectedSlot;
            _selectedSlot  = null;
            toDeselect.SetSelected(false);
        }

        // ── Painel de ação ─────────────────────────────────────────────────

        private void ShowActionPanel(ItemData itemData, InventorySlotData slotData)
        {
            if (actionPanel == null || itemData == null) return;
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

            bool isConsumable = itemData.IsConsumable;
            bool isGem        = itemData.IsPowerGem;

            if (useButton != null)
            {
                useButton.gameObject.SetActive(isConsumable);
                if (useButtonLabel != null) useButtonLabel.text = "Usar";
            }

            if (equipGemButton != null)
                equipGemButton.gameObject.SetActive(isGem);

            if (discardButton != null)
                discardButton.gameObject.SetActive(true);
        }

        // ── Ações ──────────────────────────────────────────────────────────

        private void OnUseClicked()
        {
            if (_selectedSlot == null || _selectedSlot.IsEmpty || _inventory == null) return;

            _inventory.CmdUseConsumable(_selectedSlot.SlotData.SlotIndex);

            DeselectAll();
            if (actionPanel != null) actionPanel.SetActive(false);
        }

        private void OnEquipGemClicked()
        {
            if (_selectedSlot == null || _selectedSlot.IsEmpty) return;
            if (!_selectedSlot.ItemData.IsPowerGem) return;

            PowerGemUI.Instance?.OpenForEquip(_selectedSlot.SlotData);
            Close();
        }

        private void OnDiscardClicked()
        {
            if (_selectedSlot == null || _selectedSlot.IsEmpty || _inventory == null) return;

            _inventory.CmdRemoveItem(_selectedSlot.SlotData.SlotIndex);

            DeselectAll();
            if (actionPanel != null) actionPanel.SetActive(false);
        }

        // ── Cleanup ────────────────────────────────────────────────────────

        private void OnDestroy()
        {
            // BUG-24 CORRIGIDO: sempre cancela, independente de _bindRetrying
            CancelInvoke(nameof(RetryBind));
            _bindRetrying = false;

            if (_inventory != null)
                _inventory.OnInventoryChanged -= OnInventoryChanged;
        }
    }
}
