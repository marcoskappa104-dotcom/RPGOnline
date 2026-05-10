using UnityEngine;
using UnityEngine.AI;
using Mirror;
using RPG.Character;
using RPG.UI;
using RPG.Combat;

namespace RPG.Network
{
    /// <summary>
    /// NetworkPlayerController v13
    ///
    /// CORREÇÕES v13:
    ///
    ///   BUG — HandleSkillInput ativava skills ao digitar em campos de texto:
    ///     Input.GetKeyDown(KeyCode.W) disparava skill enquanto o jogador
    ///     digitava o nome do personagem ou em qualquer TMP_InputField,
    ///     porque não havia verificação de foco de UI.
    ///     SOLUÇÃO: guard IsTypingInField() verifica EventSystem.currentSelectedGameObject
    ///     e se é TMP_InputField antes de processar qualquer atalho de teclado.
    ///
    ///   BUG — CmdMoveTo chamava RpcMoveConfirmed causando double SetDestination:
    ///     O cliente chamava _agent.SetDestination localmente em TryMoveToGround,
    ///     e o servidor confirmava via RpcMoveConfirmed que chamava SetDestination
    ///     novamente no mesmo cliente — resultando em micro-jitter e possível
    ///     conflito com o destino atual.
    ///     SOLUÇÃO: RpcMoveConfirmed removido de CmdMoveTo. O servidor aplica
    ///     o movimento autoritativo localmente; o cliente já aplica localmente
    ///     sem precisar de confirmação (prediction local).
    ///
    ///   BUG — Câmera podia passar por geometria com zoom máximo:
    ///     UpdateCameraPosition não fazia raycast contra o terreno para detectar
    ///     oclusão. Adicionado CameraOcclusionCheck que empurra a câmera para
    ///     frente quando há geometria entre o pivot e a posição calculada.
    ///
    ///   MELHORIA — SetEnabled(false) agora reseta orbiting e cursor imediatamente.
    ///
    ///   Todas as correções v12 mantidas.
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    public class NetworkPlayerController : NetworkBehaviour
    {
        [Header("Layers — configure no Inspector")]
        [SerializeField] private LayerMask terrainLayer;
        [SerializeField] private LayerMask targetableLayer;
        [SerializeField] private LayerMask itemLayer;
        [Tooltip("Layers que bloqueiam a câmera (terreno + paredes). Normalmente = terrainLayer.")]
        [SerializeField] private LayerMask cameraOcclusionLayer;

        [Header("Câmera")]
        [SerializeField] private float orbitSensitivity = 3f;
        [SerializeField] private float zoomSensitivity  = 5f;
        [SerializeField] private float cameraSmoothTime = 0.05f;
        [SerializeField] private float cameraHeight     = 1.5f;

        [Header("Indicador de Movimento")]
        [SerializeField] private GameObject moveIndicatorPrefab;

        [Header("Debug")]
        [SerializeField] private bool debugMovement = false;

        // ── Componentes ────────────────────────────────────────────────────
        private NavMeshAgent       _agent;
        private PlayerEntity       _playerEntity;
        private SkillSystem        _skillSystem;
        private BasicAttackSystem  _basicAttack;
        private Camera             _cam;

        // ── Câmera ─────────────────────────────────────────────────────────
        private float   _yaw         = 45f;
        private float   _pitch       = 45f;
        private float   _distance    = 12f;
        private bool    _orbiting;
        private Vector3 _camVelocity = Vector3.zero;

        private const float PITCH_MIN      = 10f;
        private const float PITCH_MAX      = 80f;
        private const float DIST_MIN       = 3f;
        private const float DIST_MAX       = 30f;
        private const float MAX_MOVE_DIST  = 120f;
        private const float CAM_SKIN_WIDTH = 0.3f;

        private float _lastSecurityWarnTime = -999f;
        private const float SECURITY_WARN_INTERVAL = 2f;

        // ── Awake ──────────────────────────────────────────────────────────

        private void Awake()
        {
            _agent       = GetComponent<NavMeshAgent>();
            _basicAttack = GetComponent<BasicAttackSystem>();
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
            _basicAttack  = GetComponent<BasicAttackSystem>();
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

            if (TryPickupItem(ray))         return;
            if (TryHandleMonsterClick(ray)) return;
            if (TrySelectTargetable(ray))   return;
            TryMoveToGround(ray);
        }

        private bool TryHandleMonsterClick(Ray ray)
        {
            if (targetableLayer == 0) return false;
            if (!Physics.Raycast(ray, out RaycastHit hit, 300f, targetableLayer)) return false;

            var monster = hit.collider.GetComponentInParent<NetworkMonsterEntity>();
            if ((UnityEngine.Object)monster == null) return false;
            if (monster.IsDead) return false;

            bool targetChanged = _playerEntity != null &&
                                 _playerEntity.CurrentTarget != (ITargetable)monster;

            if (targetChanged && _basicAttack != null && _basicAttack.IsAutoAttacking)
                _basicAttack.CancelAutoAttack();

            _skillSystem?.CancelPendingWalk();
            _playerEntity?.SetTarget(monster);
            UIManager.Instance?.UpdateTargetPanel(monster);
            _basicAttack?.TryRegisterClick(monster);

            return true;
        }

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
            if (targetableLayer == 0) return false;
            if (!Physics.Raycast(ray, out RaycastHit hit, 300f, targetableLayer)) return false;

            var targetable = hit.collider.GetComponentInParent<ITargetable>();
            if (targetable == null || targetable.IsDead) return false;

            _skillSystem?.CancelPendingWalk();
            _basicAttack?.CancelAutoAttack();
            _playerEntity?.SetTarget(targetable);
            UIManager.Instance?.UpdateTargetPanel(targetable);
            return true;
        }

