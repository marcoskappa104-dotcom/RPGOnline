using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Collections;

namespace RPG.UI
{
    public class SkillSlotUI : MonoBehaviour
    {
        [SerializeField] private Image    iconImage;
        [SerializeField] private Image    cooldownOverlay;
        [SerializeField] private TMP_Text cooldownText;
        [SerializeField] private TMP_Text hotkeyText;

        private float _totalCooldown;
        private float _remainingCooldown;
        public  bool  OnCooldown { get; private set; }

        private void Awake()
        {
            if (cooldownOverlay != null)
            {
                cooldownOverlay.type       = Image.Type.Filled;
                cooldownOverlay.fillMethod = Image.FillMethod.Radial360;
                cooldownOverlay.fillAmount = 0f;
            }
        }

        public void SetIcon(Sprite icon)
        {
            if (iconImage != null) iconImage.sprite = icon;
        }

        public void SetHotkey(string key)
        {
            if (hotkeyText != null) hotkeyText.text = key;
        }

        public void StartCooldown(float duration)
        {
            _totalCooldown     = duration;
            _remainingCooldown = duration;
            OnCooldown         = true;
            StartCoroutine(CooldownCoroutine());
        }

        private IEnumerator CooldownCoroutine()
        {
            while (_remainingCooldown > 0)
            {
                _remainingCooldown -= Time.deltaTime;
                float fill = Mathf.Max(0f, _remainingCooldown / _totalCooldown);

                if (cooldownOverlay != null) cooldownOverlay.fillAmount = fill;
                if (cooldownText    != null)
                    cooldownText.text = _remainingCooldown > 0 ? $"{_remainingCooldown:0.0}" : "";

                yield return null;
            }

            OnCooldown = false;
            if (cooldownOverlay != null) cooldownOverlay.fillAmount = 0f;
            if (cooldownText    != null) cooldownText.text = "";
        }
    }
}
