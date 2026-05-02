using UnityEngine;
using UnityEngine.UI;
using TMPro;
using RPG.Managers;
using RPG.Data;

namespace RPG.UI
{
    /// <summary>
    /// LoginUIController — gerencia a tela de Login e Criação de Conta.
    /// 
    /// Hierarquia esperada no Canvas:
    ///   Canvas
    ///     LoginPanel
    ///       TitleText (TMP)
    ///       UsernameInput (TMP_InputField)
    ///       PasswordInput (TMP_InputField)
    ///       LoginButton (Button)
    ///       CreateAccountButton (Button)
    ///       ErrorText (TMP)
    ///     CreateAccountPanel
    ///       TitleText (TMP)
    ///       UsernameInput (TMP_InputField)
    ///       PasswordInput (TMP_InputField)
    ///       ConfirmPasswordInput (TMP_InputField)
    ///       SubmitButton (Button)
    ///       BackButton (Button)
    ///       ErrorText (TMP)
    ///       SuccessText (TMP)
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
            // Garante que só o painel de login aparece no início
            ShowLoginPanel();

            // Bind de eventos
            loginButton.onClick.AddListener(OnLoginClicked);
            openCreateAccountButton.onClick.AddListener(ShowCreateAccountPanel);
            submitCreateButton.onClick.AddListener(OnCreateAccountClicked);
            backToLoginButton.onClick.AddListener(ShowLoginPanel);

            // Permite usar Enter nos campos
            loginUsernameInput.onSubmit.AddListener(_ => OnLoginClicked());
            loginPasswordInput.onSubmit.AddListener(_ => OnLoginClicked());
        }

        // ── Navegação entre painéis ───────────────────────────────────────

        private void ShowLoginPanel()
        {
            loginPanel.SetActive(true);
            createAccountPanel.SetActive(false);
            loginErrorText.text = "";
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

        // ── Ações ─────────────────────────────────────────────────────────

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

            var account = SaveManager.Instance.TryLogin(user, pass);
            if (account == null)
            {
                loginErrorText.text = "Usuário ou senha incorretos.";
                return;
            }

            GameManager.Instance.SetAccount(account);
            GameManager.Instance.GoToCharacterSelect();
        }

        private void OnCreateAccountClicked()
        {
            createErrorText.text   = "";
            createSuccessText.text = "";

            string user    = createUsernameInput.text.Trim();
            string pass    = createPasswordInput.text;
            string confirm = createConfirmPasswordInput.text;

            // Validações locais
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

            string error = SaveManager.Instance.TryCreateAccount(user, pass);
            if (error != null)
            {
                createErrorText.text = error;
                return;
            }

            createSuccessText.text = "Conta criada! Faça login.";
            ClearCreateFields();
            Invoke(nameof(ShowLoginPanel), 1.5f);
        }

        // ── Helpers ───────────────────────────────────────────────────────

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
