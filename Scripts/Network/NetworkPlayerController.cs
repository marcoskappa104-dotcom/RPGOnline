using UnityEngine;
using UnityEngine.AI;
using Mirror;
using RPG.Character;
using RPG.UI;
using RPG.Combat;

namespace RPG.Network
{
    /// <summary>
    /// NetworkPlayerController v12
    ///
    /// CORREÇÕES v12 (vs v11):
    ///
    ///   1. CmdMoveTo: validação de distância máxima aumentada de 80 para 120
    ///      para cobrir mapas maiores sem rejeitar movimentos legítimos.
    ///      O warning de segurança é mantido mas logado com menos frequência
    ///      (throttle de 2s para evitar flood de logs).
    ///
    ///   2. TryMoveToGround: ao clicar no chão, cancela auto-ataque E walk pendente
    ///      antes de aplicar o novo destino — evita conflito entre sistemas.
    ///
    ///   3. HandleMouseInput: a ordem de raycast agora verifica terreno PRIMEIRO
    ///      quando nenhum alvo é clicado, evitando que o click-move falhe em
    ///      terrenos com colliders sobrepostos a objetos targetable distantes.
    ///
    ///   4. UpdateCameraPosition: adicionado clamp de distância mínima ao terreno
    ///      (câmera não vai abaixo do chão mesmo com zoom máximo).
    ///
    ///   5. SetEnabled(false): garante cancelamento de auto-ataque e walk pendente
    ///      quando o controlador é desativado (morte do player, menus, etc.).
    ///
    ///   6. MELHORIA: duplo clique em monstro cancela walk pendente de skill antes
    ///      de registrar o clique no BasicAttackSystem — evita que as duas ações
    ///      coexistam causando comportamento errático.
    /// </summary>
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
        // CORREÇÃO v12: aumentado de 80 para 120
        private const float MAX_MOVE_DIST  = 120f;

        // Throttle do warning de segurança
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

            // Ordem de prioridade: item > monstro/entidade > terreno
            if (TryPickupItem(ray))        return;
            if (TryHandleMonsterClick(ray)) return;
            if (TrySelectTargetable(ray))  return;
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

            // CORREÇÃO v12: cancela walk pendente antes de registrar duplo clique
            // Evita coexistência de walk de skill + auto-ataque
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

            // CORREÇÃO v12: cancela ambos os sistemas antes de mover
            _skillSystem?.CancelPendingWalk();
            _basicAttack?.CancelAutoAttack();
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
            if (_playerEntity != null && _playerEntity.IsDead) return;

            if (Input.GetKeyDown(KeyCode.Q)) _skillSystem.TryUseSkill(0);
            if (Input.GetKeyDown(KeyCode.W)) _skillSystem.TryUseSkill(1);
            if (Input.GetKeyDown(KeyCode.E)) _skillSystem.TryUseSkill(2);
            if (Input.GetKeyDown(KeyCode.R)) _skillSystem.TryUseSkill(3);
            if (Input.GetKeyDown(KeyCode.C)) AttributeWindowUI.Instance?.Toggle();
        }

        private void HandleUIInput()
        {
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

        private void UpdateCameraPosition()
        {
            if (_cam == null) return;
            Quaternion rot    = Quaternion.Euler(_pitch, _yaw, 0f);
            Vector3    offset = rot * new Vector3(0f, 0f, -_distance);
            Vector3    pivot  = transform.position + Vector3.up * cameraHeight;
            Vector3    target = pivot + offset;

            // CORREÇÃO v12: evita câmera abaixo do terreno
            if (target.y < transform.position.y + 0.5f)
                target.y = transform.position.y + 0.5f;

            _cam.transform.position = Vector3.SmoothDamp(
                _cam.transform.position, target, ref _camVelocity, cameraSmoothTime);
            _cam.transform.LookAt(pivot);
        }

        // ── Commands ───────────────────────────────────────────────────────

        [Command]
        public void CmdMoveTo(Vector3 destination)
        {
            var netPlayer = GetComponent<NetworkPlayer>();
            if (netPlayer == null) return;
            if (netPlayer.Dead) return;

            float dist = Vector3.Distance(transform.position, destination);
            if (dist > MAX_MOVE_DIST)
            {
                // CORREÇÃO v12: throttle do warning para não flodar logs
                if (Time.time - _lastSecurityWarnTime >= SECURITY_WARN_INTERVAL)
                {
                    _lastSecurityWarnTime = Time.time;
                    Debug.LogWarning($"[Server] CmdMoveTo suspeito: dist={dist:0.0} para {netPlayer.CharacterName}");
                }
                return;
            }

            if (_agent == null) return;

            Vector3 finalDest = destination;
            bool snapped = NavMesh.SamplePosition(destination, out NavMeshHit hit, 3f, NavMesh.AllAreas) ||
                           NavMesh.SamplePosition(destination, out hit, 6f, NavMesh.AllAreas);

            if (snapped)
            {
                finalDest = hit.position;
            }
            else if (debugMovement)
            {
                Debug.LogWarning($"[Server] CmdMoveTo: destino {destination} fora do NavMesh para {netPlayer.CharacterName}");
            }

            _agent.SetDestination(finalDest);
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
                // CORREÇÃO v12: cancela todos os sistemas de movimento ao desativar
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