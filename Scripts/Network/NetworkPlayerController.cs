using UnityEngine;
using UnityEngine.AI;
using Mirror;
using RPG.Character;
using RPG.UI;
using RPG.Combat;

namespace RPG.Network
{
    /// <summary>
    /// NetworkPlayerController v3
    ///
    /// CORREÇÕES:
    ///   - Cursor NUNCA some: removido CursorLockMode.Locked ao orbitar.
    ///     Órbita funciona com RMB pressionado sem travar/esconder o cursor.
    ///   - Movimento local (predição) + CmdMoveTo no servidor.
    ///   - Cursor restaurado automaticamente ao morrer/respawnar.
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    public class NetworkPlayerController : NetworkBehaviour
    {
        [Header("Layers")]
        [SerializeField] private LayerMask terrainLayer;
        [SerializeField] private LayerMask targetableLayer;

        [Header("Câmera")]
        [SerializeField] private float orbitSensitivity = 3f;
        [SerializeField] private float zoomSensitivity  = 5f;

        [Header("Indicador de Movimento")]
        [SerializeField] private GameObject moveIndicatorPrefab;

        private NavMeshAgent _agent;
        private PlayerEntity _playerEntity;
        private SkillSystem  _skillSystem;
        private Camera       _cam;

        // Estado de câmera
        private float _yaw      = 45f;
        private float _pitch    = 45f;
        private float _distance = 12f;
        private bool  _orbiting;

        private const float PITCH_MIN = 10f;
        private const float PITCH_MAX = 80f;
        private const float DIST_MIN  = 3f;
        private const float DIST_MAX  = 30f;

        private void Awake()
        {
            _agent = GetComponent<NavMeshAgent>();
        }

        private void OnEnable()
        {
            // Garante cursor visível sempre que o controller está ativo
            Cursor.visible   = true;
            Cursor.lockState = CursorLockMode.None;
        }

        private void OnDisable()
        {
            // Ao desativar (morte, etc.), garante cursor visível
            _orbiting        = false;
            Cursor.visible   = true;
            Cursor.lockState = CursorLockMode.None;
        }

        public override void OnStartLocalPlayer()
        {
            _playerEntity = GetComponent<PlayerEntity>();
            _skillSystem  = GetComponent<SkillSystem>();
            _cam          = Camera.main;

            // Garante cursor visível ao entrar no jogo
            Cursor.visible   = true;
            Cursor.lockState = CursorLockMode.None;

            UIManager.Instance?.BindLocalPlayer(_playerEntity);

            Debug.Log("[NetworkPlayerController] Controller local iniciado.");
        }

        private void Update()
        {
            if (!isLocalPlayer) return;

            HandleMouseInput();
            HandleSkillInput();
            HandleCameraOrbit();
            UpdateCameraPosition();
        }

        // ── Movimento e Seleção ───────────────────────────────────────────

        private void HandleMouseInput()
        {
            if (!Input.GetMouseButtonDown(0)) return;
            if (_cam == null) return;

            // Ignora cliques sobre UI
            if (UnityEngine.EventSystems.EventSystem.current != null &&
                UnityEngine.EventSystems.EventSystem.current.IsPointerOverGameObject())
                return;

            Ray ray = _cam.ScreenPointToRay(Input.mousePosition);

            // ── 1. Targetable ─────────────────────────────────────────────
            if (targetableLayer != 0 &&
                Physics.Raycast(ray, out RaycastHit tHit, 300f, targetableLayer))
            {
                var targetable = tHit.collider.GetComponentInParent<ITargetable>();
                if (targetable != null)
                {
                    if (targetable.IsDead)
                    {
                        Debug.Log($"[Controller] {targetable.DisplayName} já está morto.");
                        return;
                    }
                    _skillSystem?.CancelPendingAction();
                    _playerEntity?.SetTarget(targetable);
                    UIManager.Instance?.UpdateTargetPanel(targetable);
                    Debug.Log($"[Controller] Alvo selecionado: {targetable.DisplayName} | " +
                              $"HP:{targetable.CurrentHP:0}/{targetable.MaxHP:0}");
                    return;
                }
            }

            // ── 2. Terreno ────────────────────────────────────────────────
            if (terrainLayer != 0 &&
                Physics.Raycast(ray, out RaycastHit gHit, 300f, terrainLayer))
            {
                _skillSystem?.CancelPendingAction();
                _playerEntity?.ClearTarget();
                UIManager.Instance?.ClearTargetPanel();

                Vector3 dest = gHit.point;
                if (NavMesh.SamplePosition(dest, out NavMeshHit navHit, 3f, NavMesh.AllAreas))
                    dest = navHit.position;

                // Predição local: move imediatamente sem esperar o servidor
                if (_agent != null && _agent.isOnNavMesh)
                    _agent.SetDestination(dest);

                CmdMoveTo(dest);
                SpawnMoveIndicator(gHit.point);
                Debug.Log($"[Controller] Mover para {dest}");
                return;
            }

            // ── 3. Fallback sem layers configuradas ───────────────────────
            if (terrainLayer == 0 && targetableLayer == 0)
            {
                if (Physics.Raycast(ray, out RaycastHit hitAny, 300f))
                {
                    var t = hitAny.collider.GetComponentInParent<ITargetable>();
                    if (t != null && !t.IsDead)
                    {
                        _playerEntity?.SetTarget(t);
                        UIManager.Instance?.UpdateTargetPanel(t);
                        return;
                    }
                    Vector3 dest = hitAny.point;
                    if (NavMesh.SamplePosition(dest, out NavMeshHit nh, 3f, NavMesh.AllAreas))
                        dest = nh.position;
                    if (_agent != null && _agent.isOnNavMesh)
                        _agent.SetDestination(dest);
                    CmdMoveTo(dest);
                    _skillSystem?.CancelPendingAction();
                    _playerEntity?.ClearTarget();
                    UIManager.Instance?.ClearTargetPanel();
                }
            }
        }

        // ── Skills ────────────────────────────────────────────────────────

        private void HandleSkillInput()
        {
            if (_skillSystem == null) return;
            if (Input.GetKeyDown(KeyCode.Q)) UseSkill(0);
            if (Input.GetKeyDown(KeyCode.W)) UseSkill(1);
            if (Input.GetKeyDown(KeyCode.E)) UseSkill(2);
            if (Input.GetKeyDown(KeyCode.R)) UseSkill(3);
        }

        private void UseSkill(int index)
        {
            var target = _playerEntity?.CurrentTarget;
            Debug.Log($"[Controller] Skill {index} pressionada. Alvo: {target?.DisplayName ?? "nenhum"}");
            _skillSystem?.TryUseSkill(index);
        }

        // ── Câmera ────────────────────────────────────────────────────────

        private void HandleCameraOrbit()
        {
            // Inicia/para orbitar com RMB — SEM esconder ou travar o cursor
            if (Input.GetMouseButtonDown(1)) _orbiting = true;
            if (Input.GetMouseButtonUp(1))   _orbiting = false;

            if (_orbiting)
            {
                // Usa GetAxis("Mouse X/Y") normalmente — cursor continua visível
                _yaw   += Input.GetAxis("Mouse X") * orbitSensitivity;
                _pitch -= Input.GetAxis("Mouse Y") * orbitSensitivity;
                _pitch  = Mathf.Clamp(_pitch, PITCH_MIN, PITCH_MAX);
            }

            _distance -= Input.GetAxis("Mouse ScrollWheel") * zoomSensitivity;
            _distance  = Mathf.Clamp(_distance, DIST_MIN, DIST_MAX);
        }

        private void UpdateCameraPosition()
        {
            if (_cam == null) return;

            Quaternion rot    = Quaternion.Euler(_pitch, _yaw, 0f);
            Vector3    offset = rot * new Vector3(0f, 0f, -_distance);
            Vector3    pivot  = transform.position + Vector3.up * 1.5f;

            _cam.transform.position = pivot + offset;
            _cam.transform.LookAt(pivot);
        }

        // ── Commands ──────────────────────────────────────────────────────

        [Command]
        private void CmdMoveTo(Vector3 destination)
        {
            if (_agent == null) return;
            if (NavMesh.SamplePosition(destination, out NavMeshHit hit, 2f, NavMesh.AllAreas))
                _agent.SetDestination(hit.position);
            else
                _agent.SetDestination(destination);
        }

        // ── Helpers ───────────────────────────────────────────────────────

        private void SpawnMoveIndicator(Vector3 pos)
        {
            if (moveIndicatorPrefab == null) return;
            var go = Instantiate(moveIndicatorPrefab,
                pos + Vector3.up * 0.02f, Quaternion.Euler(90f, 0f, 0f));
            Destroy(go, 0.8f);
        }
    }
}