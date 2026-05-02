using UnityEngine;
using UnityEngine.UI;
using TMPro;
using RPG.Character;
using RPG.Combat;

namespace RPG.UI
{
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

        [Header("Message")]
        [SerializeField] private TMP_Text messageText;
        [SerializeField] private float    messageDisplayTime = 2f;

        [Header("Experience")]
        [SerializeField] private Slider   expBar;
        [SerializeField] private TMP_Text expText;

        private PlayerEntity _player;
        private SkillSystem  _skills;
        private float        _messageTimer;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        private void Start()
        {
            var player = FindObjectOfType<PlayerEntity>();
            if (player != null) BindLocalPlayer(player);
            ClearTargetPanel();
            if (messageText != null) messageText.text = "";
        }

        public void BindLocalPlayer(PlayerEntity player)
        {
            if (player == null) return;

            if (_player != null)
            {
                _player.OnHPChanged    -= UpdateHP;
                _player.OnMPChanged    -= UpdateMP;
                _player.OnStatsChanged -= RefreshAll;
            }

            _player = player;
            _skills = player.GetComponent<SkillSystem>();

            _player.OnHPChanged    += UpdateHP;
            _player.OnMPChanged    += UpdateMP;
            _player.OnStatsChanged += RefreshAll;

            if (_skills != null)
                _skills.OnCooldownStarted += (i, dur) =>
                {
                    if (skillSlots != null && i < skillSlots.Length)
                        skillSlots[i]?.StartCooldown(dur);
                };

            if (playerNameText != null) playerNameText.text = player.Data?.CharacterName ?? "Player";
            if (levelText      != null) levelText.text      = $"Lv {player.Data?.Level ?? 1}";

            RefreshAll();
            Debug.Log($"[UIManager] HUD vinculado a {player.Data?.CharacterName}");
        }

        private void Update()
        {
            if (_messageTimer > 0)
            {
                _messageTimer -= Time.deltaTime;
                if (_messageTimer <= 0 && messageText != null) messageText.text = "";
            }
            UpdateTargetHP();
            UpdateExpBar();
        }

        private void UpdateHP(float current, float max)
        {
            if (hpBar  != null) { hpBar.maxValue = max; hpBar.value = current; }
            if (hpText != null) hpText.text = $"{current:0}/{max:0}";
        }

        private void UpdateMP(float current, float max)
        {
            if (mpBar  != null) { mpBar.maxValue = max; mpBar.value = current; }
            if (mpText != null) mpText.text = $"{current:0}/{max:0}";
        }

        private void RefreshAll()
        {
            if (_player == null || !_player.IsInitialized) return;
            UpdateHP(_player.CurrentHP, _player.Stats.MaxHP);
            UpdateMP(_player.CurrentMP, _player.Stats.MaxMP);
            if (levelText != null) levelText.text = $"Lv {_player.Data?.Level ?? 1}";
        }

        // ── Target Panel ──────────────────────────────────────────────────

        public void UpdateTargetPanel(ITargetable target)
        {
            if (target == null) { ClearTargetPanel(); return; }

            Debug.Log($"[UIManager] UpdateTargetPanel: {target.DisplayName} " +
                      $"HP:{target.CurrentHP:0}/{target.MaxHP:0}");

            if (targetPanel    != null) targetPanel.SetActive(true);
            if (targetNameText != null) targetNameText.text = target.DisplayName;

            if (targetHPBar != null)
            {
                // Garante maxValue antes de value para evitar clamp errado
                targetHPBar.maxValue = Mathf.Max(1f, target.MaxHP);
                targetHPBar.value    = target.CurrentHP;
            }
            if (targetHPText != null)
                targetHPText.text = $"{target.CurrentHP:0}/{target.MaxHP:0}";
        }

        private void UpdateTargetHP()
        {
            if (_player?.CurrentTarget == null || targetPanel == null || !targetPanel.activeSelf)
                return;

            var t = _player.CurrentTarget;
            if (t.IsDead) { ClearTargetPanel(); return; }

            if (targetHPBar  != null)
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

        private void UpdateExpBar()
        {
            if (_player?.Data == null || expBar == null || !_player.IsInitialized) return;
            expBar.maxValue = Mathf.Max(1f, _player.Data.ExperienceToNextLevel);
            expBar.value    = _player.Data.Experience;
            if (expText != null)
                expText.text = $"{_player.Data.Experience}/{_player.Data.ExperienceToNextLevel}";
        }

        public void ShowMessage(string msg)
        {
            if (messageText == null) return;
            messageText.text = msg;
            _messageTimer    = messageDisplayTime;
        }
    }
}
