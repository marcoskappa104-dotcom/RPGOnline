using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Collections;

namespace RPG.UI
{
    /// <summary>
    /// SkillSlotUI v3 — Slot visual individual da barra de skills (Q / W / E / R).
    ///
    /// ════════════════════════════════════════════════════════════════════
    /// PARA QUE SERVE ESTE SCRIPT?
    /// ════════════════════════════════════════════════════════════════════
    ///
    /// Cada tecla de skill (Q, W, E, R) tem um slot na HUD. Este script
    /// controla SOMENTE o visual desse slot. Ele não decide quando atacar,
    /// não fala com o servidor, não conhece monstros — só exibe:
    ///
    ///   • Ícone da skill (lido da Joia do Poder equipada via UIManager).
    ///   • Overlay de cooldown (quadrante giratório que cobre o ícone).
    ///   • Texto com os segundos restantes de cooldown (ex: "2.4").
    ///   • Texto do hotkey no canto (ex: "Q").
    ///
    /// QUEM CONTROLA ESTE SCRIPT?
    ///   → UIManager.InitSkillBar()       define ícone e hotkey ao iniciar/trocar joia.
    ///   → UIManager.OnSkillCooldown()    chama StartCooldown() quando o servidor confirma uso.
    ///   → SkillSystem.OnCooldownStarted  é o evento que dispara o cooldown.
    ///
    /// PREFAB SUGERIDO (hierarquia):
    ///   SkillSlot_Q (RectTransform + SkillSlotUI)
    ///     ├── IconImage       (Image — ícone da skill)
    ///     ├── CooldownOverlay (Image — fillMethod Radial360, começa desativado)
    ///     ├── CooldownText    (TMP_Text — segundos restantes, some ao chegar em 0)
    ///     └── HotkeyText      (TMP_Text — ex: "Q", fixo)
    ///
    /// ════════════════════════════════════════════════════════════════════
    ///
    /// CORREÇÕES v3:
    ///   - Comentário incorreto sobre WaitForEndOfFrame removido: a coroutine
    ///     usa yield return null (um frame por tick), que é o correto aqui.
    ///     WaitForEndOfFrame não era adequado porque atrasaria a primeira
    ///     atualização de fillAmount e causaria flash visual.
    ///   - StopCooldown() adicionado para cancelar cooldown externamente
    ///     (útil na morte do jogador ou troca de cena).
    ///   - SetIcon(null) desativa o Image para não exibir quadrado branco.
    ///   - Cooldown agora zera corretamente se StartCooldown() for chamado
    ///     durante um cooldown em andamento (cancela o anterior).
    /// </summary>
    public class SkillSlotUI : MonoBehaviour
    {
        [Header("Referências visuais")]
        [SerializeField] private Image    iconImage;
        [SerializeField] private Image    cooldownOverlay;
        [SerializeField] private TMP_Text cooldownText;
        [SerializeField] private TMP_Text hotkeyText;

        private float     _totalCooldown;
        private float     _remainingCooldown;
        private Coroutine _cooldownCoroutine;

        /// <summary>True enquanto o cooldown visual estiver ativo.</summary>
        public bool OnCooldown { get; private set; }

        // ── Lifecycle ──────────────────────────────────────────────────────

        private void Awake()
        {
            // Garante que o overlay começa desativado e zerado
            if (cooldownOverlay != null)
            {
                cooldownOverlay.type       = Image.Type.Filled;
                cooldownOverlay.fillMethod = Image.FillMethod.Radial360;
                cooldownOverlay.fillOrigin = (int)Image.Origin360.Top;  // inicia pelo topo (12h)
                cooldownOverlay.fillAmount = 0f;
                cooldownOverlay.enabled    = false;
            }

            if (cooldownText != null)
                cooldownText.text = "";
        }

        // ── API pública ────────────────────────────────────────────────────

