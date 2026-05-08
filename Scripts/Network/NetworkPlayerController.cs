using UnityEngine;
using UnityEngine.AI;
using Mirror;
using RPG.Character;
using RPG.UI;
using RPG.Combat;

namespace RPG.Network
{
    [RequireComponent(typeof(NavMeshAgent))]
    public class NetworkPlayerController : NetworkBehaviour
    {
        [Header("Layers — configure no Inspector")]
        [Tooltip("Layer do terreno/chão.")]
        [SerializeField] private LayerMask terrainLayer;
        [Tooltip("Layer de entidades selecionáveis (monstros, NPCs, players).")]
        [SerializeField] private LayerMask targetableLayer;
        [Tooltip("Layer dos itens no chão (WorldItem).")]
        [SerializeField] private LayerMask itemLayer;

        [Header("Câmera")]
        [SerializeField] private float orbitSensitivity = 3f;
        [SerializeField] private float zoomSensitivity  = 5f;
        [SerializeField] private float cameraSmoothTime = 0.05f;

        [Header("Indicador de Movimento")]
        [SerializeField] private GameObject moveIndicatorPrefab;

        // ── Componentes ────────────────────────────────────────────────────
        private NavMeshAgent       _agent;
        private PlayerEntity       _playerEntity;
        private SkillSystem        _skillSystem;
        private BasicAttackSystem  _basicAttack;   // v9
        private Camera             _cam;

        // ── Câmera ─────────────────────────────────────────────────────────
        private float   _yaw         = 45f;
        private float   _pitch       = 45f;
        private float   _distance    = 12f;
        private bool    _orbiting;
        private Vector3 _camVelocity = Vector3.zero;

        private const float PITCH_MIN     = 10f;
        private const float PITCH_MAX     = 80f;
        private const float DIST_MIN      = 3f;
        private const float DIST_MAX      = 30f;
        private const float MAX_MOVE_DIST = 80f;

        // ── Awake ──────────────────────────────────────────────────────────

        private void Awake()
        {
            _agent       = GetComponent<NavMeshAgent>();
            _basicAttack = GetComponent<BasicAttackSystem>(); // v9
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
            _basicAttack  = GetComponent<BasicAttackSystem>(); // v9
            _cam          = Camera.main;

            if (_cam == null)
                Debug.LogWarning("[NetworkPlayerController] Camera.main não encontrada!");

            if (_agent != null && _playerEntity != null && _playerEntity.Stats != null)
                _agent.speed = Mathf.Clamp(_playerEntity.Stats.MoveSpeed, 3f, 7f);

            Cursor.visible   = true;
            Cursor.lockState = CursorLockMode.None;

            if (terrainLayer == 0)
                Debug.LogWarning("[NetworkPlayerController] terrainLayer não configurado!");
            if (targetableLayer == 0)
                Debug.LogWarning("[NetworkPlayerController] targetableLayer não configurado!");
            if (itemLayer == 0)
                Debug.LogWarning("[NetworkPlayerController] itemLayer não configurado! Pickup não vai funcionar.");

            UIManager.Instance?.BindLocalPlayer(_playerEntity);
            Debug.Log("[NetworkPlayerController] Controlador local iniciado.");
        }

        // ── Update / LateUpdate ────────────────────────────────────────────

        private void Update()
        {
            if (!isLocalPlayer) return;
            HandleMouseInput();
            HandleSkillInput();
            HandleCameraOrbit();
            HandleUIInput();
        }

        private void LateUpdate()
        {
            if (!isLocalPlayer) return;
            UpdateCameraPosition();
        }

        // ── Mouse Input ────────────────────────────────────────────────────

        private void HandleMouseInput()
        {
            if (!Input.GetMouseButtonDown(0)) return;
            if (_cam == null) return;

            if (UnityEngine.EventSystems.EventSystem.current != null &&
                UnityEngine.EventSystems.EventSystem.current.IsPointerOverGameObject())
                return;

            Ray ray = _cam.ScreenPointToRay(Input.mousePosition);

            // 1. Pickup de item (prioridade mais alta)
            if (TryPickupItem(ray)) return;

            // 2. Clique em monstro — detecta duplo clique para auto-ataque
            if (TryHandleMonsterClick(ray)) return;

            // 3. Seleção de outros alvos (NPCs, players)
            if (TrySelectTargetable(ray)) return;

            // 4. Mover para o chão
            TryMoveToGround(ray);
        }