        private void TryMoveToGround(Ray ray)
        {
            int moveLayerMask = terrainLayer != 0
                ? (int)terrainLayer
                : ~(1 << LayerMask.NameToLayer("Targetable"));

            if (!Physics.Raycast(ray, out RaycastHit hit, 300f, moveLayerMask)) return;

            _skillSystem?.CancelPendingWalk();
            _basicAttack?.CancelAutoAttack();
            _playerEntity?.ClearTarget();
            UIManager.Instance?.ClearTargetPanel();

            Vector3 dest = hit.point;
            if (NavMesh.SamplePosition(dest, out NavMeshHit navHit, 3f, NavMesh.AllAreas))
                dest = navHit.position;

            // Aplica localmente (prediction) — CmdMoveTo sincroniza com o servidor
            if (_agent != null && _agent.isOnNavMesh)
                _agent.SetDestination(dest);

            CmdMoveTo(dest);
            SpawnMoveIndicator(hit.point);
        }

        // ── Skills ─────────────────────────────────────────────────────────

        /// <summary>
        /// CORREÇÃO v13: verifica se o jogador está digitando em algum campo de input
        /// antes de processar atalhos de teclado. Previne skill ao digitar no chat, etc.
        /// </summary>
        private void HandleSkillInput()
        {
            if (_skillSystem == null) return;
            if (_playerEntity != null && _playerEntity.IsDead) return;

            // Não processa atalhos se estiver digitando em qualquer campo de texto
            if (IsTypingInField()) return;

            if (Input.GetKeyDown(KeyCode.Q)) _skillSystem.TryUseSkill(0);
            if (Input.GetKeyDown(KeyCode.W)) _skillSystem.TryUseSkill(1);
            if (Input.GetKeyDown(KeyCode.E)) _skillSystem.TryUseSkill(2);
            if (Input.GetKeyDown(KeyCode.R)) _skillSystem.TryUseSkill(3);
            if (Input.GetKeyDown(KeyCode.C)) AttributeWindowUI.Instance?.Toggle();
        }

        /// <summary>
        /// Retorna true se o foco atual é um campo de texto (TMP_InputField ou InputField).
        /// Impede que atalhos de gameplay se ativem enquanto o jogador digita.
        /// </summary>
        private static bool IsTypingInField()
        {
            var selected = UnityEngine.EventSystems.EventSystem.current?.currentSelectedGameObject;
            if (selected == null) return false;
            return selected.GetComponent<TMPro.TMP_InputField>() != null
                || selected.GetComponent<UnityEngine.UI.InputField>() != null;
        }

        private void HandleUIInput()
        {
            if (IsTypingInField()) return;

            if (Input.GetKeyDown(KeyCode.I))
            {
                EnsureCursorVisible();
                InventoryUI.Instance?.Toggle();
            }
            if (Input.GetKeyDown(KeyCode.P))
            {
                EnsureCursorVisible();
                PowerGemUI.Instance?.Toggle();
            }
        }

        private void EnsureCursorVisible()
        {
            if (!_orbiting)
            {
                Cursor.visible   = true;
                Cursor.lockState = CursorLockMode.None;
            }
        }

        // ── Câmera ─────────────────────────────────────────────────────────