        /// <summary>
        /// Define o ícone da skill exibido no slot.
        /// Passe null para limpar (desativa o Image para não mostrar quadrado branco).
        /// Chamado pelo UIManager ao inicializar a SkillBar ou trocar de joia.
        /// </summary>
        public void SetIcon(Sprite icon)
        {
            if (iconImage == null) return;

            if (icon == null)
            {
                iconImage.sprite  = null;
                iconImage.enabled = false;
            }
            else
            {
                iconImage.sprite  = icon;
                iconImage.enabled = true;
            }
        }

        /// <summary>
        /// Define o texto do hotkey exibido no canto do slot (ex: "Q", "W", "E", "R").
        /// Chamado pelo UIManager.InitSkillBar().
        /// </summary>
        public void SetHotkey(string key)
        {
            if (hotkeyText != null)
                hotkeyText.text = key;
        }

        /// <summary>
        /// Inicia a animação visual de cooldown.
        /// Se já houver um cooldown em andamento, ele é cancelado e reiniciado.
        ///
        /// Chamado por UIManager.OnSkillCooldown(), que escuta o evento
        /// SkillSystem.OnCooldownStarted disparado quando o servidor confirma a skill.
        /// </summary>
        public void StartCooldown(float duration)
        {
            if (duration <= 0f) return;

            // Cancela cooldown anterior se ainda estiver rodando
            if (_cooldownCoroutine != null)
            {
                StopCoroutine(_cooldownCoroutine);
                _cooldownCoroutine = null;
            }

            _totalCooldown     = duration;
            _remainingCooldown = duration;
            OnCooldown         = true;

            _cooldownCoroutine = StartCoroutine(CooldownCoroutine());
        }

        /// <summary>
        /// Para o cooldown visual imediatamente.
        /// Útil ao morrer, trocar de cena, ou desequipar a joia.
        /// </summary>
        public void StopCooldown()
        {
            if (_cooldownCoroutine != null)
            {
                StopCoroutine(_cooldownCoroutine);
                _cooldownCoroutine = null;
            }

            _remainingCooldown = 0f;
            OnCooldown         = false;

            if (cooldownOverlay != null)
            {
                cooldownOverlay.fillAmount = 0f;
                cooldownOverlay.enabled    = false;
            }

            if (cooldownText != null)
                cooldownText.text = "";
        }

        // ── Coroutine de cooldown ──────────────────────────────────────────

        /// <summary>
        /// Atualiza o overlay de cooldown frame a frame.
        ///
        /// SOBRE O yield return null:
        ///   Usamos yield return null (espera 1 frame) e não WaitForEndOfFrame,
        ///   porque WaitForEndOfFrame atrasaria o primeiro tick para o final do
        ///   frame atual, causando um flash de "cooldown já quase zerado" no
        ///   primeiro frame. Com yield null, a atualização começa no próximo frame
        ///   normalmente, sem delay perceptível.
        /// </summary>
        private IEnumerator CooldownCoroutine()
        {
            // Ativa o overlay assim que começa
            if (cooldownOverlay != null)
            {
                cooldownOverlay.fillAmount = 1f;
                cooldownOverlay.enabled    = true;
            }

            if (cooldownText != null)
                cooldownText.text = $"{_totalCooldown:0.0}";

            while (_remainingCooldown > 0f)
            {
                yield return null; // aguarda 1 frame

                _remainingCooldown -= Time.deltaTime;
                _remainingCooldown  = Mathf.Max(0f, _remainingCooldown);

                float fill = _remainingCooldown / _totalCooldown;

                if (cooldownOverlay != null)
                    cooldownOverlay.fillAmount = fill;

                // Mostra texto apenas enquanto tem tempo significativo restante
                if (cooldownText != null)
                    cooldownText.text = _remainingCooldown > 0.05f
                        ? $"{_remainingCooldown:0.0}"
                        : "";
            }

            // Zera tudo ao terminar
            OnCooldown         = false;
            _cooldownCoroutine = null;

            if (cooldownOverlay != null)
            {
                cooldownOverlay.fillAmount = 0f;
                cooldownOverlay.enabled    = false;
            }

            if (cooldownText != null)
                cooldownText.text = "";
        }
    }
}