using UnityEngine;
using UnityEngine.UI;
using TMPro;
using RPG.Managers;
using RPG.Network;

namespace RPG.UI
{
    /// <summary>
    /// LoginUIController v2 — Server-Authoritative
    ///
    /// MUDANÇAS:
    ///   - Não acessa SaveManager diretamente — tudo via ClientAuthHandler.
    ///   - Ao fazer login com sucesso, o servidor envia a lista de personagens
    ///     e o GameManager navega para CharacterScene.
    ///   - Ao criar conta, mostra sucesso e volta ao painel de login.
    /// </summary>
    public class LoginUIController : MonoBehaviour
    {
        [Header("Panels")]
        [SerializeField] private GameObject loginPanel;
        [SerializeField] private GameObject createAccountPanel;

        [Header("Login Fields")]
        [SerializeField] private TMP_InputField loginUsernameInput;
        [SerializeField] private TMP_InputField loginPasswordInput;
        [SerializeField] private Button         loginButton;
        [SerializeField] private Button         openCreateAccountButton;
        [SerializeField] private TMP_Text       loginErrorText;
        [SerializeField] private TMP_Text       loginStatusText; // "Conectando..."

        [Header("Create Account Fields")]
        [SerializeField] private TMP_InputField createUsernameInput;
        [SerializeField] private TMP_InputField createPasswordInput;
        [SerializeField] private TMP_InputField createConfirmPasswordInput;
        [SerializeField] private Button         submitCreateButton;
        [SerializeField] private Button         backToLoginButton;
        [SerializeField] private TMP_Text       createErrorText;
        [SerializeField] private TMP_Text       createSuccessText;

        private void Start()
        {
            ShowLoginPanel();

            loginButton.onClick.AddListener(OnLoginClicked);
            openCreateAccountButton.onClick.AddListener(ShowCreateAccountPanel);
            submitCreateButton.onClick.AddListener(OnCreateAccountClicked);
            backToLoginButton.onClick.AddListener(ShowLoginPanel);

            loginUsernameInput.onSubmit.AddListener(_ => OnLoginClicked());
            loginPasswordInput.onSubmit.AddListener(_ => OnLoginClicked());

            // Vincula aos eventos do ClientAuthHandler
            if (ClientAuthHandler.Instance != null)
            {
                ClientAuthHandler.Instance.OnLoginResult        += HandleLoginResult;
                ClientAuthHandler.Instance.OnCreateAccountResult += HandleCreateAccountResult;
            }
            else
            {
                Debug.LogWarning("[LoginUI] ClientAuthHandler não encontrado na cena!");
            }

            SetStatus("");
        }

        private void OnDestroy()
        {
            if (ClientAuthHandler.Instance != null)
            {
                ClientAuthHandler.Instance.OnLoginResult        -= HandleLoginResult;
                ClientAuthHandler.Instance.OnCreateAccountResult -= HandleCreateAccountResult;
            }
        }

        // ── Painéis ───────────────────────────────────────────────────

        private void ShowLoginPanel()
        {
            loginPanel.SetActive(true);
            createAccountPanel.SetActive(false);
            loginErrorText.text = "";
            SetStatus("");
            ClearLoginFields();
        }

        private void ShowCreateAccountPanel()
        {
            loginPanel.SetActive(false);
            createAccountPanel.SetActive(true);
            createErrorText.text   = "";
            createSuccessText.text = "";
            ClearCreateFields();
        }

        // ── Ações ─────────────────────────────────────────────────────

        private void OnLoginClicked()
        {
            loginErrorText.text = "";
            string user = loginUsernameInput.text.Trim();
            string pass = loginPasswordInput.text;

            if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass))
            {
                loginErrorText.text = "Preencha usuário e senha.";
                return;
            }

            SetStatus("Autenticando...");
            SetInputsInteractable(false);
            ClientAuthHandler.Instance?.SendLogin(user, pass);
        }

        private void OnCreateAccountClicked()
        {
            createErrorText.text   = "";
            createSuccessText.text = "";

            string user    = createUsernameInput.text.Trim();
            string pass    = createPasswordInput.text;
            string confirm = createConfirmPasswordInput.text;

            if (user.Length < 4)
            {
                createErrorText.text = "Username deve ter ao menos 4 caracteres.";
                return;
            }
            if (string.IsNullOrWhiteSpace(pass))
            {
                createErrorText.text = "Digite uma senha.";
                return;
            }
            if (pass != confirm)
            {
                createErrorText.text = "As senhas não coincidem.";
                return;
            }

            submitCreateButton.interactable = false;
            ClientAuthHandler.Instance?.SendCreateAccount(user, pass);
        }

        // ── Handlers de resposta ──────────────────────────────────────

        private void HandleLoginResult(bool success, string error)
        {
            SetInputsInteractable(true);
            SetStatus("");

            if (success)
            {
                // Servidor confirmou login e enviará lista de personagens automaticamente.
                // CharacterScene é carregada pelo ClientAuthHandler.OnCharacterListReceived
                // via GameManager.GoToCharacterSelect() chamado pela CharacterUIController.
                GameManager.Instance?.GoToCharacterSelect();
            }
            else
            {
                loginErrorText.text = error ?? "Erro de login.";
            }
        }

        private void HandleCreateAccountResult(bool success, string error)
        {
            submitCreateButton.interactable = true;
            if (success)
            {
                createSuccessText.text = "Conta criada com sucesso! Faça login.";
                ClearCreateFields();
                Invoke(nameof(ShowLoginPanel), 1.5f);
            }
            else
            {
                createErrorText.text = error ?? "Erro ao criar conta.";
            }
        }

        // ── Helpers ───────────────────────────────────────────────────

        private void SetStatus(string msg)
        {
            if (loginStatusText != null) loginStatusText.text = msg;
        }

        private void SetInputsInteractable(bool value)
        {
            loginButton.interactable             = value;
            openCreateAccountButton.interactable = value;
            loginUsernameInput.interactable      = value;
            loginPasswordInput.interactable      = value;
        }

        private void ClearLoginFields()
        {
            loginUsernameInput.text = "";
            loginPasswordInput.text = "";
        }

        private void ClearCreateFields()
        {
            createUsernameInput.text        = "";
            createPasswordInput.text        = "";
            createConfirmPasswordInput.text = "";
        }
    }
}
