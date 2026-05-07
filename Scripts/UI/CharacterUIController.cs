using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using RPG.Data;
using RPG.Network;

namespace RPG.UI
{
    /// <summary>
    /// CharacterUIController v4
    ///
    /// CORREÇÃO v4:
    ///   SetAllButtonsInteractable(bool value) não passava o parâmetro 'value'
    ///   para os slots de personagem — sempre chamava SetInteractable(false),
    ///   impedindo re-habilitar os botões após um erro de seleção.
    ///
    ///   Agora o valor correto é propagado para todos os botões.
    /// </summary>
    public class CharacterUIController : MonoBehaviour
    {
        [Header("Panels")]
        [SerializeField] private GameObject selectionPanel;
        [SerializeField] private GameObject creationPanel;

        [Header("Selection Panel")]
        [SerializeField] private Transform  characterListContent;
        [SerializeField] private GameObject characterSlotPrefab;
        [SerializeField] private Button     createNewButton;
        [SerializeField] private Button     logoutButton;
        [SerializeField] private TMP_Text   selectionStatusText;

        [Header("Creation Panel")]
        [SerializeField] private TMP_InputField nameInput;
        [SerializeField] private TMP_Dropdown   raceDropdown;
        [SerializeField] private TMP_Text       raceInfoText;
        [SerializeField] private Button         createButton;
        [SerializeField] private Button         backButton;
        [SerializeField] private TMP_Text       errorText;

        private List<CharacterSummary> _cachedCharacters = new();

        private CharacterRace SelectedRace => (CharacterRace)raceDropdown.value;

        private void Start()
        {
            createNewButton.onClick.AddListener(ShowCreationPanel);
            logoutButton.onClick.AddListener(OnLogout);
            createButton.onClick.AddListener(OnCreateCharacter);
            backButton.onClick.AddListener(ShowSelectionPanel);
            raceDropdown.onValueChanged.AddListener(_ => UpdateRaceInfo());

            PopulateRaceDropdown();
            ShowSelectionPanel();

            if (ClientAuthHandler.Instance != null)
            {
                ClientAuthHandler.Instance.OnCharacterListReceived  += HandleCharacterList;
                ClientAuthHandler.Instance.OnCreateCharacterResult  += HandleCreateCharacterResult;
                ClientAuthHandler.Instance.OnSelectCharacterResult  += HandleSelectCharacterResult;
            }
            else
            {
                Debug.LogWarning("[CharacterUI] ClientAuthHandler não encontrado!");
                SetSelectionStatus("Erro: sem conexão com servidor.");
            }
        }

        private void OnDestroy()
        {
            if (ClientAuthHandler.Instance != null)
            {
                ClientAuthHandler.Instance.OnCharacterListReceived  -= HandleCharacterList;
                ClientAuthHandler.Instance.OnCreateCharacterResult  -= HandleCreateCharacterResult;
                ClientAuthHandler.Instance.OnSelectCharacterResult  -= HandleSelectCharacterResult;
            }
        }

        // ── Seleção ────────────────────────────────────────────────────────

        private void ShowSelectionPanel()
        {
            selectionPanel.SetActive(true);
            creationPanel.SetActive(false);
            SetSelectionStatus("Carregando personagens...");
            ClientAuthHandler.Instance?.SendRequestCharacterList();
        }

        private void HandleCharacterList(List<CharacterSummary> characters)
        {
            _cachedCharacters = characters ?? new List<CharacterSummary>();
            RefreshCharacterList();
            SetSelectionStatus(_cachedCharacters.Count == 0 ? "Nenhum personagem. Crie um!" : "");
        }

        private void RefreshCharacterList()
        {
            foreach (Transform child in characterListContent)
                Destroy(child.gameObject);

            foreach (var ch in _cachedCharacters)
            {
                var slot     = Instantiate(characterSlotPrefab, characterListContent);
                var nameText = slot.GetComponentInChildren<TMP_Text>();
                var btn      = slot.GetComponent<Button>();

                if (nameText != null)
                    nameText.text = $"{ch.CharacterName}  |  {ch.Race}  |  Lv {ch.Level}";

                var charId = ch.CharacterId;
                btn.onClick.AddListener(() => SelectCharacter(charId));
            }
        }