        /// <summary>
        /// v9: verifica se o raycast acertou um monstro.
        /// Clique simples → seleciona alvo.
        /// Duplo clique  → inicia auto-ataque via BasicAttackSystem.
        /// Retorna true se acertou um monstro (consome o evento de qualquer forma).
        /// </summary>
        private bool TryHandleMonsterClick(Ray ray)
        {
            if (targetableLayer == 0) return false;

            if (!Physics.Raycast(ray, out RaycastHit hit, 300f, targetableLayer)) return false;

            var monster = hit.collider.GetComponentInParent<NetworkMonsterEntity>();
            if ((UnityEngine.Object)monster == null) return false;
            if (monster.IsDead) return false;

            // Sempre seleciona o alvo visualmente (clique simples ou duplo)
            _skillSystem?.CancelPendingWalk();
            _playerEntity?.SetTarget(monster);
            UIManager.Instance?.UpdateTargetPanel(monster);

            // Tenta registrar duplo clique no BasicAttackSystem
            if (_basicAttack != null)
                _basicAttack.TryRegisterClick(monster);

            // Retorna true independentemente — o evento pertence ao monstro
            return true;
        }

        /// <summary>
        /// v8: verifica se o raycast acertou um WorldItem e envia pickup.
        /// </summary>
        private bool TryPickupItem(Ray ray)
        {
            if (itemLayer == 0) return false;

            if (!Physics.Raycast(ray, out RaycastHit hit, 300f, itemLayer)) return false;

            var worldItem = hit.collider.GetComponentInParent<WorldItem>();
            if (worldItem == null) return false;

            uint myNetId = GetComponent<NetworkIdentity>().netId;
            worldItem.CmdPickUp(myNetId);
            return true;
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
            _basicAttack?.CancelAutoAttack();  // v9
            _playerEntity?.SetTarget(targetable);
            UIManager.Instance?.UpdateTargetPanel(targetable);
            return true;
        }

        private void TryMoveToGround(Ray ray)
        {
            RaycastHit hit;
            int moveLayerMask = terrainLayer != 0
                ? terrainLayer
                : ~(1 << LayerMask.NameToLayer("Targetable"));

            if (!Physics.Raycast(ray, out hit, 300f, moveLayerMask)) return;

            _skillSystem?.CancelPendingWalk();
            _basicAttack?.CancelAutoAttack();  // v9
            _playerEntity?.ClearTarget();
            UIManager.Instance?.ClearTargetPanel();

            Vector3 dest = hit.point;
            if (NavMesh.SamplePosition(dest, out NavMeshHit navHit, 3f, NavMesh.AllAreas))
                dest = navHit.position;

            if (_agent != null && _agent.isOnNavMesh)
                _agent.SetDestination(dest);

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

        private void HandleUIInput()
        {
            if (Input.GetKeyDown(KeyCode.I))
                InventoryUI.Instance?.Toggle();

            if (Input.GetKeyDown(KeyCode.P))
                PowerGemUI.Instance?.Toggle();
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

            _cam.transform.position = Vector3.SmoothDamp(
                _cam.transform.position, target, ref _camVelocity, cameraSmoothTime);
            _cam.transform.LookAt(pivot);
        }

        // ── Commands ───────────────────────────────────────────────────────

        [Command]
        public void CmdMoveTo(Vector3 destination)
        {
            var netPlayer = GetComponent<NetworkPlayer>();
            if (netPlayer == null || netPlayer.Dead) return;

            float dist = Vector3.Distance(transform.position, destination);
            if (dist > MAX_MOVE_DIST)
            {
                Debug.LogWarning($"[Server] CmdMoveTo suspeito: dist={dist:0.0} para {netPlayer.CharacterName}");
                return;
            }

            if (_agent == null) return;

            Vector3 finalDest = destination;
            if (NavMesh.SamplePosition(destination, out NavMeshHit hit, 3f, NavMesh.AllAreas))
            {
                finalDest = hit.position;
                _agent.SetDestination(finalDest);
            }
            else
            {
                _agent.SetDestination(finalDest);
            }

            RpcMoveConfirmed(finalDest);
        }

        [TargetRpc]
        private void RpcMoveConfirmed(Vector3 destination)
        {
            if (_agent != null && _agent.isOnNavMesh)
                _agent.SetDestination(destination);
        }

        // ── API pública ────────────────────────────────────────────────────

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
            var go = Instantiate(moveIndicatorPrefab, pos + Vector3.up * 0.02f, Quaternion.Euler(90f, 0f, 0f));
            Destroy(go, 0.8f);
        }
    }
}