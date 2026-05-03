using UnityEngine;
using UnityEngine.UI;
using TMPro;
using RPG.Character;
using RPG.Combat;

namespace RPG.UI
{
    /// <summary>
    /// UIManager v6
    ///
    /// MUDANÇAS DESTA VERSÃO:
    ///   - RefreshLevel(int) adicionado: chamado pelo hook OnLevelChanged
    ///     do NetworkPlayer quando o servidor confirma o novo nível.
    ///   - HP/MP agora chegam exclusivamente via eventos do PlayerEntity,
    ///     que por sua vez são alimentados pelos SyncVar hooks do NetworkPlayer.
    ///     Não há mais chamada a ForceSetHP de múltiplos caminhos.
    ///   - UpdateExpBar() lê os valores do NetworkPlayer se disponível,
    ///     garantindo que XP exibido é o confirmado pelo servidor.
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

        [Header("Hotkeys (ordem Q W E R)")]
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

        private PlayerEntity              _player;
        private SkillSystem               _skills;
        private RPG.Network.NetworkPlayer _netPlayer; // para ler XP/Level via SyncVars
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

            _player    = player;
            _skills    = player.GetComponent<SkillSystem>();
            _netPlayer = player.GetComponent<RPG.Network.NetworkPlayer>();

            _player.OnHPChanged    += UpdateHP;
            _player.OnMPChanged    += UpdateMP;
            _player.OnStatsChanged += OnStatsChangedHandler;
            _player.OnInitialized  += OnPlayerInitialized;

            if (_skills != null)
            {
                _skills.OnCooldownStarted -= OnSkillCooldown;
                _skills.OnCooldownStarted += OnSkillCooldown;
                InitSkillBar();
            }

            attributeWindow?.BindPlayer(player);

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
                if (skill?.Icon != null) skillSlots[i].SetIcon(skill.Icon);
                if (hotkeyLabels != null && i < hotkeyLabels.Length)
                    skillSlots[i].SetHotkey(hotkeyLabels[i]);
            }
        }

        // ── Update ────────────────────────────────────────────────────────

        private void Update()
        {
            if (_messageTimer > 0)
            {
                _messageTimer -= Time.deltaTime;
                if (_messageTimer <= 0 && messageText != null)
                    messageText.text = "";
            }
            UpdateTargetHP();
            UpdateExpBar();
        }

        // ── HP / MP ───────────────────────────────────────────────────────

        private void UpdateHP(float current, float max)
        {
            if (hpBar != null)
            {
                hpBar.maxValue = Mathf.Max(1f, max);
                hpBar.value    = current;
            }
            if (hpText != null) hpText.text = $"{current:0}/{max:0}";
        }

        private void UpdateMP(float current, float max)
        {
            if (mpBar != null)
            {
                mpBar.maxValue = Mathf.Max(1f, max);
                mpBar.value    = current;
            }
            if (mpText != null) mpText.text = $"{current:0}/{max:0}";
        }

        private void ForceRefreshAll()
        {
            if (_player == null) return;

            float hp    = _player.CurrentHP;
            float maxHp = _player.Stats?.MaxHP ?? 1f;
            float mp    = _player.CurrentMP;
            float maxMp = _player.Stats?.MaxMP ?? 1f;

            UpdateHP(hp, maxHp);
            UpdateMP(mp, maxMp);

            if (playerNameText != null)
                playerNameText.text = _player.Data?.CharacterName ?? "Player";

            int level = _netPlayer != null ? _netPlayer.Level : (_player.Data?.Level ?? 1);
            if (levelText != null) levelText.text = $"Lv {level}";
        }

        /// <summary>
        /// Chamado pelo hook OnLevelChanged do NetworkPlayer
        /// quando o servidor confirma o level up.
        /// </summary>
        public void RefreshLevel(int newLevel)
        {
            if (levelText != null) levelText.text = $"Lv {newLevel}";
        }

        // ── Target Panel ──────────────────────────────────────────────────

        public void UpdateTargetPanel(ITargetable target)
        {
            if (target == null) { ClearTargetPanel(); return; }
            if (targetPanel    != null) targetPanel.SetActive(true);
            if (targetNameText != null) targetNameText.text = target.DisplayName;
            if (targetHPBar    != null)
            {
                targetHPBar.maxValue = Mathf.Max(1f, target.MaxHP);
                targetHPBar.value    = target.CurrentHP;
            }
            if (targetHPText != null)
                targetHPText.text = $"{target.CurrentHP:0}/{target.MaxHP:0}";
        }

        private void UpdateTargetHP()
        {
            if (_player == null || targetPanel == null || !targetPanel.activeSelf) return;

            var t = _player.CurrentTarget;
            if (t == null) return;

            if (t is UnityEngine.Object unityObj && unityObj == null)
            {
                _player.ClearTarget();
                ClearTargetPanel();
                return;
            }

            if (t.IsDead)
            {
                ClearTargetPanel();
                return;
            }

            if (targetHPBar != null)
            {
                targetHPBar.maxValue = Mathf.Max(1f, t.MaxHP);
                targetHPBar.value    = t.CurrentHP;
            }
            if (targetHPText != null)
                targetHPText.text = $"{t.CurrentHP:0}/{t.MaxHP:0}";
        }

        public void ClearTargetPanel()
        {
            if (targetPanel != null) targetPanel.SetActive(false);
        }

        // ── Exp Bar ───────────────────────────────────────────────────────

        private void UpdateExpBar()
        {
            if (_player == null || expBar == null || !_player.IsInitialized) return;

            // Prioriza SyncVars do NetworkPlayer (confirmados pelo servidor)
            long exp      = _netPlayer != null ? _netPlayer.Experience            : (_player.Data?.Experience ?? 0);
            long expToNxt = _netPlayer != null ? _netPlayer.ExperienceToNextLevel : (_player.Data?.ExperienceToNextLevel ?? 100);

            expBar.maxValue = Mathf.Max(1f, expToNxt);
            expBar.value    = exp;

            if (expText != null)
                expText.text = $"{exp}/{expToNxt}";
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