        private void HandleCameraOrbit()
        {
            if (Input.GetMouseButtonDown(1))
            {
                _orbiting = true;
                Cursor.visible   = false;
                Cursor.lockState = CursorLockMode.Locked;
            }
            if (Input.GetMouseButtonUp(1))
            {
                _orbiting        = false;
                Cursor.visible   = true;
                Cursor.lockState = CursorLockMode.None;
            }

            if (_orbiting)
            {
                _yaw   += Input.GetAxis("Mouse X") * orbitSensitivity;
                _pitch -= Input.GetAxis("Mouse Y") * orbitSensitivity;
                _pitch  = Mathf.Clamp(_pitch, PITCH_MIN, PITCH_MAX);

                if (_yaw > 360f)  _yaw -= 360f;
                if (_yaw < -360f) _yaw += 360f;
            }

            _distance -= Input.GetAxis("Mouse ScrollWheel") * zoomSensitivity;
            _distance  = Mathf.Clamp(_distance, DIST_MIN, DIST_MAX);
        }

        /// <summary>
        /// Posiciona a câmera com anti-oclusão: se houver geometria entre o pivot
        /// e a posição calculada, empurra a câmera para frente (reduz distância).
        /// </summary>
        private void UpdateCameraPosition()
        {
            if (_cam == null) return;

            Quaternion rot   = Quaternion.Euler(_pitch, _yaw, 0f);
            Vector3    pivot = transform.position + Vector3.up * cameraHeight;
            Vector3    dir   = rot * new Vector3(0f, 0f, -1f);

            // Anti-oclusão: verifica se há obstáculo entre o pivot e a câmera
            float effectiveDistance = _distance;
            int   occlusionMask = cameraOcclusionLayer != 0 ? (int)cameraOcclusionLayer : (int)terrainLayer;
            if (occlusionMask != 0 &&
                Physics.SphereCast(pivot, CAM_SKIN_WIDTH, dir, out RaycastHit camHit,
                                   _distance, occlusionMask))
            {
                effectiveDistance = Mathf.Max(DIST_MIN, camHit.distance - CAM_SKIN_WIDTH);
            }

            Vector3 target = pivot + dir * effectiveDistance;

            // Clamp mínimo acima do chão
            if (target.y < transform.position.y + 0.5f)
                target.y = transform.position.y + 0.5f;

            _cam.transform.position = Vector3.SmoothDamp(
                _cam.transform.position, target, ref _camVelocity, cameraSmoothTime);
            _cam.transform.LookAt(pivot);
        }

        // ── Commands ───────────────────────────────────────────────────────

        /// <summary>
        /// CORREÇÃO v13: RpcMoveConfirmed REMOVIDO.
        /// O cliente já aplicou SetDestination localmente (prediction).
        /// O servidor apenas valida e aplica no objeto autoritativo.
        /// Enviar RpcMoveConfirmed causava double SetDestination no cliente,
        /// resultando em micro-jitter e conflito com destinos locais recentes.
        /// </summary>
        [Command]
        public void CmdMoveTo(Vector3 destination)
        {
            var netPlayer = GetComponent<NetworkPlayer>();
            if (netPlayer == null) return;
            if (netPlayer.Dead) return;

            float dist = Vector3.Distance(transform.position, destination);
            if (dist > MAX_MOVE_DIST)
            {
                if (Time.time - _lastSecurityWarnTime >= SECURITY_WARN_INTERVAL)
                {
                    _lastSecurityWarnTime = Time.time;
                    Debug.LogWarning($"[Server] CmdMoveTo suspeito: dist={dist:0.0} para {netPlayer.CharacterName}");
                }
                return;
            }

            if (_agent == null) return;

            Vector3 finalDest = destination;
            if (NavMesh.SamplePosition(destination, out NavMeshHit hit, 3f, NavMesh.AllAreas) ||
                NavMesh.SamplePosition(destination, out hit, 6f, NavMesh.AllAreas))
            {
                finalDest = hit.position;
            }
            else if (debugMovement)
            {
                Debug.LogWarning($"[Server] CmdMoveTo: destino fora do NavMesh para {netPlayer.CharacterName}");
            }

            // Aplica apenas no servidor (sem RpcMoveConfirmed)
            _agent.SetDestination(finalDest);
        }

        // ── API pública ────────────────────────────────────────────────────

        public void SetEnabled(bool value)
        {
            enabled = value;
            if (!value)
            {
                _basicAttack?.CancelAutoAttack();
                _skillSystem?.CancelPendingWalk();

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
