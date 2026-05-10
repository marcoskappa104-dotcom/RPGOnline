using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Collections;

namespace RPG.UI
{
    /// <summary>
    /// DeathScreenUI v2
    ///
    /// CORREÇÕES v2:
    ///
    ///   BUG-18 — ReenableButton usava Invoke com string (memory leak se destruído):
    ///     `Invoke(nameof(ReenableButton), 1f)` lança MissingReferenceException se
    ///     o objeto for destruído antes do 1s passar.
    ///     SOLUÇÃO: substituído por Coroutine com verificação `if (this == null)`.
    ///
    ///   Todas as correções v1 mantidas (fade, cursor, servidor).
    /// </summary>
    public class DeathScreenUI : MonoBehaviour
    {
        public static DeathScreenUI Instance { get; private set; }

        [Header("Referências — arraste os objetos aqui no Inspector")]
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
        private CanvasGroup _canvasGroup;
        private float       _fadeTimer;
        private bool        _fadingIn;
        private bool        _isReady = false;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;

            // BUG-17: servidor dedicado não tem UI
            if (Application.isBatchMode) { enabled = false; return; }

            if (deathScreenPanel == null)
            {
                Debug.LogError("[DeathScreenUI] 'Death Screen Panel' não configurado no Inspector!");
                enabled = false;
                return;
            }

            _canvasGroup = deathScreenPanel.GetComponent<CanvasGroup>();
            if (_canvasGroup == null)
                _canvasGroup = deathScreenPanel.AddComponent<CanvasGroup>();

            deathScreenPanel.SetActive(false);
            _canvasGroup.alpha          = 0f;
            _canvasGroup.interactable   = false;
            _canvasGroup.blocksRaycasts = false;

            if (titleText    != null) titleText.text    = deathTitle;
            if (subtitleText != null) subtitleText.text = deathSubtitle;

            if (reviveButton != null)
            {
                var label = reviveButton.GetComponentInChildren<TMP_Text>();
                if (label != null) label.text = reviveLabel;
                reviveButton.onClick.RemoveAllListeners();
                reviveButton.onClick.AddListener(OnReviveClicked);
            }

            _isReady = true;
            Debug.Log("[DeathScreenUI] Configurado com sucesso.");
        }

        private void Update()
        {
            if (!_fadingIn || _canvasGroup == null) return;
            _fadeTimer += Time.unscaledDeltaTime;
            _canvasGroup.alpha = Mathf.Clamp01(_fadeTimer / fadeInDuration);
            if (_fadeTimer >= fadeInDuration) _fadingIn = false;
        }

        public static void Show(RPG.Network.NetworkPlayer localPlayer)
        {
            if (Instance == null || !Instance._isReady)
            {
                Debug.LogError("[DeathScreenUI] Não encontrado ou não configurado no Inspector!");
                return;
            }
            Instance.ShowInternal(localPlayer);
        }

        public static void Hide()
        {
            if (Instance != null && Instance._isReady)
                Instance.HideInternal();
        }

        private void ShowInternal(RPG.Network.NetworkPlayer localPlayer)
        {
            _localPlayer = localPlayer;
            deathScreenPanel.SetActive(true);
            _canvasGroup.alpha          = 0f;
            _canvasGroup.interactable   = true;
            _canvasGroup.blocksRaycasts = true;
            if (reviveButton != null) reviveButton.interactable = true;
            _fadeTimer = 0f;
            _fadingIn  = true;

            Cursor.visible   = true;
            Cursor.lockState = CursorLockMode.None;

            Debug.Log("[DeathScreenUI] Tela de morte exibida.");
        }

        private void HideInternal()
        {
            deathScreenPanel.SetActive(false);
            _canvasGroup.alpha          = 0f;
            _canvasGroup.interactable   = false;
            _canvasGroup.blocksRaycasts = false;
            _fadingIn    = false;
            _localPlayer = null;

            Cursor.visible   = true;
            Cursor.lockState = CursorLockMode.None;

            Debug.Log("[DeathScreenUI] Tela de morte escondida.");
        }

        private void OnReviveClicked()
        {
            if (_localPlayer == null) return;
            if (reviveButton != null) reviveButton.interactable = false;
            _localPlayer.CmdRequestRespawn();

            // BUG-18: Coroutine com null-check em vez de Invoke(string)
            StartCoroutine(ReenableButtonCoroutine());
        }

        /// <summary>
        /// BUG-18 CORRIGIDO: Coroutine com verificação de null-safety.
        /// Invoke(string, float) lança MissingReferenceException se o objeto
        /// for destruído antes do tempo acabar.
        /// </summary>
        private IEnumerator ReenableButtonCoroutine()
        {
            yield return new WaitForSecondsRealtime(1f);

            // Verifica se o objeto ainda existe antes de acessar membros
            if (this == null) yield break;
            if (reviveButton != null) reviveButton.interactable = true;
        }
    }
}
