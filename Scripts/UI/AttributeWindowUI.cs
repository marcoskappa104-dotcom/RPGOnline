using UnityEngine;
using UnityEngine.UI;
using TMPro;
using RPG.Character;
using RPG.Data;
using RPG.Managers;
using NetworkPlayer = RPG.Network.NetworkPlayer;

namespace RPG.UI
{
    /// <summary>
    /// AttributeWindowUI — Janela de Atributos do Personagem
    ///
    /// Exibe:
    ///   - Informações gerais (nome, raça, nível, XP)
    ///   - Stats derivados (HP, MP, ATK, MATK, DEF, MDEF, ASPD, HIT, FLEE, CRIT)
    ///   - Atributos base com botões de + para distribuir pontos livres
    ///
    /// COMO USAR:
    ///   1. Adicione este script a um Empty GameObject (ex: "AttributeWindow")
    ///   2. Monte a hierarquia de UI conforme descrito abaixo
    ///   3. Arraste todas as referências no Inspector
    ///   4. Abra/feche via AttributeWindowUI.Instance.Toggle() ou pressione 'C'
    ///
    /// HIERARQUIA SUGERIDA:
    ///   Canvas
    ///   └── AttributeWindowPanel
    ///       ├── Header
    ///       │   ├── TitleText           "Atributos"
    ///       │   ├── CloseButton         X
    ///       │   ├── CharNameText        "Aragorn"
    ///       │   ├── RaceText            "Humano"
    ///       │   └── LevelText           "Nível 5"
    ///       ├── FreePointsBanner        (ativo apenas quando há pontos)
    ///       │   └── FreePointsText      "5 pontos disponíveis!"
    ///       ├── LeftColumn — Atributos Base
    ///       │   ├── STR_Row
    ///       │   │   ├── LabelText       "Força"
    ///       │   │   ├── ValueText       "17"
    ///       │   │   └── PlusButton      "+"
    ///       │   ├── AGI_Row  (igual)
    ///       │   ├── VIT_Row  (igual)
    ///       │   ├── DEX_Row  (igual)
    ///       │   ├── INT_Row  (igual)
    ///       │   └── LUK_Row  (igual)
    ///       ├── RightColumn — Status Derivados
    ///       │   ├── HPText              "HP:  850 / 850"
    ///       │   ├── MPText              "MP:  320 / 320"
    ///       │   ├── ATKText             "ATK:   47"
    ///       │   ├── MATKText            "MATK:  28"
    ///       │   ├── DEFText             "DEF:   22"
    ///       │   ├── MDEFText            "MDEF:  18"
    ///       │   ├── ASPDText            "ASPD:  7.0"
    ///       │   ├── HITText             "HIT:   24"
    ///       │   ├── FLEEText            "FLEE:  20"
    ///       │   └── CRITText            "CRÍTICO: 9%"
    ///       └── BottomBar
    ///           ├── XPBar (Slider)
    ///           └── XPText              "1250 / 3162 XP"
    /// </summary>
    public class AttributeWindowUI : MonoBehaviour
    {
        public static AttributeWindowUI Instance { get; private set; }

        // ── Painel principal ──────────────────────────────────────────────
        [Header("Painel")]
        [SerializeField] private GameObject windowPanel;

        // ── Header ────────────────────────────────────────────────────────
        [Header("Header")]
        [SerializeField] private TMP_Text charNameText;
        [SerializeField] private TMP_Text raceText;
        [SerializeField] private TMP_Text levelText;
        [SerializeField] private Button   closeButton;

        // ── Banner de pontos livres ───────────────────────────────────────
        [Header("Pontos Livres")]
        [SerializeField] private GameObject freePointsBanner;
        [SerializeField] private TMP_Text   freePointsText;

        // ── Atributos Base — valores exibidos ─────────────────────────────
        [Header("Atributos Base — Textos")]
        [SerializeField] private TMP_Text strValueText;
        [SerializeField] private TMP_Text agiValueText;
        [SerializeField] private TMP_Text vitValueText;
        [SerializeField] private TMP_Text dexValueText;
        [SerializeField] private TMP_Text intValueText;
        [SerializeField] private TMP_Text lukValueText;

