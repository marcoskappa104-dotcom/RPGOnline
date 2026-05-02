using UnityEngine;
using UnityEngine.UI;
using TMPro;
using RPG.Character;
using RPG.Combat;

namespace RPG.UI
{
    /// <summary>
    /// UIManager v5
    ///
    /// CORREÇÃO BUG HP/MP 100/100 NA ENTRADA:
    ///
    ///   CAUSA RAIZ:
    ///     Os Sliders do Unity nascem com maxValue=1 e value=1.
    ///     Quando o UIManager se vincula ao PlayerEntity antes de Initialize()
    ///     terminar, OnHPChanged nunca é chamado com os valores reais — então
    ///     o Slider mostra 1/1. O texto formatava isso como "100/100" porque
    ///     usava Stats.MaxHP que ainda era 0, fazendo Mathf.Max(1f,0)=1.
    ///
    ///   SOLUÇÃO (3 partes):
    ///     1. PlayerEntity.Initialize() dispara OnInitialized após ter Data+Stats.
    ///     2. UIManager assina OnInitialized → chama ForceRefreshAll() com valores reais.
    ///     3. ForceRefreshAll() seta maxValue dos Sliders ANTES de value — ordem importa.
    ///        Se você setar value antes de maxValue, o Unity clampeia value em 1.
    ///
    ///   CORREÇÃO BUG NULL REF AO MATAR MOB:
    ///     UpdateTargetHP() acessava CurrentTarget.CurrentHP em um UnityEngine.Object
    ///     destruído (mob com Destroy(go, 3s)). Adicionado null-check via operador
    ///     de comparação do Unity (que detecta objetos destruídos).
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

        /// <summary>
        /// Vincula o HUD ao PlayerEntity local.
        /// Pode ser chamado múltiplas vezes — ignora duplicatas para o mesmo player.
        ///
        /// ORDEM DE EVENTOS (modo online):
        ///   1. NetworkPlayerController.OnStartLocalPlayer() → chama BindLocalPlayer()
        ///      Neste momento PlayerEntity.IsInitialized pode ainda ser false.
        ///      UIManager assina OnInitialized e aguarda.
        ///   2. PlayerEntity.Initialize() é chamado → seta Data e Stats → dispara OnInitialized.
        ///   3. UIManager.OnPlayerInitialized() é chamado → ForceRefreshAll() com valores reais.
        ///
        /// ORDEM DE EVENTOS (modo offline):
        ///   1. PlayerEntity.Start() → Initialize() → IsInitialized=true → dispara OnInitialized.
        ///   2. UIManager.Start() → BindLocalPlayer() → player já inicializado → ForceRefreshAll() imediato.
        /// </summary>
        public void BindLocalPlayer(PlayerEntity player)
        {
            if (player == null) return;

            // Ignora chamadas duplicadas para o mesmo player
            if (_player == player)
            {
                Debug.Log($"[UIManager] BindLocalPlayer duplicado ignorado — {player.Data?.CharacterName}");
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

            // Assina OnInitialized — garante que a UI atualiza quando os dados reais chegarem.
            // Em modo online, Initialize() pode não ter rodado ainda neste momento.
            _player.OnInitialized += OnPlayerInitialized;

            // Vincula cooldowns das skills
            if (_skills != null)
            {
                _skills.OnCooldownStarted -= OnSkillCooldown;
                _skills.OnCooldownStarted += OnSkillCooldown;
                InitSkillBar();
            }

            // Se já inicializado (modo offline ou bind tardio), atualiza agora
            if (player.IsInitialized)
            {
                ForceRefreshAll();
                Debug.Log($"[UIManager] HUD vinculado e atualizado: {player.Data?.CharacterName} | " +
                          $"HP:{player.CurrentHP:0}/{player.Stats?.MaxHP:0}");
            }
            else
            {
                Debug.Log("[UIManager] HUD vinculado — aguardando Initialize()");
            }
        }

        /// <summary>
        /// Chamado por PlayerEntity.OnInitialized quando Initialize() conclui.
        /// Este é o ponto garantido onde HP/MP reais estão disponíveis.
        ///
        /// CORREÇÃO BUG 100/100:
        ///   ForceRefreshAll() seta maxValue ANTES de value em cada Slider.
        ///   Se value for setado antes, o Unity clampeia em maxValue=1 (padrão).
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

        /// <summary>
        /// CORREÇÃO BUG 100/100:
        ///   Seta maxValue ANTES de value. O Slider do Unity clampeia value
        ///   em [0, maxValue] na hora da atribuição. Se maxValue ainda for 1
        ///   (padrão) quando você seta value=500, o Slider fica em 1.
        /// </summary>
        private void UpdateHP(float current, float max)
        {
            if (hpBar != null)
            {
                hpBar.maxValue = Mathf.Max(1f, max); // maxValue PRIMEIRO
                hpBar.value    = current;             // value DEPOIS
            }
            if (hpText != null) hpText.text = $"{current:0}/{max:0}";
        }

        private void UpdateMP(float current, float max)
        {
            if (mpBar != null)
            {
                mpBar.maxValue = Mathf.Max(1f, max); // maxValue PRIMEIRO
                mpBar.value    = current;             // value DEPOIS
            }
            if (mpText != null) mpText.text = $"{current:0}/{max:0}";
        }

        /// <summary>
        /// Atualiza todos os campos do HUD com os valores atuais do player.
        /// IMPORTANTE: deve ser chamado APÓS Initialize() ter rodado.
        /// </summary>
        private void ForceRefreshAll()
        {
            if (_player == null) return;

            float hp    = _player.CurrentHP;
            float maxHp = _player.Stats?.MaxHP ?? 1f;
            float mp    = _player.CurrentMP;
            float maxMp = _player.Stats?.MaxMP ?? 1f;

            // Atualiza HP e MP (ordem maxValue → value garantida dentro de UpdateHP/MP)
            UpdateHP(hp, maxHp);
            UpdateMP(mp, maxMp);

            if (playerNameText != null)
                playerNameText.text = _player.Data?.CharacterName ?? "Player";
            if (levelText != null)
                levelText.text = $"Lv {_player.Data?.Level ?? 1}";

            Debug.Log($"[UIManager] ForceRefreshAll — HP:{hp:0}/{maxHp:0} MP:{mp:0}/{maxMp:0}");
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

        /// <summary>
        /// CORREÇÃO NULL REF AO MATAR MOB:
        ///   Quando um mob morre, Destroy(gameObject, Xs) é chamado. Durante esses X
        ///   segundos, CurrentTarget ainda aponta para o objeto. O operador == do
        ///   Unity retorna true para objetos destruídos comparados a null — usamos
        ///   isso para detectar o objeto destruído e limpar o painel.
        /// </summary>
        private void UpdateTargetHP()
        {
            if (_player == null || targetPanel == null || !targetPanel.activeSelf) return;

            var t = _player.CurrentTarget;
            if (t == null) return;

            // Verifica se o UnityEngine.Object foi destruído
            // (o operador == do Unity detecta objetos destruídos como null)
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