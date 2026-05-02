using UnityEngine;
using UnityEngine.UI;

namespace RPG.UI
{
    /// <summary>
    /// MonsterHealthBarUI — barra de HP que fica sobre o mob (World Space Canvas).
    /// Sempre vira para a câmera (billboard).
    /// </summary>
    public class MonsterHealthBarUI : MonoBehaviour
    {
        [SerializeField] private Slider   hpSlider;
        [SerializeField] private GameObject container; // para esconder quando cheio

        private void Start()
        {
            if (container != null) container.SetActive(false);
        }

        private void LateUpdate()
        {
            // Billboard
            if (Camera.main != null)
                transform.forward = Camera.main.transform.forward;
        }

        public void UpdateBar(float current, float max)
        {
            if (hpSlider == null) return;
            hpSlider.maxValue = max;
            hpSlider.value    = current;

            if (container != null)
                container.SetActive(current < max); // mostra só se tomou dano
        }
    }
}
