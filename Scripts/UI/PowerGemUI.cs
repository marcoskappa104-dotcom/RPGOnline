using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Mirror;
using RPG.Data;
using RPG.Network;
using System.Collections;

namespace RPG.UI
{
    /// <summary>
    /// PowerGemUI v3 — Janela de encaixe das Joias do Poder (tecla P).
    ///
    /// CORREÇÕES v3:
    ///
    ///   1. MODO BROWSE NÃO FUNCIONAVA:
    ///      TryBindInventory() em Start() falhava silenciosamente porque o
    ///      NetworkPlayer spawna DEPOIS que a UI é criada em modo multiplayer.
    ///      Solução: retry via coroutine em OpenBrowse() e OpenForEquip(),
    ///      além de aceitar bind tardio via BindInventory() chamado pelo
    ///      NetworkInventory.OnStartLocalPlayer e pelo UIManager.BindLocalPlayer.
    ///
    ///   2. DESEQUIPAR JOIA:
    ///      O modo Browse agora funciona corretamente. Ao pressionar P:
    ///        - Slots com joia ficam clicáveis → mostra botão "Retirar joia".
    ///        - Slots vazios ficam cinzas e não reagem ao clique.
    ///        - Confirmar desequipa via CmdUnequipGem.
    ///
    ///   3. HIGHLIGHT NO MODO EQUIP:
    ///      Ao vir do InventoryUI, todos os slots ficam destacados em dourado
    ///      indicando que o jogador deve escolher onde encaixar a joia.
    ///      Após escolher, o highlight é removido e a UI fecha.
    ///
    ///   4. TOOLTIP:
    ///      Fecha junto com a UI (Close()) para evitar tooltip "preso" na tela.
    ///
    ///   5. VALIDAÇÃO DE ITEM:
    ///      OpenForEquip() verifica se a joia ainda existe no inventário antes
    ///      de abrir, evitando modo equip com slot inválido.
    ///
    /// FUNCIONALIDADES:
    ///   - 4 slots visuais: Q, W, E, R — mostram joia equipada ou slot vazio.
    ///   - Modo "equip" (vem do InventoryUI): clique no slot desejado para equipar.
    ///   - Modo "browse" (tecla P): clique em slot com joia para desequipar.
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
    ///           └── UnequipButton (só aparece quando slot com joia é selecionado)
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

            // Tenta vincular imediatamente (funciona em modo Host/Editor)
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
        /// e pelo UIManager.BindLocalPlayer.
        ///
        /// CORREÇÃO v3: aceita bind tardio. Em multiplayer, o player spawna depois
        /// da UI, então este método pode ser chamado a qualquer momento.
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

        /// <summary>
        /// Tenta vincular ao NetworkInventory do player local.
        /// Retorna true se conseguiu vincular.
        /// </summary>
        private bool TryBindInventory()
        {
            if (_inventory != null) return true;
            if (NetworkClient.localPlayer == null) return false;

            var inv = NetworkClient.localPlayer.GetComponent<NetworkInventory>();
            if (inv == null) return false;

            BindInventory(inv);
            return true;
        }

