using UnityEngine;
using UnityEngine.AI;
using UnityEngine.EventSystems;
using RPG.Character;
using RPG.Combat;
using RPG.UI;

namespace RPG.Systems
{
    [RequireComponent(typeof(PlayerEntity))]
    public class PlayerController : MonoBehaviour
    {
        [Header("Layers")]
        [SerializeField] private LayerMask terrainLayer;
        [SerializeField] private LayerMask targetableLayer;

        [Header("Teclas de Skill")]
        [SerializeField] private KeyCode skill1Key = KeyCode.Q;
        [SerializeField] private KeyCode skill2Key = KeyCode.W;
        [SerializeField] private KeyCode skill3Key = KeyCode.E;
        [SerializeField] private KeyCode skill4Key = KeyCode.R;

        [Header("Indicador de Movimento")]
        [SerializeField] private GameObject moveIndicatorPrefab;

        [Header("Debug")]
        [SerializeField] private bool debugLogs = true;

        private PlayerEntity _player;
        private SkillSystem  _skills;
        private NavMeshAgent _agent;

        private void Awake()
        {
            _player = GetComponent<PlayerEntity>();
            _skills = GetComponent<SkillSystem>();
            _agent  = GetComponent<NavMeshAgent>();
        }

        private void Start()
        {
            if (terrainLayer == 0)
                Debug.LogWarning("[PlayerController] Terrain Layer não configurado! " +
                                 "Selecione a layer do terreno no Inspector.");
            if (targetableLayer == 0)
                Debug.LogWarning("[PlayerController] Targetable Layer não configurado! " +
                                 "Selecione a layer dos monstros no Inspector.");
        }

        private void Update()
        {
            if (!_player.IsInitialized) return;
            HandleMouseClick();
            HandleSkillKeys();
        }

        // ── Mouse ─────────────────────────────────────────────────────────

        private void HandleMouseClick()
        {
            if (!Input.GetMouseButtonDown(0)) return;

            if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject())
            {
                Log("Clique ignorado — sobre UI.");
                return;
            }

            Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);

            // ── 1. Testa APENAS a layer Targetable ────────────────────────
            if (targetableLayer != 0 &&
                Physics.Raycast(ray, out RaycastHit hitTarget, 300f, targetableLayer))
            {
                var targetable = hitTarget.collider.GetComponentInParent<ITargetable>();
                if (targetable != null)
                {
                    if (targetable.IsDead)
                    {
                        Log($"Clique em {targetable.DisplayName} — já está morto, ignorando.");
                        return;
                    }

                    // SELECIONA — não move
                    Log($"Clique em TARGETABLE: {targetable.DisplayName} | HP:{targetable.CurrentHP:0}/{targetable.MaxHP:0}");
                    _player.SetTarget(targetable);
                    UIManager.Instance?.UpdateTargetPanel(targetable);
                    return; // <-- sai aqui, nunca cai no movimento
                }
                else
                {
                    Log($"Raycast targetable hit {hitTarget.collider.name} mas sem ITargetable.");
                }
            }

            // ── 2. Testa APENAS a layer Terrain ───────────────────────────
            if (terrainLayer != 0 &&
                Physics.Raycast(ray, out RaycastHit hitTerrain, 300f, terrainLayer))
            {
                Vector3 dest = hitTerrain.point;

                if (NavMesh.SamplePosition(dest, out NavMeshHit navHit, 3f, NavMesh.AllAreas))
                    dest = navHit.position;
                else
                    Log($"Ponto {hitTerrain.point} não encontrou NavMesh próximo!");

                Log($"Mover para {dest} (terreno clicado: {hitTerrain.collider.name})");

                _skills?.CancelPendingAction();
                _player.ClearTarget();
                UIManager.Instance?.ClearTargetPanel();
                _player.MoveTo(dest);
                SpawnMoveIndicator(hitTerrain.point);
                return;
            }

            // ── 3. Fallback: nenhuma layer configurada — usa tudo ─────────
            if (terrainLayer == 0 && targetableLayer == 0)
            {
                Log("AVISO: Nenhuma layer configurada! Usando raycast geral (pode causar bugs).");
                if (Physics.Raycast(ray, out RaycastHit hitAny, 300f))
                {
                    var t = hitAny.collider.GetComponentInParent<ITargetable>();
                    if (t != null && !t.IsDead)
                    {
                        _player.SetTarget(t);
                        UIManager.Instance?.UpdateTargetPanel(t);
                        return;
                    }

                    Vector3 dest = hitAny.point;
                    if (NavMesh.SamplePosition(dest, out NavMeshHit nh, 3f, NavMesh.AllAreas))
                        dest = nh.position;
                    _skills?.CancelPendingAction();
                    _player.ClearTarget();
                    UIManager.Instance?.ClearTargetPanel();
                    _player.MoveTo(dest);
                    SpawnMoveIndicator(hitAny.point);
                }
            }
        }

        // ── Skills ────────────────────────────────────────────────────────

        private void HandleSkillKeys()
        {
            if (Input.GetKeyDown(skill1Key)) UseSkill(0);
            if (Input.GetKeyDown(skill2Key)) UseSkill(1);
            if (Input.GetKeyDown(skill3Key)) UseSkill(2);
            if (Input.GetKeyDown(skill4Key)) UseSkill(3);
        }

        private void UseSkill(int index)
        {
            Log($"Tecla de skill {index} pressionada. Alvo atual: {(_player.CurrentTarget?.DisplayName ?? "nenhum")}");
            _skills?.TryUseSkill(index);
        }

        // ── Move indicator ────────────────────────────────────────────────

        private void SpawnMoveIndicator(Vector3 pos)
        {
            if (moveIndicatorPrefab == null) return;
            var go = Instantiate(moveIndicatorPrefab,
                pos + Vector3.up * 0.02f, Quaternion.Euler(90f, 0f, 0f));
            Destroy(go, 0.8f);
        }

        private void Log(string msg)
        {
            if (debugLogs) Debug.Log($"[PlayerController] {msg}");
        }
    }
}
