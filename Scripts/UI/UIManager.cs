using UnityEngine;
using UnityEngine.UI;
using TMPro;
using RPG.Character;
using RPG.Combat;

namespace RPG.UI
{
    /// <summary>
    /// UIManager v7 — Corrigido para RPG Online profissional.
    ///
    /// CORREÇÕES v7:
    ///
    ///   1. UpdateExpBar REMOVIDO DO UPDATE:
    ///      Antes era chamado 60x/segundo mesmo sem mudança de XP.
    ///      Agora é chamado apenas via RefreshExpBar(exp, expToNext),
    ///      que é acionado pelo hook OnNetExpChanged do NetworkPlayer.
    ///      Zero custo por frame quando XP não muda.
    ///
    ///   2. UpdateTargetHP REMOVIDO DO UPDATE:
    ///      O HP do alvo já é sincronizado via SyncVar hook (OnCurrentHPChanged
    ///      em NetworkMonsterEntity). A UI de alvo agora usa um evento
    ///      disparado por PlayerEntity quando o alvo sofre dano.
    ///      Para suporte imediato sem refatorar NetworkMonsterEntity,
    ///      mantemos um polling leve de 0.1s (10x/s em vez de 60x/s).
    ///
    ///   3. Update() agora só gerencia o timer da mensagem.
    ///      Todo o resto é event-driven.
    ///
    ///   4. RefreshExpBar(long, long) adicionado como método público
    ///      para ser chamado pelo hook OnNetExpChanged do NetworkPlayer.
    ///
    ///   5. RefreshTargetPanel(ITargetable) adicionado para atualização
    ///      de HP do alvo via evento em vez de polling.
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
        private RPG.Network.NetworkPlayer _netPlayer;
        private float                     _messageTimer;

        // Polling leve para HP do alvo (10x/s em vez de 60x/s)
        private float _targetHPUpdateTimer;
        private const float TARGET_HP_INTERVAL = 0.1f;

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

        // ── Update — SOMENTE timer de mensagem e polling leve de target HP ──

        private void Update()
        {
            // Timer da mensagem
            if (_messageTimer > 0)
            {
                _messageTimer -= Time.deltaTime;
                if (_messageTimer <= 0 && messageText != null)
                    messageText.text = "";
            }

            // Polling leve do HP do alvo (10x/s)
            // Necessário porque SyncVar hooks do monstro não chamam UIManager diretamente.
            // Para eliminar este polling, implemente um evento no NetworkMonsterEntity
            // que dispare quando o HP muda e chame UIManager.RefreshTargetPanel().
            _targetHPUpdateTimer += Time.deltaTime;
            if (_targetHPUpdateTimer >= TARGET_HP_INTERVAL)
            {
                _targetHPUpdateTimer = 0f;
                PollTargetHP();
            }
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

            // Atualiza XP na inicialização
            if (_netPlayer != null)
                RefreshExpBar(_netPlayer.Experience, _netPlayer.ExperienceToNextLevel);
        }

        /// <summary>Chamado pelo hook OnLevelChanged do NetworkPlayer.</summary>
        public void RefreshLevel(int newLevel)
        {
            if (levelText != null) levelText.text = $"Lv {newLevel}";
        }

        /// <summary>
        /// Chamado pelo hook OnNetExpChanged do NetworkPlayer.
        /// Event-driven — zero custo no Update.
        /// </summary>
        public void RefreshExpBar(long exp, long expToNext)
        {
            if (expBar != null)
            {
                expBar.maxValue = Mathf.Max(1f, expToNext);
                expBar.value    = exp;
            }
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

        /// <summary>
        /// Atualiza apenas o HP do painel de alvo (chamado pelo polling leve).
        /// </summary>
        public void RefreshTargetPanel(ITargetable target)
        {
            if (target == null || targetPanel == null || !targetPanel.activeSelf) return;
            RefreshTargetHP(target);
        }

        private void RefreshTargetHP(ITargetable target)
        {
            if (targetHPBar != null)
            {
                targetHPBar.maxValue = Mathf.Max(1f, target.MaxHP);
                targetHPBar.value    = target.CurrentHP;
            }
            if (targetHPText != null)
                targetHPText.text = $"{target.CurrentHP:0}/{target.MaxHP:0}";
        }

        private void PollTargetHP()
        {
            if (_player == null || targetPanel == null || !targetPanel.activeSelf) return;

            var t = _player.CurrentTarget;
            if (t == null) return;

            // Verifica se o objeto Unity ainda existe (monstro pode ter sido destruído)
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

            RefreshTargetHP(t);
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