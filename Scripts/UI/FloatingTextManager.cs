	using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using TMPro;

namespace RPG.UI
{
    /// <summary>
    /// FloatingTextManager v2
    ///
    /// CORREÇÃO DO PREFAB:
    ///   O prefab NÃO deve ter Canvas. Deve ser um GameObject vazio com
    ///   filho TextMeshPro (não UI, mas o componente 3D world space).
    ///
    ///   COMO CRIAR O PREFAB CORRETAMENTE:
    ///
    ///   1. Hierarchy → clique direito → Create Empty
    ///      Renomeie para: FloatingTextPrefab
    ///
    ///   2. Com FloatingTextPrefab selecionado:
    ///      Add Component → TextMeshPro - Text (NÃO o UI Text, o 3D Text)
    ///      Ou: clique direito no FloatingTextPrefab → 3D Object → Text - TextMeshPro
    ///
    ///   3. Configure o TextMeshPro 3D:
    ///      Font Size:  5
    ///      Bold:       sim
    ///      Alignment:  Center
    ///      Color:      branco
    ///
    ///   4. Salve como prefab em Assets/Prefabs/UI/FloatingTextPrefab
    ///
    ///   ATENÇÃO: Se criar via UI → Text - TextMeshPro, ele cria um Canvas
    ///   automaticamente e o texto vai aparecer no canto da tela, não no mundo.
    ///   Use sempre o 3D Text (TextMeshPro component diretamente no GameObject).
    /// </summary>
    public class FloatingTextManager : MonoBehaviour
    {
        public static FloatingTextManager Instance { get; private set; }

        [SerializeField] private GameObject floatingTextPrefab;
        [SerializeField] private int        poolSize   = 20;
        [SerializeField] private float      riseSpeed  = 2f;
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
            if (floatingTextPrefab == null)
            {
                Debug.LogWarning("[FloatingTextManager] floatingTextPrefab não configurado! " +
                                 "Arraste o prefab no Inspector.");
                return;
            }

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
            // Pega do pool ou instancia novo se o pool estiver vazio
            GameObject obj = _pool.Count > 0
                ? _pool.Dequeue()
                : Instantiate(floatingTextPrefab, transform);

            // Posição inicial: acima do ponto de origem, com offset horizontal aleatório
            obj.transform.position = worldPos + new Vector3(
                Random.Range(-0.3f, 0.3f), 0f, 0f);
            obj.SetActive(true);

            // Tenta achar o TMP no próprio objeto ou em filhos
            var tmp = obj.GetComponent<TextMeshPro>();
            if (tmp == null)
                tmp = obj.GetComponentInChildren<TextMeshPro>();

            if (tmp != null)
            {
                tmp.text  = text;
                tmp.color = color;
            }
            else
            {
                Debug.LogWarning("[FloatingTextManager] Prefab não tem TextMeshPro (3D)! " +
                                 "Verifique se usou o componente 3D, não o UI.");
            }

            float   elapsed  = 0f;
            Vector3 startPos = obj.transform.position;

            while (elapsed < lifetime)
            {
                elapsed += Time.deltaTime;
                float t = elapsed / lifetime;

                // Sobe progressivamente
                obj.transform.position = startPos + Vector3.up * (riseSpeed * t);

                // Fade out no final
                if (tmp != null)
                {
                    var c = tmp.color;
                    c.a       = 1f - Mathf.Pow(t, 2f); // fade quadrático (mais suave)
                    tmp.color = c;
                }

// Billboard — texto sempre vira para a câmera
if (Camera.main != null)
{
    Vector3 dir = obj.transform.position - Camera.main.transform.position;
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