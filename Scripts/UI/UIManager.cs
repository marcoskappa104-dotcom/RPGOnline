using UnityEngine;
using UnityEngine.UI;
using TMPro;
using RPG.Character;
using RPG.Combat;

namespace RPG.UI
{
    /// <summary>
    /// UIManager v9
    ///
    /// CORREÇÃO v9 — Botão do PowerGemUI não abria:
    ///
    ///   CAUSA RAIZ: O GameObject do PowerGemUI (ou seu pai) começava desativado
    ///   no Inspector. Isso impede que o Awake() rode, então PowerGemUI.Instance
    ///   fica null. O onClick do botão captura Instance em runtime, mas se o
    ///   objeto nunca acordou, Instance é null e Toggle() nunca é chamado.
    ///
    ///   SOLUÇÃO: Os botões de HUD (inventoryHudButton, powerGemHudButton) agora
    ///   são registrados em BindLocalPlayer() em vez de Start(), garantindo que
    ///   os singletons já existem quando o jogador entra no jogo.
    ///   Adicionado também um fallback FindObjectOfType para casos onde o
    ///   GameObject estava inativo durante o Start().
    ///
    ///   CORREÇÃO ADICIONAL: PowerGemUI e InventoryUI agora têm seus GameObjects
    ///   raiz SEMPRE ativos na cena — apenas o painel interno (panel/inventoryPanel)
    ///   começa desativado. Isso garante que Awake() rode e Instance seja setado.
    ///   (Instrução de setup adicionada nos comentários abaixo.)
    ///
    /// SETUP OBRIGATÓRIO NO INSPECTOR:
    ///   - O GameObject que tem PowerGemUI.cs DEVE estar ATIVO na hierarquia.
    ///     Apenas o campo [SerializeField] panel (filho interno) deve começar inativo.
    ///   - O mesmo vale para InventoryUI, ItemTooltipUI, FloatingTextManager etc.
    ///   - Se o GameObject raiz estiver inativo, Awake() não roda e Instance = null.
    /// </summary>
    public class UIManager : MonoBehaviour
    {
        public static UIManager Instance { get; private set; }

        [Header("Player HUD")]
        [SerializeField] private Slider   hpBar;
        [SerializeField] private TMP_Text hpText;
        [SerializeField] private Slider   mpBar;
        [SerializeField] private TMP_Text mpText;
        [SerializeField] private TMP_Text playerNameText;
        [SerializeField] private TMP_Text levelText;

        [Header("Target Panel")]
        [SerializeField] private GameObject targetPanel;
        [SerializeField] private TMP_Text   targetNameText;
        [SerializeField] private Slider     targetHPBar;
        [SerializeField] private TMP_Text   targetHPText;

        [Header("Skill Bar")]
        [SerializeField] private SkillSlotUI[] skillSlots;
        [SerializeField] private string[] hotkeyLabels = { "Q", "W", "E", "R" };

        [Header("Message")]
        [SerializeField] private TMP_Text messageText;
        [SerializeField] private float    messageDisplayTime = 2f;

        [Header("Experience")]
        [SerializeField] private Slider   expBar;
        [SerializeField] private TMP_Text expText;

        [Header("Attribute Window")]
        [SerializeField] private AttributeWindowUI attributeWindow;
        [SerializeField] private Button            attributeWindowButton;

        [Header("Atalhos de UI (opcional)")]
        [Tooltip("Botão na HUD que abre o inventário. Pode ser null se usar só a tecla I.")]
        [SerializeField] private Button inventoryHudButton;
        [Tooltip("Botão na HUD que abre as Joias do Poder. Pode ser null se usar só a tecla P.")]
        [SerializeField] private Button powerGemHudButton;

        private PlayerEntity              _player;
        private SkillSystem               _skills;
        private RPG.Network.NetworkPlayer _netPlayer;
        private float                     _messageTimer;

        // Controla se os listeners dos botões de HUD já foram registrados
        // (evita duplicar ao chamar BindLocalPlayer mais de uma vez)
        private bool _hudButtonsRegistered = false;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        private void Start()
        {
            ClearTargetPanel();
            if (messageText != null) messageText.text = "";

            // Botão da janela de atributos (C) — registrado aqui pois AttributeWindowUI
            // geralmente está sempre ativo na cena.
            if (attributeWindowButton != null)
                attributeWindowButton.onClick.AddListener(() => attributeWindow?.Toggle());

            // CORREÇÃO v9: botões de inventário e joias são registrados AQUI
            // apenas como fallback para modo offline/host onde o player já existe.
            // Em multiplayer, RegisterHudButtons() é chamado em BindLocalPlayer()
            // quando os singletons já estão garantidamente inicializados.
            RegisterHudButtonsSafe();

            // Modo offline: tenta vincular ao PlayerEntity que já existe na cena
            var player = FindObjectOfType<PlayerEntity>();
            if (player != null && player.IsInitialized)
                BindLocalPlayer(player);
        }

