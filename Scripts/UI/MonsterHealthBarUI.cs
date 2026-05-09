using UnityEngine;
using UnityEngine.UI;

namespace RPG.UI
{
    /// <summary>
    /// MonsterHealthBarUI v2
    ///
    /// CORREÇÃO v2:
    ///   Camera.main era chamada TODA vez no LateUpdate, para CADA monstro vivo.
    ///   Com 30 monstros na tela = 30 buscas por frame (Camera.main usa FindObjectOfType
    ///   internamente, que é O(n) sobre todos os objetos da cena).
    ///
    ///   Solução: cacheia a câmera em Start e atualiza apenas se ela for destruída
    ///   (ex: troca de câmera em transição de cena).
    /// </summary>
    public class MonsterHealthBarUI : MonoBehaviour
    {
        [SerializeField] private Slider    hpSlider;
        [SerializeField] private GameObject container;

        // CORREÇÃO: câmera cacheada — não busca todo LateUpdate
        private Camera _cam;

        private void Start()
        {
            _cam = Camera.main;
            if (container != null) container.SetActive(false);
        }

        private void LateUpdate()
        {
            // Atualiza cache apenas se câmera foi destruída (ex: troca de câmera)
            if (_cam == null) _cam = Camera.main;
            if (_cam == null) return;

            transform.forward = _cam.transform.forward;
        }

        public void UpdateBar(float current, float max)
        {
            if (hpSlider == null) return;
            hpSlider.maxValue = max;
            hpSlider.value    = current;

            if (container != null)
                container.SetActive(current < max);
        }
    }
}