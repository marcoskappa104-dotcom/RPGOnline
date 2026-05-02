using UnityEngine;
using UnityEngine.UI;
using TMPro;
using RPG.Character;
using RPG.Combat;

namespace RPG.UI
{
    /// <summary>
    /// UIManager v4
    ///
    /// CORREÇÃO BUG HP 100/100:
    ///   O NetworkUIConnector chamava BindLocalPlayer() uma segunda vez depois
    ///   que o NetworkPlayerController já havia chamado. Na segunda chamada,
    ///   OnInitialized já tinha sido disparado e nunca dispararia novamente,
    ///   então o HUD ficava com os valores padrão dos Sliders (100/100).
    ///
    ///   Solução: BindLocalPlayer() agora verifica se já está vinculado ao
    ///   mesmo player e ignora chamadas duplicadas. Também ForceRefreshAll()
    ///   sempre roda ao vincular se o player já está inicializado.
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
            ClearTargetPanel();
            if (messageText != null) messageText.text = "";

            // Modo offline: tenta vincular se o player já existe e está inicializado
            var player = FindObjectOfType<PlayerEntity>();
            if (player != null && player.IsInitialized)
                BindLocalPlayer(player);
        }

        // ── Vinculação ────────────────────────────────────────────────────

        public void BindLocalPlayer(PlayerEntity player)
        {
            if (player == null) return;

            // CORREÇÃO: ignora chamadas duplicadas para o mesmo player
            // O NetworkUIConnector chama BindLocalPlayer depois do NetworkPlayerController,
            // mas ambos apontam para o mesmo PlayerEntity — a segunda chamada é inútil
            // e causa o bug do HP 100/100 (OnInitialized já foi disparado).
            if (_player == player)
            {
                Debug.Log($"[UIManager] BindLocalPlayer ignorado — já vinculado a {player.Data?.CharacterName}");
                // Mesmo assim força refresh caso o HUD esteja desatualizado
                if (player.IsInitialized) ForceRefreshAll();
                return;
            }

            // Desvincula player anterior
            if (_player != null)
            {
                _player.OnHPChanged    -= UpdateHP;
                _player.OnMPChanged    -= UpdateMP;
                _player.OnStatsChanged -= OnStatsChangedHandler;
                _player.OnInitialized  -= OnPlayerInitialized;
            }

            _player = player;
            _skills = player.GetComponent<SkillSystem>();

            // Vincula eventos
            _player.OnHPChanged    += UpdateHP;
            _player.OnMPChanged    += UpdateMP;
            _player.OnStatsChanged += OnStatsChangedHandler;

            // Assina OnInitialized para atualizar quando os dados reais chegarem
            // (modo online: Initialize() pode ainda não ter rodado)
            _player.OnInitialized += OnPlayerInitialized;

            // Vincula cooldowns
            if (_skills != null)
            {
                _skills.OnCooldownStarted -= OnSkillCooldown;
                _skills.OnCooldownStarted += OnSkillCooldown;
                InitSkillBar();
            }

            // Se o player já está inicializado, atualiza agora
            // (modo offline, ou quando NetworkPlayerController chama após Initialize())
            if (player.IsInitialized)
            {
                ForceRefreshAll();
                Debug.Log($"[UIManager] HUD vinculado e atualizado: {player.Data?.CharacterName} | " +
                          $"HP:{player.CurrentHP:0}/{player.Stats?.MaxHP:0}");
            }
            else
            {
                Debug.Log($"[UIManager] HUD vinculado — aguardando Initialize()");
            }
        }

        /// <summary>
        /// Chamado pelo PlayerEntity.OnInitialized quando Initialize() conclui.
        /// Este é o ponto garantido onde HP/MP reais estão disponíveis.
        /// </summary>
        private void OnPlayerInitialized()
        {
            Debug.Log($"[UIManager] OnPlayerInitialized → " +
                      $"HP:{_player?.CurrentHP:0}/{_player?.Stats?.MaxHP:0} | " +
                      $"MP:{_player?.CurrentMP:0}/{_player?.Stats?.MaxMP:0}");
            ForceRefreshAll();
        }

        private void OnSkillCooldown(int index, float duration)
        {
            if (skillSlots != null && index < skillSlots.Length)
                skillSlots[index]?.StartCooldown(duration);
        }

        private void OnStatsChangedHandler()
        {
            if (_player == null || !_player.IsInitialized) return;
            if (levelText != null)
                levelText.text = $"Lv {_player.Data?.Level ?? 1}";
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
            UpdateHP(_player.CurrentHP, _player.Stats?.MaxHP ?? 1f);
            UpdateMP(_player.CurrentMP, _player.Stats?.MaxMP ?? 1f);
            if (playerNameText != null)
                playerNameText.text = _player.Data?.CharacterName ?? "Player";
            if (levelText != null)
                levelText.text = $"Lv {_player.Data?.Level ?? 1}";
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
            if (_player?.CurrentTarget == null ||
                targetPanel == null || !targetPanel.activeSelf) return;
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

        // ── Exp Bar ───────────────────────────────────────────────────────

        private void UpdateExpBar()
        {
            if (_player?.Data == null || expBar == null || !_player.IsInitialized) return;
            expBar.maxValue = Mathf.Max(1f, _player.Data.ExperienceToNextLevel);
            expBar.value    = _player.Data.Experience;
            if (expText != null)
                expText.text = $"{_player.Data.Experience}/{_player.Data.ExperienceToNextLevel}";
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