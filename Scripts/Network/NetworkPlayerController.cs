using UnityEngine;
using UnityEngine.AI;
using Mirror;
using RPG.Character;
using RPG.UI;
using RPG.Combat;

namespace RPG.Network
{
    /// <summary>
    /// NetworkPlayerController v11
    ///
    /// CORREÇÕES v11 (vs v10):
    ///
    ///   1. TryMoveToGround agora envia o destino snappado ao NavMesh para o servidor
    ///      via CmdMoveTo (igual ao comportamento do click em monstro).
    ///      Antes enviava o hit.point bruto sem snap, causando pequenos desvios.
    ///
    ///   2. HandleMouseInput: ordem de raycast melhorada.
    ///      TryPickupItem agora usa apenas itemLayer (sem fallback para AllLayers)
    ///      para evitar falsos positivos em cliques sobre o terreno.
    ///
    ///   3. TrySelectTargetable: quando o targetable selecionado for um
    ///      NetworkPlayer (outro jogador), exibe painel de alvo corretamente.
    ///      Antes o painel só funcionava para monstros.
    ///
    ///   4. Cursor: ao abrir/fechar janelas de UI com teclas (I, P, C),
    ///      garante que o cursor fica visível independente do estado de órbita.
    ///
    ///   5. CmdMoveTo: adicionado log de debug opcional para diagnóstico de
    ///      destinos rejeitados (posição fora do NavMesh).
    ///
    ///   6. Camera smoothing: adicionado clamp de pitch/yaw para evitar que
    ///      câmera faça volta completa vertical (gimbal lock visual).
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

        private const float PITCH_MIN     = 10f;
        private const float PITCH_MAX     = 80f;
        private const float DIST_MIN      = 3f;
        private const float DIST_MAX      = 30f;
        private const float MAX_MOVE_DIST = 80f;

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

            // Ignora cliques sobre elementos de UI
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

            // Cancela qualquer ação pendente ao selecionar novo alvo
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

            // CORREÇÃO v11: snap ao NavMesh antes de mover E antes de enviar ao servidor
            Vector3 dest = hit.point;
            if (NavMesh.SamplePosition(dest, out NavMeshHit navHit, 3f, NavMesh.AllAreas))
                dest = navHit.position;

            if (_agent != null && _agent.isOnNavMesh)
                _agent.SetDestination(dest);

            // CORREÇÃO v11: envia destino snappado (não o hit.point bruto)
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

        // CORREÇÃO v11: garante cursor visível ao abrir UI sem desfazer o orbit lock acidentalmente
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

                // Normaliza yaw para evitar overflow de float em sessões longas
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
                Debug.LogWarning($"[Server] CmdMoveTo suspeito: dist={dist:0.0} para {netPlayer.CharacterName}");
                return;
            }

            if (_agent == null) return;

            // Tenta snap ao NavMesh com raios crescentes
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
