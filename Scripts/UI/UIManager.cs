// UIManager v8 — PATCH para integração com o sistema de inventário e joias.
//
// MUDANÇAS v8 (vs v7):
//   1. BindLocalPlayer: agora também vincula InventoryUI e PowerGemUI.
//   2. OnSkillBarNeedsRefresh: subscreve ao evento do SkillSystem para
//      re-inicializar a SkillBar quando o loadout de joias muda.
//      Isso atualiza ícones e nomes das skills ao equipar/desequipar joias.
//   3. InitSkillBar: agora lê skills do SkillSystem (que lê das joias),
//      não de uma lista hardcoded — comportamento correto com o novo sistema.
//
// INSTRUÇÕES:
//   Substitua o UIManager.cs completo por este arquivo.
//   O código é idêntico ao v7 exceto pelas seções marcadas com "NOVO v8".

using UnityEngine;
using UnityEngine.UI;
using TMPro;
using RPG.Character;
using RPG.Combat;

namespace RPG.UI
{
    /// <summary>
    /// UIManager v8 — Integrado com inventário e Joias do Poder.
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

        // NOVO v8: botões de atalho para Inventário e Joias na HUD (opcional)
        [Header("Atalhos de UI (opcional)")]
        [SerializeField] private Button inventoryHudButton;
        [SerializeField] private Button powerGemHudButton;

        private PlayerEntity              _player;
        private SkillSystem               _skills;
        private RPG.Network.NetworkPlayer _netPlayer;
        private float                     _messageTimer;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        private void Start()
        {
            ClearTargetPanel();
            if (messageText != null) messageText.text = "";

            if (attributeWindowButton != null)
                attributeWindowButton.onClick.AddListener(() => attributeWindow?.Toggle());

            // NOVO v8: botões de atalho
            if (inventoryHudButton != null)
                inventoryHudButton.onClick.AddListener(() => InventoryUI.Instance?.Toggle());
            if (powerGemHudButton != null)
                powerGemHudButton.onClick.AddListener(() => PowerGemUI.Instance?.Toggle());

            // Modo offline
            var player = FindObjectOfType<PlayerEntity>();
            if (player != null && player.IsInitialized)
                BindLocalPlayer(player);
        }

        // ── Vinculação ────────────────────────────────────────────────────

        public void BindLocalPlayer(PlayerEntity player)
        {
            if (player == null) return;

            if (_player == player)
            {
                attributeWindow?.BindPlayer(player);
                if (player.IsInitialized) ForceRefreshAll();
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

            // NOVO v8: desinscreve do SkillSystem anterior
            if (_skills != null)
            {
                _skills.OnCooldownStarted     -= OnSkillCooldown;
                _skills.OnSkillBarNeedsRefresh -= InitSkillBar; // NOVO v8
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
                _skills.OnCooldownStarted     += OnSkillCooldown;
                _skills.OnSkillBarNeedsRefresh += InitSkillBar; // NOVO v8
                InitSkillBar();
            }

            attributeWindow?.BindPlayer(player);

            // NOVO v8: vincula UIs de inventário se já estiverem prontas
            var inventory = player.GetComponent<RPG.Network.NetworkInventory>();
            if (inventory != null)
            {
                InventoryUI.Instance?.BindInventory(inventory);
                PowerGemUI.Instance?.BindInventory(inventory);
            }

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

        /// <summary>
        /// NOVO v8: atualiza ícones e nomes da skill bar quando o loadout de joias muda.
        /// Chamado automaticamente via OnSkillBarNeedsRefresh do SkillSystem.
        /// </summary>
        private void InitSkillBar()
        {
            if (_skills == null || skillSlots == null) return;

            for (int i = 0; i < skillSlots.Length; i++)
            {
                if (skillSlots[i] == null) continue;

                var skill = _skills.GetSkill(i); // lê da joia equipada

                if (skill?.Icon != null)
                    skillSlots[i].SetIcon(skill.Icon);
                else
                    skillSlots[i].SetIcon(null); // limpa ícone se slot vazio

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
            if (hpBar != null) { hpBar.maxValue = Mathf.Max(1f, max); hpBar.value = current; }
            if (hpText != null) hpText.text = $"{current:0}/{max:0}";
        }

        private void UpdateMP(float current, float max)
        {
            if (mpBar != null) { mpBar.maxValue = Mathf.Max(1f, max); mpBar.value = current; }
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

            InitSkillBar(); // NOVO v8: atualiza skill bar na inicialização
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