        /// <summary>
        /// CORREÇÃO v3: coroutine de retry para quando a UI abre antes do player spawnar.
        /// Tenta vincular a cada 0.2s por até 10s.
        /// </summary>
        private IEnumerator WaitAndBind()
        {
            float elapsed = 0f;
            while (_inventory == null && elapsed < 10f)
            {
                yield return new WaitForSeconds(0.2f);
                elapsed += 0.2f;
                TryBindInventory();
            }

            if (_inventory == null)
            {
                Debug.LogWarning("[PowerGemUI] Não foi possível vincular ao NetworkInventory após 10s.");
            }
            else if (_isOpen)
            {
                RefreshSlots();
            }
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

        /// <summary>
        /// Abre em modo "visualizar/desequipar" (tecla P).
        ///
        /// CORREÇÃO v3: se _inventory ainda for null (player ainda não spawnado),
        /// inicia coroutine de retry para tentar vincular enquanto a UI fica aberta.
        /// </summary>
        public void OpenBrowse()
        {
            _equipMode            = false;
            _pendingGemSlot       = default;
            _selectedGemSlotIndex = -1;

            if (titleText       != null) titleText.text       = "Joias do Poder";
            if (instructionText != null) instructionText.text =
                "Clique em um slot com joia para removê-la.";

            _isOpen = true;
            if (panel         != null) panel.SetActive(true);
            if (unequipButton != null) unequipButton.gameObject.SetActive(false);

            // Garante que os slots fiquem sem highlight no modo browse
            HighlightAllSlots(false);

            if (!TryBindInventory())
            {
                StartCoroutine(WaitAndBind());
                if (instructionText != null)
                    instructionText.text = "Conectando ao inventário...";
                return;
            }

            RefreshSlots();
        }

        /// <summary>
        /// Abre em modo "equipar" — chamado pelo InventoryUI com a joia selecionada.
        ///
        /// CORREÇÃO v3: retry se _inventory for null, igual ao OpenBrowse.
        /// </summary>
        public void OpenForEquip(InventorySlotData gemSlotData)
        {
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

            if (!TryBindInventory())
            {
                StartCoroutine(WaitAndBind());
                return;
            }

            RefreshSlots();
            HighlightAllSlots(true);
        }

        public void Close()
        {
            _isOpen               = false;
            _equipMode            = false;
            _selectedGemSlotIndex = -1;
            HighlightAllSlots(false);

            // Limpa seleção visual em todos os slots
            slotQ?.SetSelected(false);
            slotW?.SetSelected(false);
            slotE?.SetSelected(false);
            slotR?.SetSelected(false);

            if (panel         != null) panel.SetActive(false);
            if (unequipButton != null) unequipButton.gameObject.SetActive(false);

            // Fecha tooltip junto para evitar tooltip "preso"
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

            // Atualiza instrução baseado no estado atual dos slots
            if (!_equipMode && instructionText != null)
            {
                bool anyGem = false;
                for (int i = 0; i < 4; i++)
                    if (!string.IsNullOrEmpty(_inventory.GetGemItemId(i))) { anyGem = true; break; }

                instructionText.text = anyGem
                    ? "Clique em um slot com joia para removê-la."
                    : "Nenhuma joia equipada.\nAbra o inventário (I) para equipar.";
            }
        }

        private void RefreshSlotWidget(GemSlotWidget widget, int slotIndex)
        {
            if (widget == null || _inventory == null) return;

            string gemId   = _inventory.GetGemItemId(slotIndex);
            bool   isEmpty = string.IsNullOrEmpty(gemId);
            var    item    = isEmpty ? null : ItemDatabase.Instance?.GetItem(gemId);

            widget.SetGem(item, isEmpty ? null : gemId);
            widget.SetSelected(slotIndex == _selectedGemSlotIndex);

            // No modo equip, mantém o highlight em todos os slots
            if (_equipMode)
                widget.SetHighlight(true);
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
                // ── MODO EQUIP: encaixa a joia pendente neste slot ──────────
                _inventory.CmdEquipGem(slotIndex, _pendingGemSlot.SlotIndex);
                HighlightAllSlots(false);
                Close();
                UIManager.Instance?.ShowMessage($"Joia equipada no slot {SlotNames[slotIndex]}!");
            }
            else
            {
                // ── MODO BROWSE: seleciona para desequipar ──────────────────
                string gemId  = _inventory.GetGemItemId(slotIndex);
                bool   hasGem = !string.IsNullOrEmpty(gemId);

                if (hasGem)
                {
                    // Seleciona este slot (deseleciona o anterior)
                    _selectedGemSlotIndex = slotIndex;
                    RefreshSlots();

                    if (unequipButton != null)
                    {
                        unequipButton.gameObject.SetActive(true);
                        if (unequipButtonLabel != null)
                            unequipButtonLabel.text = $"Retirar joia do slot {SlotNames[slotIndex]}";
                    }

                    // Atualiza instrução
                    if (instructionText != null)
                    {
                        var item = ItemDatabase.Instance?.GetItem(gemId);
                        string name = item?.DisplayName ?? "joia";
                        instructionText.text = $"Slot {SlotNames[slotIndex]}: <color=#FFD700>{name}</color>\nClique em \"Retirar\" para desequipar.";
                    }
                }
                else
                {
                    // Slot vazio: cancela seleção
                    _selectedGemSlotIndex = -1;
                    RefreshSlots();
                    if (unequipButton != null) unequipButton.gameObject.SetActive(false);

                    if (instructionText != null)
                        instructionText.text = "Este slot está vazio. Clique em um slot com joia.";
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

            string slotName = SlotNames[_selectedGemSlotIndex];

            // Pega o nome da joia antes de desequipar (para o feedback)
            string gemId  = _inventory.GetGemItemId(_selectedGemSlotIndex);
            var    item   = string.IsNullOrEmpty(gemId) ? null : ItemDatabase.Instance?.GetItem(gemId);
            string gemName = item?.DisplayName ?? "joia";

            _inventory.CmdUnequipGem(_selectedGemSlotIndex);

            UIManager.Instance?.ShowMessage($"{gemName} removida do slot {slotName}.");
            Debug.Log($"[PowerGemUI] Desequipando slot {slotName}.");

            // Reseta seleção e atualiza visual
            _selectedGemSlotIndex = -1;
            if (unequipButton != null) unequipButton.gameObject.SetActive(false);

            if (instructionText != null)
                instructionText.text = "Clique em um slot com joia para removê-la.";

            // RefreshSlots() será chamado automaticamente via OnLoadoutChanged
            // quando o SyncVar atualizar, mas chamamos aqui para resposta imediata
            RefreshSlots();
        }

        // ── Cleanup ────────────────────────────────────────────────────────

        private void OnDestroy()
        {
            if (_inventory != null)
                _inventory.OnGemLoadoutChanged -= OnLoadoutChanged;
        }
    }
}