        // ── Atributos Base — botões + ─────────────────────────────────────
        [Header("Atributos Base — Botões +")]
        [SerializeField] private Button strPlusButton;
        [SerializeField] private Button agiPlusButton;
        [SerializeField] private Button vitPlusButton;
        [SerializeField] private Button dexPlusButton;
        [SerializeField] private Button intPlusButton;
        [SerializeField] private Button lukPlusButton;

        // ── Status Derivados ──────────────────────────────────────────────
        [Header("Status Derivados")]
        [SerializeField] private TMP_Text hpDerivedText;
        [SerializeField] private TMP_Text mpDerivedText;
        [SerializeField] private TMP_Text atkText;
        [SerializeField] private TMP_Text matkText;
        [SerializeField] private TMP_Text defText;
        [SerializeField] private TMP_Text mdefText;
        [SerializeField] private TMP_Text aspdText;
        [SerializeField] private TMP_Text hitText;
        [SerializeField] private TMP_Text fleeText;
        [SerializeField] private TMP_Text critText;
        [SerializeField] private TMP_Text hpregenText;
        [SerializeField] private TMP_Text mpregenText;

        // ── Barra de XP ───────────────────────────────────────────────────
        [Header("XP")]
        [SerializeField] private Slider   xpBar;
        [SerializeField] private TMP_Text xpText;

        // ── Tecla de atalho ───────────────────────────────────────────────
        [Header("Tecla de Atalho")]
        [SerializeField] private KeyCode toggleKey = KeyCode.C;

        // ── Estado interno ────────────────────────────────────────────────
        private PlayerEntity _player;
        private bool         _isOpen;

        // ─────────────────────────────────────────────────────────────────
        // Unity
        // ─────────────────────────────────────────────────────────────────

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        private void Start()
        {
            // Fecha a janela ao iniciar
            if (windowPanel != null) windowPanel.SetActive(false);
            _isOpen = false;

            // Botão fechar
            if (closeButton != null)
                closeButton.onClick.AddListener(Close);

            // Botões de + para cada atributo
            if (strPlusButton != null) strPlusButton.onClick.AddListener(() => AllocatePoint(Stat.STR));
            if (agiPlusButton != null) agiPlusButton.onClick.AddListener(() => AllocatePoint(Stat.AGI));
            if (vitPlusButton != null) vitPlusButton.onClick.AddListener(() => AllocatePoint(Stat.VIT));
            if (dexPlusButton != null) dexPlusButton.onClick.AddListener(() => AllocatePoint(Stat.DEX));
            if (intPlusButton != null) intPlusButton.onClick.AddListener(() => AllocatePoint(Stat.INT));
            if (lukPlusButton != null) lukPlusButton.onClick.AddListener(() => AllocatePoint(Stat.LUK));

            // Tenta vincular ao player offline
            var player = FindObjectOfType<PlayerEntity>();
            if (player != null && player.IsInitialized)
                BindPlayer(player);
        }

        private void Update()
        {
            // Abre/fecha com a tecla de atalho
            if (Input.GetKeyDown(toggleKey))
                Toggle();

            // Atualiza a janela enquanto estiver aberta
            if (_isOpen && _player != null && _player.IsInitialized)
                RefreshAll();
        }

        // ─────────────────────────────────────────────────────────────────
        // Vínculo com o PlayerEntity
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Vincula a janela ao PlayerEntity local.
        /// Chamado pelo UIManager após o player ser inicializado.
        /// </summary>
        public void BindPlayer(PlayerEntity player)
        {
            if (player == null) return;

            // Desvincula anterior
            if (_player != null)
            {
                _player.OnStatsChanged -= OnStatsChanged;
                _player.OnInitialized  -= OnInitialized;
            }

            _player = player;
            _player.OnStatsChanged += OnStatsChanged;
            _player.OnInitialized  += OnInitialized;

            if (player.IsInitialized)
                RefreshAll();

            Debug.Log($"[AttributeWindow] Vinculado a {player.Data?.CharacterName}");
        }

        private void OnInitialized()   => RefreshAll();
        private void OnStatsChanged()  => RefreshAll();

        // ─────────────────────────────────────────────────────────────────
        // Abrir / Fechar
        // ─────────────────────────────────────────────────────────────────