        /// <summary>
        /// Registra os listeners dos botões de HUD de forma segura.
        /// Verifica se os singletons existem antes de registrar.
        /// Registra no máximo uma vez (guarda flag _hudButtonsRegistered).
        /// </summary>
        private void RegisterHudButtonsSafe()
        {
            if (_hudButtonsRegistered) return;

            bool inventoryReady = InventoryUI.Instance != null;
            bool gemReady       = PowerGemUI.Instance  != null;

            // Fallback: tenta encontrar via FindObjectOfType se Instance for null
            // (acontece quando o GameObject raiz estava inativo durante o Awake da UI)
            if (!inventoryReady)
            {
                var found = FindObjectOfType<InventoryUI>();
                if (found != null)
                    Debug.LogWarning("[UIManager] InventoryUI.Instance era null — encontrado via FindObjectOfType. " +
                                     "Verifique se o GameObject do InventoryUI está ATIVO na hierarquia.");
            }

            if (!gemReady)
            {
                var found = FindObjectOfType<PowerGemUI>();
                if (found != null)
                    Debug.LogWarning("[UIManager] PowerGemUI.Instance era null — encontrado via FindObjectOfType. " +
                                     "Verifique se o GameObject do PowerGemUI está ATIVO na hierarquia.");
            }

            // Registra o botão de inventário
            if (inventoryHudButton != null)
            {
                inventoryHudButton.onClick.RemoveAllListeners();
                inventoryHudButton.onClick.AddListener(() =>
                {
                    if (InventoryUI.Instance != null)
                        InventoryUI.Instance.Toggle();
                    else
                        Debug.LogWarning("[UIManager] InventoryUI.Instance é null ao clicar no botão!");
                });
            }

            // Registra o botão de joias do poder
            if (powerGemHudButton != null)
            {
                powerGemHudButton.onClick.RemoveAllListeners();
                powerGemHudButton.onClick.AddListener(() =>
                {
                    if (PowerGemUI.Instance != null)
                    {
                        PowerGemUI.Instance.Toggle();
                    }
                    else
                    {
                        Debug.LogWarning("[UIManager] PowerGemUI.Instance é null ao clicar no botão! " +
                                         "Certifique-se que o GameObject do PowerGemUI está ATIVO na hierarquia da cena.");
                    }
                });
            }

            // Só marca como registrado se ambos os botões foram configurados
            // (ou se não há botões para configurar)
            if (inventoryHudButton == null && powerGemHudButton == null)
                _hudButtonsRegistered = true;
            else if (InventoryUI.Instance != null || PowerGemUI.Instance != null)
                _hudButtonsRegistered = true;
        }

        // ── Vinculação ────────────────────────────────────────────────────

        public void BindLocalPlayer(PlayerEntity player)
        {
            if (player == null) return;

            if (_player == player)
            {
                attributeWindow?.BindPlayer(player);
                if (player.IsInitialized) ForceRefreshAll();

                // Tenta registrar botões novamente (pode ter falhado no Start
                // se os singletons ainda não existiam)
                RegisterHudButtonsSafe();
                return;
            }

            // Desvincula anterior
            if (_player != null)
            {
                _player.OnHPChanged    -= UpdateHP;
                _player.OnMPChanged    -= UpdateMP;
                _player.OnStatsChanged -= OnStatsChangedHandler;
                _player.OnInitialized  -= OnPlayerInitialized;
            }

            if (_skills != null)
            {
                _skills.OnCooldownStarted      -= OnSkillCooldown;
                _skills.OnSkillBarNeedsRefresh -= InitSkillBar;
            }

            _player    = player;
            _skills    = player.GetComponent<SkillSystem>();
            _netPlayer = player.GetComponent<RPG.Network.NetworkPlayer>();

            _player.OnHPChanged    += UpdateHP;
            _player.OnMPChanged    += UpdateMP;
            _player.OnStatsChanged += OnStatsChangedHandler;
            _player.OnInitialized  += OnPlayerInitialized;

            if (_skills != null)
            {
                _skills.OnCooldownStarted      += OnSkillCooldown;
                _skills.OnSkillBarNeedsRefresh += InitSkillBar;
                InitSkillBar();
            }

            attributeWindow?.BindPlayer(player);

            // Vincula UIs de inventário se já estiverem prontas
            var inventory = player.GetComponent<RPG.Network.NetworkInventory>();
            if (inventory != null)
            {
                InventoryUI.Instance?.BindInventory(inventory);
                PowerGemUI.Instance?.BindInventory(inventory);
            }

            // CORREÇÃO v9: registra os botões de HUD aqui, após o player spawnar.
            // Neste ponto os singletons de UI já estão garantidamente inicializados.
            RegisterHudButtonsSafe();

            if (player.IsInitialized)
                ForceRefreshAll();
            else
                Debug.Log("[UIManager] HUD vinculado — aguardando Initialize()");
        }

