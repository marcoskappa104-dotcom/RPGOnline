using UnityEngine;
using UnityEngine.AI;
using Mirror;
using RPG.Character;
using RPG.UI;
using RPG.Combat;

namespace RPG.Network
{
    /// <summary>
    /// NetworkPlayerController v7 — Corrigido para RPG Online profissional.
    ///
    /// CORREÇÕES v7:
    ///
    ///   1. NETWORKтранsform AWARENESS:
    ///      Este controller usa predição local de movimento (cliente move imediatamente,
    ///      servidor valida). Para que isso funcione sem jitter, o componente
    ///      NetworkTransform no prefab do PLAYER LOCAL deve estar configurado com:
    ///        - Client Authority: TRUE  (ou usar NetworkTransformReliable com isOwned)
    ///      Caso contrário, o servidor vai sobrescrever a posição local todo frame,
    ///      causando o efeito de "andar travado".
    ///
    ///      CONFIGURAÇÃO OBRIGATÓRIA NO PREFAB:
    ///        NetworkTransform → Sync Direction: Client To Server
    ///        (Mirror 2022+: NetworkTransformReliable com syncDirection = ClientToServer)
    ///
    ///   2. Velocidade do NavMeshAgent do player configurada a partir dos stats
    ///      (MoveSpeed de DerivedStats, não ASPD).
    ///
    ///   3. Anti-cheat de CmdMoveTo com distância máxima de 80 unidades (jogador
    ///      não pode teleportar, mas o limite anterior de 100 era grande demais).
    ///
    ///   4. LateUpdate de câmera mantido para evitar jitter pós-movimento.
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
        private float   _yaw         = 45f;
        private float   _pitch       = 45f;
        private float   _distance    = 12f;
        private bool    _orbiting;
        private Vector3 _camVelocity = Vector3.zero;

        private const float PITCH_MIN = 10f;
        private const float PITCH_MAX = 80f;
        private const float DIST_MIN  = 3f;
        private const float DIST_MAX  = 30f;

        // ── Constantes ─────────────────────────────────────────────────────
        /// <summary>
        /// Distância máxima permitida para um CmdMoveTo.
        /// Previne teleporte via cheat. 80u cobre cliques em mapas grandes.
        /// </summary>
        private const float MAX_MOVE_DIST = 80f;

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

            // Configura velocidade do agente a partir dos stats do player
            // (será atualizado novamente quando InitializeFromServer rodar)
            if (_agent != null && _playerEntity != null && _playerEntity.Stats != null)
                _agent.speed = Mathf.Clamp(_playerEntity.Stats.MoveSpeed, 3f, 7f);

            Cursor.visible   = true;
            Cursor.lockState = CursorLockMode.None;

            UIManager.Instance?.BindLocalPlayer(_playerEntity);
            Debug.Log("[NetworkPlayerController] Controller local iniciado.");
        }

        // ── Update / LateUpdate ────────────────────────────────────────────

        private void Update()
        {
            if (!isLocalPlayer) return;
            HandleMouseInput();
            HandleSkillInput();
            HandleCameraOrbit();
        }

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

            if (UnityEngine.EventSystems.EventSystem.current != null &&
                UnityEngine.EventSystems.EventSystem.current.IsPointerOverGameObject())
                return;

            Ray ray = _cam.ScreenPointToRay(Input.mousePosition);

            if (TrySelectTargetable(ray)) return;
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

            if (terrainLayer == 0 && hit.collider.GetComponentInParent<ITargetable>() != null)
                return;

            _skillSystem?.CancelPendingWalk();
            _playerEntity?.ClearTarget();
            UIManager.Instance?.ClearTargetPanel();

            Vector3 dest = hit.point;
            if (NavMesh.SamplePosition(dest, out NavMeshHit navHit, 3f, NavMesh.AllAreas))
                dest = navHit.position;

            // Predição local: cliente move imediatamente para UX responsivo
            // IMPORTANTE: NetworkTransform do player DEVE ter syncDirection = ClientToServer
            // para que esta posição local não seja sobrescrita pelo servidor.
            if (_agent != null && _agent.isOnNavMesh)
                _agent.SetDestination(dest);

            // Confirma no servidor (servidor valida e aplica)
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

            _cam.transform.position = Vector3.SmoothDamp(
                _cam.transform.position, target, ref _camVelocity, cameraSmoothTime);
            _cam.transform.LookAt(pivot);
        }

        // ── Commands ───────────────────────────────────────────────────────

        /// <summary>
        /// Movimento server-authoritative com anti-teleport.
        ///
        /// NOTA SOBRE SINCRONIZAÇÃO:
        /// O cliente já moveu o agente localmente (predição). O servidor valida
        /// e aplica o mesmo destino. Como o NetworkTransform está configurado com
        /// ClientToServer, o servidor NÃO sobrescreve a posição do cliente local —
        /// apenas registra e transmite para outros jogadores.
        /// </summary>
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

            if (NavMesh.SamplePosition(destination, out NavMeshHit hit, 3f, NavMesh.AllAreas))
                _agent.SetDestination(hit.position);
            else
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
            var go = Instantiate(
                moveIndicatorPrefab,
                pos + Vector3.up * 0.02f,
                Quaternion.Euler(90f, 0f, 0f));
            Destroy(go, 0.8f);
        }
    }
}