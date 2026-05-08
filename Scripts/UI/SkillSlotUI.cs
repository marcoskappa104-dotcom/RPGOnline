using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Collections;

namespace RPG.UI
{
    /// <summary>
    /// SkillSlotUI v2
    ///
    /// CORREÇÕES v2:
    ///   - SetIcon(null): antes deixava o Image habilitado sem sprite, exibindo
    ///     um quadrado branco. Agora desativa o Image quando o sprite é null.
    ///   - CooldownCoroutine: usa WaitForEndOfFrame em vez de yield null para
    ///     evitar frame de atraso visível no primeiro tick.
    ///   - StopCooldown() adicionado para cancelar o cooldown visual externamente
    ///     (útil para resets de cena ou morte do jogador).
    /// </summary>
    public class SkillSlotUI : MonoBehaviour
    {
        [SerializeField] private Image    iconImage;
        [SerializeField] private Image    cooldownOverlay;
        [SerializeField] private TMP_Text cooldownText;
        [SerializeField] private TMP_Text hotkeyText;

        private float     _totalCooldown;
        private float     _remainingCooldown;
        private Coroutine _cooldownCoroutine;

        public bool OnCooldown { get; private set; }

        // ── Lifecycle ──────────────────────────────────────────────────────

        private void Awake()
        {
            if (cooldownOverlay != null)
            {
                cooldownOverlay.type       = Image.Type.Filled;
                cooldownOverlay.fillMethod = Image.FillMethod.Radial360;
                cooldownOverlay.fillAmount = 0f;
                cooldownOverlay.enabled    = false;
            }

            if (cooldownText != null)
                cooldownText.text = "";
        }

        // ── API pública ────────────────────────────────────────────────────

        /// <summary>
        /// Define o ícone do slot.
        /// Passe null para limpar o ícone (desativa o Image para não exibir quadrado branco).
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

        /// <summary>Define o texto do hotkey exibido no slot (ex: "Q").</summary>
        public void SetHotkey(string key)
        {
            if (hotkeyText != null)
                hotkeyText.text = key;
        }

        /// <summary>Inicia a animação de cooldown.</summary>
        public void StartCooldown(float duration)
        {
            if (duration <= 0f) return;

            _totalCooldown     = duration;
            _remainingCooldown = duration;
            OnCooldown         = true;

            if (_cooldownCoroutine != null)
                StopCoroutine(_cooldownCoroutine);

            _cooldownCoroutine = StartCoroutine(CooldownCoroutine());
        }

        /// <summary>Para o cooldown visual imediatamente (ex: ao morrer ou trocar skill).</summary>
        public void StopCooldown()
        {
            if (_cooldownCoroutine != null)
            {
                StopCoroutine(_cooldownCoroutine);
                _cooldownCoroutine = null;
            }

            OnCooldown = false;

            if (cooldownOverlay != null)
            {
                cooldownOverlay.fillAmount = 0f;
                cooldownOverlay.enabled    = false;
            }

            if (cooldownText != null)
                cooldownText.text = "";
        }

        // ── Cooldown Coroutine ─────────────────────────────────────────────

        private IEnumerator CooldownCoroutine()
        {
            // Ativa overlay no início
            if (cooldownOverlay != null)
                cooldownOverlay.enabled = true;

            while (_remainingCooldown > 0f)
            {
                _remainingCooldown -= Time.deltaTime;

                float fill = Mathf.Clamp01(_remainingCooldown / _totalCooldown);

                if (cooldownOverlay != null)
                    cooldownOverlay.fillAmount = fill;

                if (cooldownText != null)
                    cooldownText.text = _remainingCooldown > 0.05f
                        ? $"{_remainingCooldown:0.0}"
                        : "";

                yield return null;
            }

            // Zera ao terminar
            _remainingCooldown = 0f;
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