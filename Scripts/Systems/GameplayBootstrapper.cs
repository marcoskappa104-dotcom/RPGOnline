using UnityEngine;
using UnityEngine.AI;
using RPG.Character;
using RPG.Managers;

namespace RPG.Systems
{
    public class GameplayBootstrapper : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private GameObject       playerPrefab;
        [SerializeField] private Transform        spawnPoint;
        [SerializeField] private CameraController cameraController;

        [Header("NavMesh Snap")]
        [SerializeField] private float snapRadius = 20f;

        private void Start()
        {
            if (GameManager.Instance?.SelectedCharacter == null)
            {
                UnityEngine.SceneManagement.SceneManager.LoadScene(GameManager.SCENE_LOGIN);
                return;
            }

            var charData = GameManager.Instance.SelectedCharacter;

            // ── 1. Instancia fora de cena (desativado) para evitar o erro do NavMesh ──
            var playerGO = Instantiate(playerPrefab, Vector3.zero, Quaternion.identity);
            playerGO.SetActive(false);

            // ── 2. Desativa o NavMeshAgent antes de posicionar ────────────
            var agent = playerGO.GetComponent<NavMeshAgent>();
            if (agent != null) agent.enabled = false;

            // ── 3. Determina posição desejada ─────────────────────────────
            Vector3 desired = spawnPoint != null ? spawnPoint.position : Vector3.zero;
            if (charData.PosX != 0 || charData.PosZ != 0)
                desired = new Vector3(charData.PosX, charData.PosY, charData.PosZ);

            // ── 4. Tenta snapar para o NavMesh ────────────────────────────
            Vector3 finalPos = FindNavMeshPosition(desired);
            playerGO.transform.position = finalPos;

            // ── 5. Ativa o objeto, depois o agente ────────────────────────
            playerGO.SetActive(true);
            if (agent != null) agent.enabled = true;

            // ── 6. Inicializa dados do personagem ─────────────────────────
            var player = playerGO.GetComponent<PlayerEntity>();
            player?.Initialize(charData);

            // ── 7. Conecta câmera ─────────────────────────────────────────
            cameraController?.SetTarget(playerGO.transform);

            Debug.Log($"[Bootstrapper] {charData.CharacterName} ({charData.Race} Lv{charData.Level}) em {finalPos}");
        }

        private Vector3 FindNavMeshPosition(Vector3 origin)
        {
            // Tentativas com raios crescentes e alturas variadas
            float[] radii   = { snapRadius, snapRadius * 2f, snapRadius * 5f };
            float[] offsets = { 0f, 1f, 2f, 5f, -1f };

            foreach (float r in radii)
            {
                foreach (float dy in offsets)
                {
                    Vector3 probe = origin + Vector3.up * dy;
                    if (NavMesh.SamplePosition(probe, out NavMeshHit hit, r, NavMesh.AllAreas))
                    {
                        Debug.Log($"[Bootstrapper] NavMesh encontrado em {hit.position} (raio={r}, dy={dy})");
                        return hit.position;
                    }
                }
            }

            // Último recurso: varre a cena inteira procurando qualquer ponto no NavMesh
            Debug.LogWarning("[Bootstrapper] NavMesh não encontrado perto do spawn. " +
                             "Certifique-se de ter feito o Bake do NavMesh! " +
                             "Window → AI → Navigation → Bake");
            return origin;
        }
    }
}
