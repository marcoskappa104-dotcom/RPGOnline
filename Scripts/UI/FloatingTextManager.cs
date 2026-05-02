using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using TMPro;

namespace RPG.UI
{
    /// <summary>
    /// FloatingTextManager — pool de textos flutuantes (números de dano, XP, etc.)
    /// Coloque um prefab com TMP_Text que tem Animator para subir e sumir.
    /// </summary>
    public class FloatingTextManager : MonoBehaviour
    {
        public static FloatingTextManager Instance { get; private set; }

        [SerializeField] private GameObject floatingTextPrefab;
        [SerializeField] private int        poolSize   = 20;
        [SerializeField] private float      riseSpeed  = 1.5f;
        [SerializeField] private float      lifetime   = 1.2f;

        private Queue<GameObject> _pool = new Queue<GameObject>();

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
            PrewarmPool();
        }

        private void PrewarmPool()
        {
            if (floatingTextPrefab == null) return;
            for (int i = 0; i < poolSize; i++)
            {
                var obj = Instantiate(floatingTextPrefab, transform);
                obj.SetActive(false);
                _pool.Enqueue(obj);
            }
        }

        public void Show(string text, Vector3 worldPos, Color color)
        {
            if (floatingTextPrefab == null) return;
            StartCoroutine(ShowCoroutine(text, worldPos, color));
        }

        private IEnumerator ShowCoroutine(string text, Vector3 worldPos, Color color)
        {
            GameObject obj = _pool.Count > 0 ? _pool.Dequeue() : Instantiate(floatingTextPrefab, transform);
            obj.SetActive(true);
            obj.transform.position = worldPos + Vector3.up * 1.5f +
                                     new Vector3(Random.Range(-0.3f, 0.3f), 0, 0);

            var tmp = obj.GetComponentInChildren<TMP_Text>();
            if (tmp != null) { tmp.text = text; tmp.color = color; }

            float elapsed = 0f;
            Vector3 startPos = obj.transform.position;

            while (elapsed < lifetime)
            {
                elapsed += Time.deltaTime;
                float t = elapsed / lifetime;
                obj.transform.position = startPos + Vector3.up * (riseSpeed * t);
                if (tmp != null)
                {
                    var c = tmp.color;
                    c.a     = 1f - t;
                    tmp.color = c;
                }
                // Billboard — texto sempre vira para câmera
                if (Camera.main != null)
                    obj.transform.forward = Camera.main.transform.forward;

                yield return null;
            }

            obj.SetActive(false);
            _pool.Enqueue(obj);
        }
    }
}
