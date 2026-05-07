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
    /// AttributeWindowUI v3
    ///
    /// CORREÇÕES v3:
    ///   1. CRÍTICO: RefreshBaseAttributes agora lê BaseAttributes reais do
    ///      NetworkPlayer (BaseSTR..BaseLUK SyncVars) em vez de hardcodar BASE=10.
    ///      O valor 10 estava correto APENAS para personagens recém-criados.
    ///      Para raças com base diferente no futuro, ou personagens com base
    ///      modificada, a janela mostrava valores incorretos.
    ///
    ///   2. Sem outras alterações de lógica — apenas a correção do BASE.
    /// </summary>
    public class AttributeWindowUI : MonoBehaviour
    {
        public static AttributeWindowUI Instance { get; private set; }

        [Header("Painel")]
        [SerializeField] private GameObject windowPanel;

        [Header("Header")]
        [SerializeField] private TMP_Text charNameText;
        [SerializeField] private TMP_Text raceText;
        [SerializeField] private TMP_Text levelText;
        [SerializeField] private Button   closeButton;

        [Header("Pontos Livres")]
        [SerializeField] private GameObject freePointsBanner;
        [SerializeField] private TMP_Text   freePointsText;

        [Header("Atributos Base — Textos")]
        [SerializeField] private TMP_Text strValueText;
        [SerializeField] private TMP_Text agiValueText;
        [SerializeField] private TMP_Text vitValueText;
        [SerializeField] private TMP_Text dexValueText;
        [SerializeField] private TMP_Text intValueText;
        [SerializeField] private TMP_Text lukValueText;

        [Header("Atributos Base — Botões +")]
        [SerializeField] private Button strPlusButton;
        [SerializeField] private Button agiPlusButton;
        [SerializeField] private Button vitPlusButton;
        [SerializeField] private Button dexPlusButton;
        [SerializeField] private Button intPlusButton;
        [SerializeField] private Button lukPlusButton;

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

        [Header("XP")]
        [SerializeField] private Slider   xpBar;
        [SerializeField] private TMP_Text xpText;

        private PlayerEntity  _player;
        private NetworkPlayer _netPlayer;
        private bool          _isOpen;
        private bool          _allocating;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        private void Start()
        {
            if (windowPanel != null) windowPanel.SetActive(false);
            _isOpen = false;

            if (closeButton    != null) closeButton.onClick.AddListener(Close);
            if (strPlusButton  != null) strPlusButton.onClick.AddListener(() => RequestAllocate(0));
            if (agiPlusButton  != null) agiPlusButton.onClick.AddListener(() => RequestAllocate(1));
            if (vitPlusButton  != null) vitPlusButton.onClick.AddListener(() => RequestAllocate(2));
            if (dexPlusButton  != null) dexPlusButton.onClick.AddListener(() => RequestAllocate(3));
            if (intPlusButton  != null) intPlusButton.onClick.AddListener(() => RequestAllocate(4));
            if (lukPlusButton  != null) lukPlusButton.onClick.AddListener(() => RequestAllocate(5));
        }

        // ── Vínculo com PlayerEntity ───────────────────────────────────────

        public void BindPlayer(PlayerEntity player)
        {
            if (player == null) return;
            if (_player == player) return;

            if (_player != null)
            {
                _player.OnStatsChanged -= OnDataChanged;
                _player.OnInitialized  -= OnDataChanged;
                _player.OnHPChanged    -= OnHPMPChanged;
                _player.OnMPChanged    -= OnHPMPChanged;
            }

            _player    = player;
            _netPlayer = player.GetComponent<NetworkPlayer>();

            _player.OnStatsChanged += OnDataChanged;
            _player.OnInitialized  += OnDataChanged;
            _player.OnHPChanged    += OnHPMPChanged;
            _player.OnMPChanged    += OnHPMPChanged;

            if (player.IsInitialized) RefreshAll();
            Debug.Log($"[AttributeWindowUI] Vinculado a {player.Data?.CharacterName}");
        }

        private void OnDataChanged()
        {
            if (_isOpen) RefreshAll();
        }

        private void OnHPMPChanged(float _, float __)
        {
            if (!_isOpen || _player == null || !_player.IsInitialized) return;
            RefreshHPMP();
        }

        // ── Abrir / Fechar ─────────────────────────────────────────────────

        public void Toggle() { if (_isOpen) Close(); else Open(); }

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

        // ── Refresh ────────────────────────────────────────────────────────

        private void RefreshAll()
        {
            if (_player == null || !_player.IsInitialized) return;
            var data  = _player.Data;
            var stats = _player.Stats;
            if (data == null || stats == null) return;

            int  level      = _netPlayer != null ? _netPlayer.Level                : data.Level;
            long exp        = _netPlayer != null ? _netPlayer.Experience            : data.Experience;
            long expToNext  = _netPlayer != null ? _netPlayer.ExperienceToNextLevel : data.ExperienceToNextLevel;
            int  freePoints = _netPlayer != null ? _netPlayer.FreeAttributePoints   : data.FreeAttributePoints;
            int  allocSTR   = _netPlayer != null ? _netPlayer.AllocatedSTR : data.AllocatedSTR;
            int  allocAGI   = _netPlayer != null ? _netPlayer.AllocatedAGI : data.AllocatedAGI;
            int  allocVIT   = _netPlayer != null ? _netPlayer.AllocatedVIT : data.AllocatedVIT;
            int  allocDEX   = _netPlayer != null ? _netPlayer.AllocatedDEX : data.AllocatedDEX;
            int  allocINT   = _netPlayer != null ? _netPlayer.AllocatedINT : data.AllocatedINT;
            int  allocLUK   = _netPlayer != null ? _netPlayer.AllocatedLUK : data.AllocatedLUK;

            // CORREÇÃO CRÍTICA v3: lê BaseAttributes reais do servidor via SyncVars
            // Em vez de hardcodar BASE=10, usa os valores confirmados pelo servidor.
            int baseSTR = _netPlayer != null ? _netPlayer.BaseSTR : data.BaseAttributes.STR;
            int baseAGI = _netPlayer != null ? _netPlayer.BaseAGI : data.BaseAttributes.AGI;
            int baseVIT = _netPlayer != null ? _netPlayer.BaseVIT : data.BaseAttributes.VIT;
            int baseDEX = _netPlayer != null ? _netPlayer.BaseDEX : data.BaseAttributes.DEX;
            int baseINT = _netPlayer != null ? _netPlayer.BaseINT : data.BaseAttributes.INT;
            int baseLUK = _netPlayer != null ? _netPlayer.BaseLUK : data.BaseAttributes.LUK;

            RefreshHeader(data.CharacterName, data.Race, level);
            RefreshBaseAttributes(
                data.Race,
                baseSTR, baseAGI, baseVIT, baseDEX, baseINT, baseLUK,
                allocSTR, allocAGI, allocVIT, allocDEX, allocINT, allocLUK);
            RefreshDerivedStats(stats, _player.CurrentHP, _player.CurrentMP);
            RefreshXPBar(exp, expToNext);
            RefreshFreePointsBanner(freePoints);
            RefreshPlusButtons(freePoints);
        }

        private void RefreshHPMP()
        {
            if (_player?.Stats == null) return;
            if (hpDerivedText != null)
                hpDerivedText.text = $"{_player.CurrentHP:0} / {_player.Stats.MaxHP:0}";
            if (mpDerivedText != null)
                mpDerivedText.text = $"{_player.CurrentMP:0} / {_player.Stats.MaxMP:0}";
        }

        private void RefreshHeader(string charName, CharacterRace race, int level)
        {
            if (charNameText != null) charNameText.text = charName;
            if (raceText     != null) raceText.text     = RaceDisplayName(race);
            if (levelText    != null) levelText.text    = $"Nível {level}";
        }

        /// <summary>
        /// CORREÇÃO v3: agora recebe baseSTR..baseLUK reais do servidor.
        /// Exibe: total = base + raceBonus + allocado, bônus entre parênteses.
        /// </summary>
        private void RefreshBaseAttributes(
            CharacterRace race,
            int baseSTR, int baseAGI, int baseVIT,
            int baseDEX, int baseINT, int baseLUK,
            int allocSTR, int allocAGI, int allocVIT,
            int allocDEX, int allocINT, int allocLUK)
        {
            var bonus = StatsCalculator.GetRaceBonus(race);

            int totalSTR = baseSTR + bonus.STR + allocSTR;
            int totalAGI = baseAGI + bonus.AGI + allocAGI;
            int totalVIT = baseVIT + bonus.VIT + allocVIT;
            int totalDEX = baseDEX + bonus.DEX + allocDEX;
            int totalINT = baseINT + bonus.INT + allocINT;
            int totalLUK = baseLUK + bonus.LUK + allocLUK;

            int bonusSTR = bonus.STR + allocSTR;
            int bonusAGI = bonus.AGI + allocAGI;
            int bonusVIT = bonus.VIT + allocVIT;
            int bonusDEX = bonus.DEX + allocDEX;
            int bonusINT = bonus.INT + allocINT;
            int bonusLUK = bonus.LUK + allocLUK;

            SetAttrText(strValueText, totalSTR, bonusSTR);
            SetAttrText(agiValueText, totalAGI, bonusAGI);
            SetAttrText(vitValueText, totalVIT, bonusVIT);
            SetAttrText(dexValueText, totalDEX, bonusDEX);
            SetAttrText(intValueText, totalINT, bonusINT);
            SetAttrText(lukValueText, totalLUK, bonusLUK);
        }

        private void SetAttrText(TMP_Text label, int total, int bonus)
        {
            if (label == null) return;
            label.text = bonus > 0
                ? $"{total} <color=#88FF88>(+{bonus})</color>"
                : $"{total}";
        }

        private void RefreshDerivedStats(DerivedStats s, float hp, float mp)
        {
            if (hpDerivedText != null) hpDerivedText.text = $"{hp:0} / {s.MaxHP:0}";
            if (mpDerivedText != null) mpDerivedText.text = $"{mp:0} / {s.MaxMP:0}";
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

        public void RefreshXPBar(long exp, long expToNext)
        {
            if (xpBar != null)
            {
                xpBar.maxValue = Mathf.Max(1f, expToNext);
                xpBar.value    = exp;
            }
            if (xpText != null) xpText.text = $"{exp} / {expToNext} XP";
        }

        private void RefreshFreePointsBanner(int freePoints)
        {
            bool has = freePoints > 0;
            if (freePointsBanner != null) freePointsBanner.SetActive(has);
            if (freePointsText != null && has)
                freePointsText.text = freePoints == 1
                    ? "1 ponto disponível!"
                    : $"{freePoints} pontos disponíveis!";
        }

        private void RefreshPlusButtons(int freePoints)
        {
            bool can = freePoints > 0 && !_allocating;
            if (strPlusButton != null) strPlusButton.gameObject.SetActive(can);
            if (agiPlusButton != null) agiPlusButton.gameObject.SetActive(can);
            if (vitPlusButton != null) vitPlusButton.gameObject.SetActive(can);
            if (dexPlusButton != null) dexPlusButton.gameObject.SetActive(can);
            if (intPlusButton != null) intPlusButton.gameObject.SetActive(can);
            if (lukPlusButton != null) lukPlusButton.gameObject.SetActive(can);
        }

        // ── Alocação de Pontos ─────────────────────────────────────────────

        private void RequestAllocate(int attributeIndex)
        {
            if (_allocating) return;
            if (_netPlayer == null)
            {
                UIManager.Instance?.ShowMessage("Alocação requer conexão com o servidor.");
                return;
            }
            if (_netPlayer.FreeAttributePoints <= 0)
            {
                UIManager.Instance?.ShowMessage("Sem pontos disponíveis!");
                return;
            }

            _allocating = true;
            SetPlusButtonsInteractable(false);
            _netPlayer.CmdAllocateAttribute(attributeIndex);
            Invoke(nameof(FinishAllocating), 0.5f);
        }

        private void FinishAllocating()
        {
            _allocating = false;
            if (_player != null)
                RefreshPlusButtons(_netPlayer != null ? _netPlayer.FreeAttributePoints : 0);
            SetPlusButtonsInteractable(true);
        }

        private void SetPlusButtonsInteractable(bool value)
        {
            if (strPlusButton != null) strPlusButton.interactable = value;
            if (agiPlusButton != null) agiPlusButton.interactable = value;
            if (vitPlusButton != null) vitPlusButton.interactable = value;
            if (dexPlusButton != null) dexPlusButton.interactable = value;
            if (intPlusButton != null) intPlusButton.interactable = value;
            if (lukPlusButton != null) lukPlusButton.interactable = value;
        }

        public void OnFreePointsUpdated(int newPoints)
        {
            RefreshFreePointsBanner(newPoints);
            RefreshPlusButtons(newPoints);
        }

        private static string RaceDisplayName(CharacterRace race) => race switch
        {
            CharacterRace.Human  => "Humano",
            CharacterRace.Elf    => "Elfo",
            CharacterRace.Dwarf  => "Anão",
            CharacterRace.Orc    => "Orc",
            CharacterRace.Undead => "Morto-Vivo",
            _ => race.ToString()
        };
    }
}