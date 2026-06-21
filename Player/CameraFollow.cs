using UnityEngine;

namespace Player
{
    /// <summary>
    /// Smooth 2D camera follow for a platformer.
    /// Attach to the Main Camera.  Drag the player Transform into 'target'.
    /// </summary>
    public sealed class CameraFollow : MonoBehaviour
    {
        [SerializeField] private Transform target;
        [SerializeField] private float     smoothSpeed    = 5f;
        [SerializeField] private Vector3   offset         = new Vector3(0f, 1.5f, -10f);
        [SerializeField] private float     lookAheadX     = 1.5f;  // shift camera in facing direction
        [SerializeField] private bool      clampEnabled   = false;
        [SerializeField] private Vector2   minBounds;
        [SerializeField] private Vector2   maxBounds;

        private float _targetX;
        private float _fixedZ;

        private void Awake()
        {
            _fixedZ = offset.z;
        }

        private void Start()
        {
            // If no target was assigned in the Inspector, try to find the player.
            if (target == null)
            {
                var go = GameObject.FindGameObjectWithTag("Player");
                if (go != null) target = go.transform;
            }

            // Snap immediately so the camera does not slide in from (0,0,0) at level load.
            SnapToTarget();
        }

        private void LateUpdate()
        {
            if (target == null) return;

            // Look-ahead: shift camera slightly in the direction the player is moving
            float facingDir = target.localScale.x >= 0 ? 1f : -1f;
            _targetX = Mathf.Lerp(_targetX, target.position.x + facingDir * lookAheadX,
                                   Time.deltaTime * smoothSpeed);

            Vector3 desired  = new Vector3(_targetX + offset.x, target.position.y + offset.y, _fixedZ);
            Vector3 smoothed = Vector3.Lerp(transform.position, desired, smoothSpeed * Time.deltaTime);

            if (clampEnabled)
            {
                smoothed.x = Mathf.Clamp(smoothed.x, minBounds.x, maxBounds.x);
                smoothed.y = Mathf.Clamp(smoothed.y, minBounds.y, maxBounds.y);
            }

            transform.position = smoothed;
        }

        /// <summary>Snap immediately to target (call on scene load).</summary>
        public void SnapToTarget()
        {
            if (target == null) return;
            transform.position = new Vector3(target.position.x + offset.x, target.position.y + offset.y, _fixedZ);
            _targetX           = target.position.x;
        }

        public void SetTarget(Transform t) { target = t; SnapToTarget(); }
    }
}
