using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using TMPro;

namespace RPG.UI
{
    /// <summary>
    /// FloatingTextManager v3
    ///
    /// CORREÇÃO v3:
    ///   Camera.main era chamada TODO frame dentro da coroutine ShowCoroutine,
    ///   para CADA texto flutuante ativo ao mesmo tempo. Com 10 textos simultâneos
    ///   e 60fps = 600 buscas por segundo.
    ///
    ///   Solução: câmera buscada UMA VEZ em Show() e passada para a coroutine.
    ///   Cache global atualizado apenas quando a câmera for null (troca de cena).
    ///
    ///   Adicionado: proteção contra pool vazio quando poolSize = 0.
    /// </summary>
    public class FloatingTextManager : MonoBehaviour
    {
        public static FloatingTextManager Instance { get; private set; }

        [SerializeField] private GameObject floatingTextPrefab;
        [SerializeField] private int        poolSize  = 20;
        [SerializeField] private float      riseSpeed = 2f;
        [SerializeField] private float      lifetime  = 1.2f;

        private Queue<GameObject> _pool = new Queue<GameObject>();

        // CORREÇÃO: câmera cacheada globalmente no manager
        private Camera _cachedCamera;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
            PrewarmPool();
        }

        private void Start()
        {
            // Cache inicial — evita busca na primeira chamada de Show()
            _cachedCamera = Camera.main;
        }

        private void PrewarmPool()
        {
            if (floatingTextPrefab == null)
            {
                Debug.LogWarning("[FloatingTextManager] floatingTextPrefab não configurado!");
                return;
            }

            for (int i = 0; i < Mathf.Max(poolSize, 1); i++)
            {
                var obj = Instantiate(floatingTextPrefab, transform);
                obj.SetActive(false);
                _pool.Enqueue(obj);
            }
        }

        public void Show(string text, Vector3 worldPos, Color color)
        {
            if (floatingTextPrefab == null) return;

            // CORREÇÃO: câmera buscada aqui (uma vez por Show), não dentro da coroutine
            if (_cachedCamera == null) _cachedCamera = Camera.main;

            StartCoroutine(ShowCoroutine(text, worldPos, color, _cachedCamera));
        }

        private IEnumerator ShowCoroutine(string text, Vector3 worldPos, Color color, Camera cam)
        {
            GameObject obj = _pool.Count > 0
                ? _pool.Dequeue()
                : Instantiate(floatingTextPrefab, transform);

            obj.transform.position = worldPos + new Vector3(
                Random.Range(-0.3f, 0.3f), 0f, 0f);
            obj.SetActive(true);

            var tmp = obj.GetComponent<TextMeshPro>()
                   ?? obj.GetComponentInChildren<TextMeshPro>();

            if (tmp != null)
            {
                tmp.text  = text;
                tmp.color = color;
            }
            else
            {
                Debug.LogWarning("[FloatingTextManager] Prefab não tem TextMeshPro (3D)!");
            }

            float   elapsed  = 0f;
            Vector3 startPos = obj.transform.position;

            while (elapsed < lifetime)
            {
                elapsed += Time.deltaTime;
                float t = elapsed / lifetime;

                obj.transform.position = startPos + Vector3.up * (riseSpeed * t);

                if (tmp != null)
                {
                    var c = tmp.color;
                    c.a       = 1f - Mathf.Pow(t, 2f);
                    tmp.color = c;
                }

                // CORREÇÃO: usa 'cam' (parâmetro da coroutine), não Camera.main
                if (cam != null)
                {
                    Vector3 dir = obj.transform.position - cam.transform.position;
                    if (dir.sqrMagnitude > 0.001f)
                        obj.transform.forward = dir.normalized;
                }

                yield return null;
            }

            obj.SetActive(false);
            _pool.Enqueue(obj);
        }
    }
}