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
    /// AttributeWindowUI v2 — Janela de Atributos do Personagem.
    ///
    /// CORREÇÕES v2:
    ///   1. RefreshAll() removido do Update() — não precisa recalcular todo frame.
    ///      Agora atualiza apenas via eventos (OnInitialized, OnStatsChanged,
    ///      OnHPChanged, OnMPChanged) e quando a janela abre.
    ///      Antes: ~60 recálculos/segundo enquanto a janela estava aberta.
    ///      Agora: 0 no idle, atualiza só quando muda algo.
    ///
    ///   2. BindPlayer verifica se já está vinculado ao mesmo player antes de
    ///      resubscrever eventos (evita handlers duplicados).
    ///
    ///   3. Anti-spam de alocação aumentado para 0.5 s e usa botão interactable
    ///      em vez de timer para feedback visual correto.
    /// </summary>
    public class AttributeWindowUI : MonoBehaviour
    {
        public static AttributeWindowUI Instance { get; private set; }

        // ── Painel principal ───────────────────────────────────────────────
        [Header("Painel")]
        [SerializeField] private GameObject windowPanel;

        // ── Header ─────────────────────────────────────────────────────────
        [Header("Header")]
        [SerializeField] private TMP_Text charNameText;
        [SerializeField] private TMP_Text raceText;
        [SerializeField] private TMP_Text levelText;
        [SerializeField] private Button   closeButton;

        // ── Pontos livres ──────────────────────────────────────────────────
        [Header("Pontos Livres")]
        [SerializeField] private GameObject freePointsBanner;
        [SerializeField] private TMP_Text   freePointsText;

        // ── Atributos Base ─────────────────────────────────────────────────
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

        // ── Status Derivados ───────────────────────────────────────────────
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

        // ── XP ─────────────────────────────────────────────────────────────
        [Header("XP")]
        [SerializeField] private Slider   xpBar;
        [SerializeField] private TMP_Text xpText;

        // ── Estado interno ─────────────────────────────────────────────────
        private PlayerEntity  _player;
        private NetworkPlayer _netPlayer;
        private bool          _isOpen;
        private bool          _allocating; // previne spam durante request ao servidor

        // ── Lifecycle ──────────────────────────────────────────────────────

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        private void Start()
        {
            if (windowPanel != null) windowPanel.SetActive(false);
            _isOpen = false;

            if (closeButton != null)    closeButton.onClick.AddListener(Close);
            if (strPlusButton != null)  strPlusButton.onClick.AddListener(() => RequestAllocate(0));
            if (agiPlusButton != null)  agiPlusButton.onClick.AddListener(() => RequestAllocate(1));
            if (vitPlusButton != null)  vitPlusButton.onClick.AddListener(() => RequestAllocate(2));
            if (dexPlusButton != null)  dexPlusButton.onClick.AddListener(() => RequestAllocate(3));
            if (intPlusButton != null)  intPlusButton.onClick.AddListener(() => RequestAllocate(4));
            if (lukPlusButton != null)  lukPlusButton.onClick.AddListener(() => RequestAllocate(5));
        }

        // ── Vínculo com PlayerEntity ───────────────────────────────────────

        public void BindPlayer(PlayerEntity player)
        {
            if (player == null) return;
            if (_player == player) return; // já vinculado ao mesmo player

            // Desvincula anterior
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
            // Atualiza apenas os campos de HP/MP sem recalcular tudo
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
            RefreshAll(); // Refresh completo ao abrir
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

            int  level      = _netPlayer != null ? _netPlayer.Level                  : data.Level;
            long exp        = _netPlayer != null ? _netPlayer.Experience              : data.Experience;
            long expToNext  = _netPlayer != null ? _netPlayer.ExperienceToNextLevel   : data.ExperienceToNextLevel;
            int  freePoints = _netPlayer != null ? _netPlayer.FreeAttributePoints     : data.FreeAttributePoints;
            int  allocSTR   = _netPlayer != null ? _netPlayer.AllocatedSTR : data.AllocatedSTR;
            int  allocAGI   = _netPlayer != null ? _netPlayer.AllocatedAGI : data.AllocatedAGI;
            int  allocVIT   = _netPlayer != null ? _netPlayer.AllocatedVIT : data.AllocatedVIT;
            int  allocDEX   = _netPlayer != null ? _netPlayer.AllocatedDEX : data.AllocatedDEX;
            int  allocINT   = _netPlayer != null ? _netPlayer.AllocatedINT : data.AllocatedINT;
            int  allocLUK   = _netPlayer != null ? _netPlayer.AllocatedLUK : data.AllocatedLUK;

            RefreshHeader(data.CharacterName, data.Race, level);
            RefreshBaseAttributes(data.Race, allocSTR, allocAGI, allocVIT, allocDEX, allocINT, allocLUK);
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

        private void RefreshBaseAttributes(CharacterRace race,
            int allocSTR, int allocAGI, int allocVIT,
            int allocDEX, int allocINT, int allocLUK)
        {
            var bonus = StatsCalculator.GetRaceBonus(race);
            const int BASE = 10;

            SetAttrText(strValueText, BASE + bonus.STR + allocSTR, bonus.STR + allocSTR);
            SetAttrText(agiValueText, BASE + bonus.AGI + allocAGI, bonus.AGI + allocAGI);
            SetAttrText(vitValueText, BASE + bonus.VIT + allocVIT, bonus.VIT + allocVIT);
            SetAttrText(dexValueText, BASE + bonus.DEX + allocDEX, bonus.DEX + allocDEX);
            SetAttrText(intValueText, BASE + bonus.INT + allocINT, bonus.INT + allocINT);
            SetAttrText(lukValueText, BASE + bonus.LUK + allocLUK, bonus.LUK + allocLUK);
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

        private void RefreshXPBar(long exp, long expToNext)
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

            // Reabilita após timeout (SyncVar hook atualiza de verdade)
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

        // ── Chamado pelo SyncVar hook OnNetFreePointsChanged ───────────────

        public void OnFreePointsUpdated(int newPoints)
        {
            RefreshFreePointsBanner(newPoints);
            RefreshPlusButtons(newPoints);
        }

        // ── Helpers ────────────────────────────────────────────────────────

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