        public void Toggle()
        {
            if (_isOpen) Close();
            else         Open();
        }

        public void Open()
        {
            if (windowPanel == null) return;
            _isOpen = true;
            windowPanel.SetActive(true);
            RefreshAll();
        }

        public void Close()
        {
            if (windowPanel == null) return;
            _isOpen = false;
            windowPanel.SetActive(false);
        }

        // ─────────────────────────────────────────────────────────────────
        // Atualizar UI
        // ─────────────────────────────────────────────────────────────────

        private void RefreshAll()
        {
            if (_player == null || !_player.IsInitialized) return;

            var data  = _player.Data;
            var stats = _player.Stats;
            if (data == null || stats == null) return;

            RefreshHeader(data);
            RefreshBaseAttributes(data);
            RefreshDerivedStats(stats, _player.CurrentHP, _player.CurrentMP);
            RefreshXPBar(data);
            RefreshFreePointsBanner(data);
            RefreshPlusButtons(data);
        }

        private void RefreshHeader(CharacterData data)
        {
            if (charNameText != null) charNameText.text = data.CharacterName;
            if (raceText     != null) raceText.text     = RaceDisplayName(data.Race);
            if (levelText    != null) levelText.text    = $"Nível {data.Level}";
        }

        private void RefreshBaseAttributes(CharacterData data)
        {
            // Recalcula bônus de raça para exibir o total correto
            var bonus = StatsCalculator.GetRaceBonus(data.Race);

            // Total = 10 (base) + bônus de raça + pontos alocados manualmente
            int totalSTR = 10 + bonus.STR + data.AllocatedSTR;
            int totalAGI = 10 + bonus.AGI + data.AllocatedAGI;
            int totalVIT = 10 + bonus.VIT + data.AllocatedVIT;
            int totalDEX = 10 + bonus.DEX + data.AllocatedDEX;
            int totalINT = 10 + bonus.INT + data.AllocatedINT;
            int totalLUK = 10 + bonus.LUK + data.AllocatedLUK;

            // Mostra: total (bônus de raça entre parênteses se houver)
            SetAttrText(strValueText, totalSTR, bonus.STR + data.AllocatedSTR);
            SetAttrText(agiValueText, totalAGI, bonus.AGI + data.AllocatedAGI);
            SetAttrText(vitValueText, totalVIT, bonus.VIT + data.AllocatedVIT);
            SetAttrText(dexValueText, totalDEX, bonus.DEX + data.AllocatedDEX);
            SetAttrText(intValueText, totalINT, bonus.INT + data.AllocatedINT);
            SetAttrText(lukValueText, totalLUK, bonus.LUK + data.AllocatedLUK);
        }

        /// <summary>
        /// Exibe: "17 (+7)" em verde quando há bônus acima do base 10.
        /// </summary>
        private void SetAttrText(TMP_Text label, int total, int bonus)
        {
            if (label == null) return;
            if (bonus > 0)
                label.text = $"{total} <color=#88FF88>(+{bonus})</color>";
            else
                label.text = $"{total}";
        }

        private void RefreshDerivedStats(DerivedStats s, float currentHP, float currentMP)
        {
            if (hpDerivedText != null) hpDerivedText.text = $"{currentHP:0} / {s.MaxHP:0}";
            if (mpDerivedText != null) mpDerivedText.text = $"{currentMP:0} / {s.MaxMP:0}";
            if (atkText       != null) atkText.text       = $"{s.ATK:0}";
            if (matkText      != null) matkText.text      = $"{s.MATK:0}";
            if (defText       != null) defText.text       = $"{s.DEF:0}";
            if (mdefText      != null) mdefText.text      = $"{s.MDEF:0}";
            if (aspdText      != null) aspdText.text      = $"{s.ASPD:0.0}";
            if (hitText       != null) hitText.text       = $"{s.HIT:0}";
            if (fleeText      != null) fleeText.text      = $"{s.FLEE:0}";
            if (critText      != null) critText.text      = $"{s.CRIT:0.0}%";
            if (hpregenText   != null) hpregenText.text   = $"{s.HPRegen:0.0}/5s";
            if (mpregenText   != null) mpregenText.text   = $"{s.MPRegen:0.0}/5s";
        }