        private void OnPlayerInitialized() => ForceRefreshAll();

        private void OnSkillCooldown(int index, float duration)
        {
            if (skillSlots != null && index < skillSlots.Length)
                skillSlots[index]?.StartCooldown(duration);
        }

        private void OnStatsChangedHandler()
        {
            if (_player == null || !_player.IsInitialized) return;
            int level = _netPlayer != null ? _netPlayer.Level : (_player.Data?.Level ?? 1);
            if (levelText != null) levelText.text = $"Lv {level}";
        }

        private void InitSkillBar()
        {
            if (_skills == null || skillSlots == null) return;

            for (int i = 0; i < skillSlots.Length; i++)
            {
                if (skillSlots[i] == null) continue;

                var skill = _skills.GetSkill(i);

                if (skill?.Icon != null)
                    skillSlots[i].SetIcon(skill.Icon);
                else
                    skillSlots[i].SetIcon(null);

                if (hotkeyLabels != null && i < hotkeyLabels.Length)
                    skillSlots[i].SetHotkey(hotkeyLabels[i]);
            }
        }

        // ── Update — SOMENTE timer de mensagem ────────────────────────────

        private void Update()
        {
            if (_messageTimer > 0)
            {
                _messageTimer -= Time.deltaTime;
                if (_messageTimer <= 0 && messageText != null)
                    messageText.text = "";
            }
        }

        // ── HP / MP ───────────────────────────────────────────────────────

        private void UpdateHP(float current, float max)
        {
            if (hpBar  != null) { hpBar.maxValue = Mathf.Max(1f, max); hpBar.value = current; }
            if (hpText != null) hpText.text = $"{current:0}/{max:0}";
        }

        private void UpdateMP(float current, float max)
        {
            if (mpBar  != null) { mpBar.maxValue = Mathf.Max(1f, max); mpBar.value = current; }
            if (mpText != null) mpText.text = $"{current:0}/{max:0}";
        }

        private void ForceRefreshAll()
        {
            if (_player == null) return;

            float hp = _player.CurrentHP, maxHp = _player.Stats?.MaxHP ?? 1f;
            float mp = _player.CurrentMP, maxMp = _player.Stats?.MaxMP ?? 1f;

            UpdateHP(hp, maxHp);
            UpdateMP(mp, maxMp);

            if (playerNameText != null) playerNameText.text = _player.Data?.CharacterName ?? "Player";

            int level = _netPlayer != null ? _netPlayer.Level : (_player.Data?.Level ?? 1);
            if (levelText != null) levelText.text = $"Lv {level}";

            if (_netPlayer != null)
                RefreshExpBar(_netPlayer.Experience, _netPlayer.ExperienceToNextLevel);

            InitSkillBar();
        }

        public void RefreshLevel(int newLevel)
        {
            if (levelText != null) levelText.text = $"Lv {newLevel}";
        }

        public void RefreshExpBar(long exp, long expToNext)
        {
            if (expBar  != null) { expBar.maxValue = Mathf.Max(1f, expToNext); expBar.value = exp; }
            if (expText != null) expText.text = $"{exp}/{expToNext}";
        }

        // ── Target Panel ──────────────────────────────────────────────────

        public void UpdateTargetPanel(ITargetable target)
        {
            if (target == null) { ClearTargetPanel(); return; }
            if (targetPanel    != null) targetPanel.SetActive(true);
            if (targetNameText != null) targetNameText.text = target.DisplayName;
            RefreshTargetHP(target);
        }

        public void RefreshTargetPanel(ITargetable target)
        {
            if (target == null || targetPanel == null || !targetPanel.activeSelf) return;
            RefreshTargetHP(target);
        }

        private void RefreshTargetHP(ITargetable target)
        {
            if (targetHPBar  != null) { targetHPBar.maxValue = Mathf.Max(1f, target.MaxHP); targetHPBar.value = target.CurrentHP; }
            if (targetHPText != null) targetHPText.text = $"{target.CurrentHP:0}/{target.MaxHP:0}";
        }

        public void ClearTargetPanel()
        {
            if (targetPanel != null) targetPanel.SetActive(false);
        }

        // ── Message ───────────────────────────────────────────────────────

        public void ShowMessage(string msg)
        {
            if (messageText == null) return;
            messageText.text = msg;
            _messageTimer    = messageDisplayTime;
        }
    }
}