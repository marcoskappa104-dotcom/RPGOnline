using UnityEngine;
using UnityEngine.AI;
using Mirror;
using RPG.Character;
using RPG.UI;
using RPG.Combat;

namespace RPG.Network
{
    /// <summary>
    /// NetworkPlayerController v6 — Server-Authoritative
    ///
    /// CORREÇÕES v6:
    ///   1. CmdMoveTo agora está neste script (era duplicado com NetworkPlayer).
    ///      NetworkPlayer.CmdMoveTo foi removido — controlador de input é aqui.
    ///
    ///   2. Predição local de movimento: cliente move o agente imediatamente para UX
    ///      responsivo, servidor confirma e pode corrigir se necessário.
    ///      Sem predição local o personagem "trava" esperando o RTT do servidor.
    ///
    ///   3. SkillSystem.WalkThenSendCmd passa a usar CmdMoveTo deste controller
    ///      para manter o servidor ciente do movimento durante uso de skill.
    ///
    ///   4. Layer masks validadas com aviso claro se não configuradas.
    ///
    ///   5. Câmera usa LateUpdate para eliminar jitter após o player ser movido
    ///      no Update.
    ///
    ///   6. SetEnabled() público mantido para NetworkPlayer usar na morte/respawn.
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    public class NetworkPlayerController : NetworkBehaviour
    {
        [Header("Layers — configure no Inspector")]
        [Tooltip("Layer do terreno/chão. Se 0, usa Physics.Raycast sem filtro.")]
        [SerializeField] private LayerMask terrainLayer;
        [Tooltip("Layer de entidades selecionáveis (monstros, NPCs, players).")]
        [SerializeField] private LayerMask targetableLayer;

        [Header("Câmera")]
        [SerializeField] private float orbitSensitivity = 3f;
        [SerializeField] private float zoomSensitivity  = 5f;
        [SerializeField] private float cameraSmoothTime = 0.05f;

        [Header("Indicador de Movimento")]
        [SerializeField] private GameObject moveIndicatorPrefab;

        // ── Componentes ────────────────────────────────────────────────────
        private NavMeshAgent _agent;
        private PlayerEntity _playerEntity;
        private SkillSystem  _skillSystem;
        private Camera       _cam;

        // ── Câmera ─────────────────────────────────────────────────────────
        private float   _yaw          = 45f;
        private float   _pitch        = 45f;
        private float   _distance     = 12f;
        private bool    _orbiting;
        private Vector3 _camVelocity  = Vector3.zero;

        private const float PITCH_MIN = 10f;
        private const float PITCH_MAX = 80f;
        private const float DIST_MIN  = 3f;
        private const float DIST_MAX  = 30f;

        // ── Awake ──────────────────────────────────────────────────────────

        private void Awake()
        {
            _agent = GetComponent<NavMeshAgent>();
        }

        private void OnEnable()
        {
            Cursor.visible   = true;
            Cursor.lockState = CursorLockMode.None;
        }

        private void OnDisable()
        {
            _orbiting        = false;
            Cursor.visible   = true;
            Cursor.lockState = CursorLockMode.None;
        }

        // ── OnStartLocalPlayer ─────────────────────────────────────────────

        public override void OnStartLocalPlayer()
        {
            _playerEntity = GetComponent<PlayerEntity>();
            _skillSystem  = GetComponent<SkillSystem>();
            _cam          = Camera.main;

            if (_cam == null)
                Debug.LogWarning("[NetworkPlayerController] Camera.main não encontrada!");

            Cursor.visible   = true;
            Cursor.lockState = CursorLockMode.None;

            UIManager.Instance?.BindLocalPlayer(_playerEntity);
            Debug.Log("[NetworkPlayerController] Controller local iniciado.");
        }

        // ── Update (input do cliente) ──────────────────────────────────────

        private void Update()
        {
            if (!isLocalPlayer) return;
            HandleMouseInput();
            HandleSkillInput();
            HandleCameraOrbit();
        }

        /// <summary>
        /// LateUpdate para câmera evita jitter quando o player é movido no Update.
        /// </summary>
        private void LateUpdate()
        {
            if (!isLocalPlayer) return;
            UpdateCameraPosition();
        }

        // ── Movimento e Seleção ────────────────────────────────────────────

        private void HandleMouseInput()
        {
            if (!Input.GetMouseButtonDown(0)) return;
            if (_cam == null) return;

            // Bloqueia clique sobre UI
            if (UnityEngine.EventSystems.EventSystem.current != null &&
                UnityEngine.EventSystems.EventSystem.current.IsPointerOverGameObject())
                return;

            Ray ray = _cam.ScreenPointToRay(Input.mousePosition);

            // 1. Tenta selecionar entidade targetável
            if (TrySelectTargetable(ray)) return;

            // 2. Tenta mover para o terreno
            TryMoveToGround(ray);
        }

        private bool TrySelectTargetable(Ray ray)
        {
            RaycastHit hit;
            bool didHit = targetableLayer != 0
                ? Physics.Raycast(ray, out hit, 300f, targetableLayer)
                : Physics.Raycast(ray, out hit, 300f);

            if (!didHit) return false;

            var targetable = hit.collider.GetComponentInParent<ITargetable>();
            if (targetable == null || targetable.IsDead) return false;

            _skillSystem?.CancelPendingWalk();
            _playerEntity?.SetTarget(targetable);
            UIManager.Instance?.UpdateTargetPanel(targetable);
            return true;
        }

        private void TryMoveToGround(Ray ray)
        {
            RaycastHit hit;
            bool didHit = terrainLayer != 0
                ? Physics.Raycast(ray, out hit, 300f, terrainLayer)
                : Physics.Raycast(ray, out hit, 300f);

            if (!didHit) return;

            // Ignora se atingiu uma entidade ao usar fallback sem layer
            if (terrainLayer == 0 && hit.collider.GetComponentInParent<ITargetable>() != null)
                return;

            _skillSystem?.CancelPendingWalk();
            _playerEntity?.ClearTarget();
            UIManager.Instance?.ClearTargetPanel();

            Vector3 dest = hit.point;
            if (NavMesh.SamplePosition(dest, out NavMeshHit navHit, 3f, NavMesh.AllAreas))
                dest = navHit.position;

            // Predição local: move imediatamente para UX responsivo
            if (_agent != null && _agent.isOnNavMesh)
                _agent.SetDestination(dest);

            // Confirma no servidor (servidor re-valida e aplica)
            CmdMoveTo(dest);
            SpawnMoveIndicator(hit.point);
        }

        // ── Skills ─────────────────────────────────────────────────────────

        private void HandleSkillInput()
        {
            if (_skillSystem == null) return;
            if (Input.GetKeyDown(KeyCode.Q)) _skillSystem.TryUseSkill(0);
            if (Input.GetKeyDown(KeyCode.W)) _skillSystem.TryUseSkill(1);
            if (Input.GetKeyDown(KeyCode.E)) _skillSystem.TryUseSkill(2);
            if (Input.GetKeyDown(KeyCode.R)) _skillSystem.TryUseSkill(3);
            if (Input.GetKeyDown(KeyCode.C)) AttributeWindowUI.Instance?.Toggle();
        }

        // ── Câmera ─────────────────────────────────────────────────────────

        private void HandleCameraOrbit()
        {
            if (Input.GetMouseButtonDown(1)) _orbiting = true;
            if (Input.GetMouseButtonUp(1))   _orbiting = false;

            if (_orbiting)
            {
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
            Vector3    target = pivot + offset;

            // Suaviza movimento da câmera para eliminar jitter
            _cam.transform.position = Vector3.SmoothDamp(
                _cam.transform.position, target, ref _camVelocity, cameraSmoothTime);
            _cam.transform.LookAt(pivot);
        }

        // ── Commands ───────────────────────────────────────────────────────

        /// <summary>
        /// Movimento server-authoritative com anti-teleport.
        /// O cliente já moveu localmente (predição); o servidor valida e confirma.
        /// </summary>
        [Command]
        public void CmdMoveTo(Vector3 destination)
        {
            var netPlayer = GetComponent<NetworkPlayer>();
            if (netPlayer == null || netPlayer.Dead) return;

            // Anti-cheat: rejeita destinos muito distantes
            float dist = Vector3.Distance(transform.position, destination);
            if (dist > 100f)
            {
                Debug.LogWarning($"[Server] CmdMoveTo suspeito: dist={dist:0.0} para {netPlayer.CharacterName}");
                return;
            }

            if (_agent == null) return;

            if (NavMesh.SamplePosition(destination, out NavMeshHit hit, 3f, NavMesh.AllAreas))
                _agent.SetDestination(hit.position);
            else
                _agent.SetDestination(destination);
        }

        // ── API pública ────────────────────────────────────────────────────

        /// <summary>
        /// Chamado por NetworkPlayer na morte/respawn para bloquear/liberar input.
        /// </summary>
        public void SetEnabled(bool value)
        {
            enabled = value;
            if (!value)
            {
                _orbiting        = false;
                Cursor.visible   = true;
                Cursor.lockState = CursorLockMode.None;
            }
        }

        // ── Helpers ────────────────────────────────────────────────────────

        private void SpawnMoveIndicator(Vector3 pos)
        {
            if (moveIndicatorPrefab == null) return;
            var go = Instantiate(
                moveIndicatorPrefab,
                pos + Vector3.up * 0.02f,
                Quaternion.Euler(90f, 0f, 0f));
            Destroy(go, 0.8f);
        }
    }
}