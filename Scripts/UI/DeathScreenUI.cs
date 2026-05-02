using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace RPG.UI
{
    /// <summary>
    /// DeathScreenUI — tela de morte que aparece quando o jogador morre.
    ///
    /// CONFIGURAÇÃO NO CANVAS (GameplayScene):
    ///   Canvas
    ///     DeathScreen (Panel — filho do Canvas, desativado no início)
    ///       Background (Image — preto com alpha ~180)
    ///       Container (Panel centralizado)
    ///         TitleText (TMP_Text — "VOCÊ MORREU")
    ///         SubtitleText (TMP_Text — "Deseja reviver?")
    ///         ReviveButton (Button — "REVIVER")
    ///
    /// Adicione DeathScreenUI.cs no GameObject DeathScreen e configure os campos.
    ///
    /// ACESSO ESTÁTICO: DeathScreenUI.Show(networkPlayer) / DeathScreenUI.Hide()
    /// </summary>
    public class DeathScreenUI : MonoBehaviour
    {
        public static DeathScreenUI Instance { get; private set; }

        [Header("Referências")]
        [SerializeField] private GameObject deathScreenPanel;
        [SerializeField] private Button     reviveButton;
        [SerializeField] private TMP_Text   titleText;
        [SerializeField] private TMP_Text   subtitleText;

        [Header("Textos")]
        [SerializeField] private string deathTitle    = "VOCÊ MORREU";
        [SerializeField] private string deathSubtitle = "Deseja reviver?";
        [SerializeField] private string reviveLabel   = "REVIVER";

        [Header("Animação")]
        [SerializeField] private float fadeInDuration = 0.5f;

        private RPG.Network.NetworkPlayer _localPlayer;
        private CanvasGroup   _canvasGroup;
        private float         _fadeTimer;
        private bool          _fadingIn;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;

            _canvasGroup = deathScreenPanel?.GetComponent<CanvasGroup>();
            if (_canvasGroup == null && deathScreenPanel != null)
                _canvasGroup = deathScreenPanel.AddComponent<CanvasGroup>();

            // Começa escondida
            if (deathScreenPanel != null)
            {
                deathScreenPanel.SetActive(false);
                if (_canvasGroup != null) _canvasGroup.alpha = 0f;
            }

            SetupTexts();
            SetupButton();
        }

        private void SetupTexts()
        {
            if (titleText    != null) titleText.text    = deathTitle;
            if (subtitleText != null) subtitleText.text = deathSubtitle;
        }

        private void SetupButton()
        {
            if (reviveButton == null) return;

            var label = reviveButton.GetComponentInChildren<TMP_Text>();
            if (label != null) label.text = reviveLabel;

            reviveButton.onClick.RemoveAllListeners();
            reviveButton.onClick.AddListener(OnReviveClicked);
        }

        private void Update()
        {
            if (!_fadingIn || _canvasGroup == null) return;

            _fadeTimer += Time.unscaledDeltaTime;
            _canvasGroup.alpha = Mathf.Clamp01(_fadeTimer / fadeInDuration);

            if (_fadeTimer >= fadeInDuration)
                _fadingIn = false;
        }

        // ── API Estática ──────────────────────────────────────────────────

        /// <summary>
        /// Exibe a tela de morte associada ao player local.
        /// Chamado pelo NetworkPlayer.RpcPlayerDied() no cliente dono.
        /// </summary>
        public static void Show(RPG.Network.NetworkPlayer localPlayer)
        {
            if (Instance == null)
            {
                Debug.LogError("[DeathScreenUI] Instância não encontrada na cena! " +
                               "Adicione DeathScreenUI ao Canvas da GameplayScene.");
                return;
            }
            Instance.ShowInternal(localPlayer);
        }

        /// <summary>
        /// Esconde a tela de morte.
        /// Chamado pelo NetworkPlayer.RpcOnRespawned().
        /// </summary>
        public static void Hide()
        {
            if (Instance != null) Instance.HideInternal();
        }

        // ── Internos ──────────────────────────────────────────────────────

        private void ShowInternal(RPG.Network.NetworkPlayer localPlayer)
        {
            _localPlayer = localPlayer;

            if (deathScreenPanel != null)
                deathScreenPanel.SetActive(true);

            if (_canvasGroup != null)
            {
                _canvasGroup.alpha          = 0f;
                _canvasGroup.interactable   = true;
                _canvasGroup.blocksRaycasts = true;
            }

            _fadeTimer = 0f;
            _fadingIn  = true;

            Debug.Log("[DeathScreenUI] Tela de morte exibida.");
        }

        private void HideInternal()
        {
            if (deathScreenPanel != null)
                deathScreenPanel.SetActive(false);

            if (_canvasGroup != null)
            {
                _canvasGroup.alpha          = 0f;
                _canvasGroup.interactable   = false;
                _canvasGroup.blocksRaycasts = false;
            }

            _fadingIn    = false;
            _localPlayer = null;

            Debug.Log("[DeathScreenUI] Tela de morte escondida.");
        }

        private void OnReviveClicked()
        {
            if (_localPlayer == null)
            {
                Debug.LogWarning("[DeathScreenUI] Nenhum player local referenciado.");
                return;
            }

            // Desativa o botão para evitar clique duplo
            if (reviveButton != null) reviveButton.interactable = false;

            Debug.Log("[DeathScreenUI] Solicitando respawn...");
            _localPlayer.CmdRequestRespawn();

            // Reativa o botão após 1s caso o servidor demore
            Invoke(nameof(ReenableButton), 1f);
        }

        private void ReenableButton()
        {
            if (reviveButton != null) reviveButton.interactable = true;
        }
    }
}