        private void SelectCharacter(string characterId)
        {
            SetSelectionStatus("Entrando no jogo...");
            SetAllButtonsInteractable(false);
            ClientAuthHandler.Instance?.SendSelectCharacter(characterId);
        }

        private void HandleSelectCharacterResult(bool success, string error)
        {
            if (!success)
            {
                SetSelectionStatus($"Erro: {error}");
                // CORREÇÃO: passa 'true' para re-habilitar os botões após erro
                SetAllButtonsInteractable(true);
            }
            // Se success: ClientAuthHandler carregou GameplayScene — esta cena some.
        }

        // ── Criação ────────────────────────────────────────────────────────

        private void ShowCreationPanel()
        {
            selectionPanel.SetActive(false);
            creationPanel.SetActive(true);
            nameInput.text     = "";
            if (errorText) errorText.text = "";
            raceDropdown.value = 0;
            UpdateRaceInfo();
        }

        private void OnCreateCharacter()
        {
            if (errorText) errorText.text = "";
            string charName = nameInput.text.Trim();

            if (charName.Length < 2)
            {
                if (errorText) errorText.text = "Nome: mínimo 2 caracteres.";
                return;
            }

            createButton.interactable = false;
            ClientAuthHandler.Instance?.SendCreateCharacter(charName, raceDropdown.value);
        }

        private void HandleCreateCharacterResult(bool success, string error, List<CharacterSummary> updatedList)
        {
            createButton.interactable = true;

            if (!success)
            {
                if (errorText) errorText.text = error ?? "Erro ao criar personagem.";
                return;
            }

            if (updatedList != null) _cachedCharacters = updatedList;
            ShowSelectionPanel();
        }

        // ── Race Dropdown ──────────────────────────────────────────────────

        private void PopulateRaceDropdown()
        {
            var options = new List<TMP_Dropdown.OptionData>();
            foreach (CharacterRace race in System.Enum.GetValues(typeof(CharacterRace)))
                options.Add(new TMP_Dropdown.OptionData(race.ToString()));
            raceDropdown.ClearOptions();
            raceDropdown.AddOptions(options);
        }

        private void UpdateRaceInfo()
        {
            var bonus = StatsCalculator.GetRaceBonus(SelectedRace);
            raceInfoText.text = SelectedRace switch
            {
                CharacterRace.Human  => $"<b>Humano</b> — Equilibrado.\n+{bonus.STR} STR +{bonus.AGI} AGI +{bonus.VIT} VIT +{bonus.DEX} DEX +{bonus.INT} INT +{bonus.LUK} LUK",
                CharacterRace.Elf    => $"<b>Elfo</b> — Magia e agilidade.\n+{bonus.AGI} AGI +{bonus.DEX} DEX +{bonus.INT} INT +{bonus.LUK} LUK",
                CharacterRace.Dwarf  => $"<b>Anão</b> — Resistente e forte.\n+{bonus.STR} STR +{bonus.VIT} VIT",
                CharacterRace.Orc    => $"<b>Orc</b> — Força bruta.\n+{bonus.STR} STR +{bonus.AGI} AGI +{bonus.VIT} VIT",
                CharacterRace.Undead => $"<b>Morto-Vivo</b> — Mago sombrio.\n+{bonus.STR} STR +{bonus.AGI} AGI +{bonus.DEX} DEX +{bonus.INT} INT",
                _ => ""
            };
        }

        // ── Logout ─────────────────────────────────────────────────────────

        private void OnLogout() => Managers.GameManager.Instance?.Logout();

        // ── Helpers ────────────────────────────────────────────────────────

        private void SetSelectionStatus(string msg)
        {
            if (selectionStatusText != null) selectionStatusText.text = msg;
        }

        /// <summary>
        /// CORREÇÃO v4: propaga o parâmetro 'value' corretamente para os slots.
        /// Antes sempre passava 'false', impedindo re-habilitar após erro.
        /// </summary>
        private void SetAllButtonsInteractable(bool value)
        {
            foreach (Transform child in characterListContent)
                child.GetComponent<Button>()?.SetInteractable(value); // CORRIGIDO: era false hardcoded
            if (createNewButton) createNewButton.interactable = value;
            if (logoutButton)    logoutButton.interactable    = value;
        }
    }

    public static class ButtonExtensions
    {
        public static void SetInteractable(this Button btn, bool value)
        {
            if (btn != null) btn.interactable = value;
        }
    }
}