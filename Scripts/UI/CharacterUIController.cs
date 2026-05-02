using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using RPG.Data;
using RPG.Managers;

namespace RPG.UI
{
    /// <summary>
    /// CharacterUIController — tela de Criação e Seleção de Personagens.
    ///
    /// Hierarquia sugerida:
    ///   Canvas
    ///     SelectionPanel
    ///       TitleText
    ///       CharacterListContent (Layout Group com CharacterSlot prefabs)
    ///       CreateNewButton
    ///       LogoutButton
    ///     CreationPanel
    ///       TitleText
    ///       NameInput
    ///       RaceDropdown
    ///       RaceInfoText
    ///       CreateButton
    ///       BackButton
    ///       ErrorText
    ///
    /// Prefab CharacterSlot: Button com filho TMP_Text (CharacterNameText)
    /// </summary>
    public class CharacterUIController : MonoBehaviour
    {
        [Header("Panels")]
        [SerializeField] private GameObject selectionPanel;
        [SerializeField] private GameObject creationPanel;

        [Header("Selection Panel")]
        [SerializeField] private Transform  characterListContent;   // pai dos slots
        [SerializeField] private GameObject characterSlotPrefab;    // prefab do botão
        [SerializeField] private Button     createNewButton;
        [SerializeField] private Button     logoutButton;

        [Header("Creation Panel")]
        [SerializeField] private TMP_InputField nameInput;
        [SerializeField] private TMP_Dropdown   raceDropdown;
        [SerializeField] private TMP_Text       raceInfoText;
        [SerializeField] private Button         createButton;
        [SerializeField] private Button         backButton;
        [SerializeField] private TMP_Text       errorText;

        private CharacterRace SelectedRace => (CharacterRace)raceDropdown.value;

        private void Start()
        {
            createNewButton.onClick.AddListener(ShowCreationPanel);
            logoutButton.onClick.AddListener(() => GameManager.Instance.Logout());
            createButton.onClick.AddListener(OnCreateCharacter);
            backButton.onClick.AddListener(ShowSelectionPanel);
            raceDropdown.onValueChanged.AddListener(_ => UpdateRaceInfo());

            PopulateRaceDropdown();
            ShowSelectionPanel();
        }

        // ── Seleção ───────────────────────────────────────────────────────

        private void ShowSelectionPanel()
        {
            selectionPanel.SetActive(true);
            creationPanel.SetActive(false);
            PopulateCharacterList();
        }

        private void PopulateCharacterList()
        {
            // Limpa slots anteriores
            foreach (Transform child in characterListContent)
                Destroy(child.gameObject);

            var account = GameManager.Instance.CurrentAccount;
            if (account == null) return;

            foreach (var ch in account.Characters)
            {
                var slot      = Instantiate(characterSlotPrefab, characterListContent);
                var nameText  = slot.GetComponentInChildren<TMP_Text>();
                var btn       = slot.GetComponent<Button>();

                if (nameText != null)
                    nameText.text = $"{ch.CharacterName}  |  {ch.Race}  |  Lv {ch.Level}";

                var charRef = ch; // closure
                btn.onClick.AddListener(() => SelectCharacter(charRef));
            }
        }

        private void SelectCharacter(CharacterData character)
        {
            GameManager.Instance.SetSelectedCharacter(character);
            GameManager.Instance.GoToGameplay();
        }

        // ── Criação ───────────────────────────────────────────────────────

        private void ShowCreationPanel()
        {
            selectionPanel.SetActive(false);
            creationPanel.SetActive(true);
            nameInput.text  = "";
            errorText.text  = "";
            raceDropdown.value = 0;
            UpdateRaceInfo();
        }

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
                CharacterRace.Human  => $"<b>Humano</b> — Equilibrado em tudo.\n+{bonus.STR} STR +{bonus.AGI} AGI +{bonus.VIT} VIT +{bonus.DEX} DEX +{bonus.INT} INT +{bonus.LUK} LUK",
                CharacterRace.Elf    => $"<b>Elfo</b> — Mestre em magia e agilidade.\n+{bonus.AGI} AGI +{bonus.DEX} DEX +{bonus.INT} INT +{bonus.LUK} LUK",
                CharacterRace.Dwarf  => $"<b>Anão</b> — Resistente e forte.\n+{bonus.STR} STR +{bonus.VIT} VIT",
                CharacterRace.Orc    => $"<b>Orc</b> — Força bruta máxima.\n+{bonus.STR} STR +{bonus.AGI} AGI +{bonus.VIT} VIT",
                CharacterRace.Undead => $"<b>Morto-Vivo</b> — Mago sombrio.\n+{bonus.STR} STR +{bonus.AGI} AGI +{bonus.DEX} DEX +{bonus.INT} INT",
                _ => ""
            };
        }

        private void OnCreateCharacter()
        {
            errorText.text = "";
            string charName = nameInput.text.Trim();

            string error = SaveManager.Instance.TryCreateCharacter(
                GameManager.Instance.CurrentAccount, charName, SelectedRace);

            if (error != null)
            {
                errorText.text = error;
                return;
            }

            // Recarrega conta do disco para pegar o personagem novo
            var refreshed = SaveManager.Instance.LoadAccount(
                GameManager.Instance.CurrentAccount.Username);
            GameManager.Instance.SetAccount(refreshed);

            ShowSelectionPanel();
        }
    }
}
