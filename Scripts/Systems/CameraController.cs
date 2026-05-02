using UnityEngine;

namespace RPG.Systems
{
    /// <summary>
    /// CameraController — câmera isométrica/top-down que segue o player.
    ///   RMB + arrastar → orbitar (yaw + pitch)
    ///   Scroll wheel   → zoom (distância)
    ///   Sempre olha para o player
    /// </summary>
    public class CameraController : MonoBehaviour
    {
        [Header("Target")]
        [SerializeField] private Transform target; // player transform

        [Header("Orbit")]
        [SerializeField] private float yaw          = 45f;   // ângulo horizontal inicial
        [SerializeField] private float pitch         = 50f;   // ângulo vertical inicial
        [SerializeField] private float minPitch      = 15f;
        [SerializeField] private float maxPitch      = 75f;
        [SerializeField] private float rotateSpeed   = 200f;

        [Header("Zoom")]
        [SerializeField] private float distance      = 12f;
        [SerializeField] private float minDistance   = 3f;
        [SerializeField] private float maxDistance   = 30f;
        [SerializeField] private float zoomSpeed     = 5f;
        [SerializeField] private float zoomSmoothing = 8f;

        [Header("Follow")]
        [SerializeField] private float followSmoothing = 10f;
        [SerializeField] private Vector3 offset = Vector3.zero; // offset acima do player

        private float   _targetDistance;
        private Vector3 _currentVelocity;

        private void Awake()
        {
            _targetDistance = distance;

            // Se não setado no Inspector, tenta achar o player automaticamente
            if (target == null)
            {
                var player = FindObjectOfType<RPG.Character.PlayerEntity>();
                if (player != null) target = player.transform;
            }
        }

        private void LateUpdate()
        {
            if (target == null) return;

            HandleRotation();
            HandleZoom();
            ApplyTransform();
        }

        private void HandleRotation()
        {
            if (!Input.GetMouseButton(1)) return; // só RMB pressionado

            yaw   += Input.GetAxis("Mouse X") * rotateSpeed * Time.deltaTime;
            pitch -= Input.GetAxis("Mouse Y") * rotateSpeed * Time.deltaTime;
            pitch  = Mathf.Clamp(pitch, minPitch, maxPitch);
        }

        private void HandleZoom()
        {
            float scroll = Input.GetAxis("Mouse ScrollWheel");
            if (Mathf.Abs(scroll) > 0.01f)
                _targetDistance -= scroll * zoomSpeed;

            _targetDistance = Mathf.Clamp(_targetDistance, minDistance, maxDistance);
            distance = Mathf.Lerp(distance, _targetDistance, zoomSmoothing * Time.deltaTime);
        }

        private void ApplyTransform()
        {
            Quaternion rotation = Quaternion.Euler(pitch, yaw, 0f);
            Vector3 desiredPos  = target.position + offset + rotation * new Vector3(0, 0, -distance);

            // Smooth follow
            transform.position = Vector3.SmoothDamp(
                transform.position, desiredPos, ref _currentVelocity, followSmoothing * Time.deltaTime);

            transform.LookAt(target.position + offset);
        }

        public void SetTarget(Transform newTarget)
        {
            target = newTarget;
        }
    }
}
