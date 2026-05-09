using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using TMPro;

namespace RPG.UI
{
    /// <summary>
    /// FloatingTextManager v4
    ///
    /// CORREÇÕES v4:
    ///
    ///   BUG-17 — Crashava em servidor dedicado (Application.isBatchMode):
    ///     O servidor dedicado não tem câmera nem UI. Se o FloatingTextManager
    ///     fosse instanciado no servidor, Show() tentava Camera.main = null
    ///     e crashava silenciosamente.
    ///     SOLUÇÃO: Guard no Awake() — em batch mode (servidor dedicado),
    ///     o componente é desativado imediatamente. Show() também tem guard.
    ///
    ///   Todas as correções v3 mantidas:
    ///     - Câmera cacheada globalmente (não busca todo frame).
    ///     - Pool com tamanho mínimo de 1.
    ///     - Câmera passada como parâmetro para ShowCoroutine (sem GC por frame).
    /// </summary>
    public class FloatingTextManager : MonoBehaviour
    {
        public static FloatingTextManager Instance { get; private set; }

        [SerializeField] private GameObject floatingTextPrefab;
        [SerializeField] private int        poolSize  = 20;
        [SerializeField] private float      riseSpeed = 2f;
        [SerializeField] private float      lifetime  = 1.2f;

        private Queue<GameObject> _pool = new Queue<GameObject>();
        private Camera            _cachedCamera;
        private bool              _isServerOnly = false;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);

            // BUG-17: servidor dedicado não tem UI — desativa tudo
            if (Application.isBatchMode)
            {
                _isServerOnly = true;
                Debug.Log("[FloatingTextManager] Servidor dedicado detectado — UI desabilitada.");
                return;
            }

            PrewarmPool();
        }

        private void Start()
        {
            if (_isServerOnly) return;
            _cachedCamera = Camera.main;
        }

        private void PrewarmPool()
        {
            if (floatingTextPrefab == null)
            {
                Debug.LogWarning("[FloatingTextManager] floatingTextPrefab não configurado!");
                return;
            }

            int size = Mathf.Max(poolSize, 1);
            for (int i = 0; i < size; i++)
            {
                var obj = Instantiate(floatingTextPrefab, transform);
                obj.SetActive(false);
                _pool.Enqueue(obj);
            }
        }

        public void Show(string text, Vector3 worldPos, Color color)
        {
            // BUG-17: guard duplo — servidor dedicado e prefab não configurado
            if (_isServerOnly || Application.isBatchMode) return;
            if (floatingTextPrefab == null) return;

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