        private void RefreshXPBar(CharacterData data)
        {
            if (xpBar != null)
            {
                xpBar.maxValue = Mathf.Max(1f, data.ExperienceToNextLevel);
                xpBar.value    = data.Experience;
            }
            if (xpText != null)
                xpText.text = $"{data.Experience} / {data.ExperienceToNextLevel} XP";
        }

        private void RefreshFreePointsBanner(CharacterData data)
        {
            bool hasPoints = data.FreeAttributePoints > 0;
            if (freePointsBanner != null)
                freePointsBanner.SetActive(hasPoints);
            if (freePointsText != null && hasPoints)
                freePointsText.text = data.FreeAttributePoints == 1
                    ? "1 ponto disponível!"
                    : $"{data.FreeAttributePoints} pontos disponíveis!";
        }

        /// <summary>
        /// Ativa os botões de + apenas quando há pontos livres.
        /// </summary>
        private void RefreshPlusButtons(CharacterData data)
        {
            bool can = data.FreeAttributePoints > 0;
            if (strPlusButton != null) strPlusButton.gameObject.SetActive(can);
            if (agiPlusButton != null) agiPlusButton.gameObject.SetActive(can);
            if (vitPlusButton != null) vitPlusButton.gameObject.SetActive(can);
            if (dexPlusButton != null) dexPlusButton.gameObject.SetActive(can);
            if (intPlusButton != null) intPlusButton.gameObject.SetActive(can);
            if (lukPlusButton != null) lukPlusButton.gameObject.SetActive(can);
        }

        // ─────────────────────────────────────────────────────────────────
        // Distribuição de Pontos
        // ─────────────────────────────────────────────────────────────────

        private enum Stat { STR, AGI, VIT, DEX, INT, LUK }

        private void AllocatePoint(Stat stat)
        {
            if (_player == null || !_player.IsInitialized) return;

            var data = _player.Data;
            if (data == null || data.FreeAttributePoints <= 0)
            {
                UIManager.Instance?.ShowMessage("Sem pontos disponíveis!");
                return;
            }

            // Decrementa o pool e incrementa o atributo alocado
            data.FreeAttributePoints--;

            switch (stat)
            {
                case Stat.STR: data.AllocatedSTR++; break;
                case Stat.AGI: data.AllocatedAGI++; break;
                case Stat.VIT: data.AllocatedVIT++; break;
                case Stat.DEX: data.AllocatedDEX++; break;
                case Stat.INT: data.AllocatedINT++; break;
                case Stat.LUK: data.AllocatedLUK++; break;
            }

            // Recalcula stats derivados e notifica o PlayerEntity
            _player.RefreshStats();

            // HP/MP: ajusta o atual se o máximo subiu (não deixa acima do novo máximo)
            // RefreshStats já chama OnStatsChanged → UIManager atualiza o HUD

            // Salva imediatamente
            var account = GameManager.Instance?.CurrentAccount;
            if (account != null)
                SaveManager.Instance?.SaveCharacter(account, data);

            // Atualiza a janela
            RefreshAll();

            Debug.Log($"[AttributeWindow] +1 {stat} | Pontos restantes: {data.FreeAttributePoints}");
        }

        // ─────────────────────────────────────────────────────────────────
        // Helpers
        // ─────────────────────────────────────────────────────────────────

        private static string RaceDisplayName(CharacterRace race)
        {
            return race switch
            {
                CharacterRace.Human  => "Humano",
                CharacterRace.Elf    => "Elfo",
                CharacterRace.Dwarf  => "Anão",
                CharacterRace.Orc    => "Orc",
                CharacterRace.Undead => "Morto-Vivo",
                _ => race.ToString()
            };
        }
		public void OnFreePointsUpdated(int newPoints)
{
    if (_player == null || !_player.IsInitialized) return;

    // Atualiza só o necessário (leve e eficiente)
    var data = _player.Data;
    if (data == null) return;

    data.FreeAttributePoints = newPoints;

    RefreshFreePointsBanner(data);
    RefreshPlusButtons(data);

    Debug.Log($"[AttributeWindowUI] FreePoints atualizado: {newPoints}");
}
    }
	
}