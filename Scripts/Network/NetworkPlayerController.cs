using UnityEngine;
using UnityEngine.AI;
using Mirror;
using RPG.Character;
using RPG.UI;
using RPG.Combat;

namespace RPG.Network
{
    /// <summary>
    /// NetworkPlayerController — controller ÚNICO para o jogador online.
    ///
    /// SUBSTITUI completamente PlayerController + NetworkPlayerController antigos.
    /// Remove o PlayerController offline do prefab e usa apenas este.
    ///
    /// Responsabilidades:
    ///   - Input de movimento (LMB no terreno → CmdMoveTo)
    ///   - Input de seleção de alvo (LMB em Targetable)
    ///   - Input de skills (Q/W/E/R)
    ///   - Camera orbit (RMB)
    ///   - Só processa no isLocalPlayer
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    public class NetworkPlayerController : NetworkBehaviour
    {
        [Header("Layers")]
        [SerializeField] private LayerMask terrainLayer;
        [SerializeField] private LayerMask targetableLayer;

        [Header("Câmera")]
        [SerializeField] private float orbitSensitivity = 3f;

        // Referências locais
        private NavMeshAgent _agent;
        private PlayerEntity _playerEntity;
        private SkillSystem  _skillSystem;
        private Camera       _cam;

        // Estado de câmera
        private float _yaw;
        private float _pitch   = 45f;
        private float _distance = 12f;
        private bool  _orbiting;

        // Constraint de câmera
        private const float PITCH_MIN = 10f;
        private const float PITCH_MAX = 80f;
        private const float DIST_MIN  = 3f;
        private const float DIST_MAX  = 30f;

        private void Awake()
        {
            _agent = GetComponent<NavMeshAgent>();
        }

        public override void OnStartLocalPlayer()
        {
            _playerEntity = GetComponent<PlayerEntity>();
            _skillSystem  = GetComponent<SkillSystem>();
            _cam          = Camera.main;

            // Conecta câmera e HUD
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

            // Bloqueia clique sobre UI
            if (UnityEngine.EventSystems.EventSystem.current != null &&
                UnityEngine.EventSystems.EventSystem.current.IsPointerOverGameObject())
                return;

            Ray ray = _cam.ScreenPointToRay(Input.mousePosition);

            // 1) Testa Targetable primeiro
            if (Physics.Raycast(ray, out RaycastHit tHit, 200f, targetableLayer))
            {
                var targetable = tHit.collider.GetComponentInParent<ITargetable>();
                if (targetable != null)
                {
                    if (targetable.IsDead)
                    {
                        Debug.Log($"[Controller] Clique em {targetable.DisplayName} — já está morto.");
                        return;
                    }

                    _playerEntity?.SetTarget(targetable);
                    UIManager.Instance?.UpdateTargetPanel(targetable);
                    _skillSystem?.CancelPendingAction();
                    Debug.Log($"[Controller] Alvo selecionado: {targetable.DisplayName} | HP:{targetable.CurrentHP:0}/{targetable.MaxHP:0}");
                    return;
                }
            }

            // 2) Testa Terreno
            if (Physics.Raycast(ray, out RaycastHit gHit, 200f, terrainLayer))
            {
                _skillSystem?.CancelPendingAction();
                _playerEntity?.ClearTarget();
                UIManager.Instance?.ClearTargetPanel();

                CmdMoveTo(gHit.point);
                Debug.Log($"[Controller] Mover para {gHit.point}");
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
            if (Input.GetMouseButtonDown(1)) _orbiting = true;
            if (Input.GetMouseButtonUp(1))   _orbiting = false;

            if (_orbiting)
            {
                _yaw   += Input.GetAxis("Mouse X") * orbitSensitivity;
                _pitch -= Input.GetAxis("Mouse Y") * orbitSensitivity;
                _pitch  = Mathf.Clamp(_pitch, PITCH_MIN, PITCH_MAX);
            }

            _distance -= Input.GetAxis("Mouse ScrollWheel") * 5f;
            _distance  = Mathf.Clamp(_distance, DIST_MIN, DIST_MAX);
        }

        private void UpdateCameraPosition()
        {
            if (_cam == null) return;

            Quaternion rot    = Quaternion.Euler(_pitch, _yaw, 0f);
            Vector3    offset = rot * new Vector3(0f, 0f, -_distance);
            _cam.transform.position = transform.position + offset;
            _cam.transform.LookAt(transform.position + Vector3.up * 1.5f);
        }

        // ── Commands (Cliente → Servidor) ─────────────────────────────────

        [Command]
        private void CmdMoveTo(Vector3 destination)
        {
            if (NavMesh.SamplePosition(destination, out NavMeshHit hit, 2f, NavMesh.AllAreas))
                _agent.SetDestination(hit.position);
        }
    }
}