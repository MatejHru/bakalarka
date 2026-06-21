using UnityEngine;

namespace Gameplay
{
    /// <summary>
    /// Flying enemy that moves horizontally back and forth while oscillating vertically
    /// in a sinusoidal wave pattern.  Uses Rigidbody for collision detection but position is
    /// driven directly in Update.  Damages the player on contact.
    /// </summary>
    [RequireComponent(typeof(Rigidbody2D))]
    public sealed class FlyingEnemyController : MonoBehaviour
    {
        [SerializeField] private float moveSpeed     = 2.0f;
        [SerializeField] private float patrolRange   = 4.0f;
        [SerializeField] private float bobAmplitude  = 1.2f;
        [SerializeField] private float bobSpeed      = 1.8f;
        [SerializeField] private int   contactDamage = 1;
        [SerializeField] private float chaseRangeMultiplier = 1.5f;
        [SerializeField] private float turnDeadZone = 0.65f;
        [SerializeField] private float minTurnInterval = 0.25f;
        [SerializeField] private float maxVerticalChaseDifference = 5.0f;

        private Rigidbody2D    _rb;
        private SpriteRenderer _sr;
        private Vector3        _startPos;
        private int            _dir = 1;
        private float          _time;
        private bool           _dead;
        private Transform      _playerTransform;
        private Collider2D     _trigger;
        private float          _nextTurnAllowedAt;

        // ── Animation
        private float _tilt = 0f;

        // ──────────────────────────────────────────────────────────────────────
        private void Awake()
        {
            _rb       = GetComponent<Rigidbody2D>();
            if (_rb == null)
            {
                _rb = gameObject.AddComponent<Rigidbody2D>();
            }
            _rb.bodyType = RigidbodyType2D.Kinematic;
            _rb.gravityScale = 0f;
            _rb.freezeRotation = true;

            _sr       = GetComponentInChildren<SpriteRenderer>();
            _startPos = transform.position;

            // Ensure there is a trigger collider for player contact detection.
            bool hasTrigger = false;
            foreach (Collider2D c in GetComponents<Collider2D>())
                if (c.isTrigger) { hasTrigger = true; _trigger = c; break; }

            if (!hasTrigger)
            {
                var col       = gameObject.AddComponent<CapsuleCollider2D>();
                col.isTrigger = true;
                col.size      = new Vector2(0.8f, 1.4f);
                _trigger      = col;
            }
        }

        private void Start()
        {
            var player = GameObject.FindWithTag("Player");
            if (player != null) _playerTransform = player.transform;
        }

        private void Update()
        {
            if (_dead) return;

            _time += Time.deltaTime;

            // Pursuit with dead-zone to prevent rapid direction flipping
            if (_playerTransform != null)
            {
                float dx = _playerTransform.position.x - transform.position.x;
                float dy = _playerTransform.position.y - transform.position.y;
                float detectionDist = patrolRange * chaseRangeMultiplier;

                bool canChase =
                    Mathf.Abs(dx) < detectionDist &&
                    Mathf.Abs(dy) < maxVerticalChaseDifference &&
                    Mathf.Abs(dx) > turnDeadZone;

                if (canChase)
                {
                    TrySetDirection(dx > 0f ? 1 : -1);
                }
                else
                {
                    float dist = transform.position.x - _startPos.x;
                    if (dist >  patrolRange) TrySetDirection(-1, force: true);
                    if (dist < -patrolRange) TrySetDirection(1,  force: true);
                }
            }
            else
            {
                // Fallback patrol if player not found
                float dist = transform.position.x - _startPos.x;
                if (dist >  patrolRange) TrySetDirection(-1, force: true);
                if (dist < -patrolRange) TrySetDirection(1,  force: true);
            }

            // Banking tilt in direction of flight (pure visual)
            _tilt = Mathf.Lerp(_tilt, _dir * (-1.4f), Time.deltaTime * 4f);
            if (_sr != null)
                _sr.transform.localEulerAngles = new Vector3(0f, 0f, _tilt);
        }

        private void FixedUpdate()
        {
            if (_dead) return;

            // Update position via Rigidbody velocity (kinematic)
            float newX = _dir * moveSpeed;
            float newY = Mathf.Cos(_time * bobSpeed) * bobAmplitude * bobSpeed; // vertical oscillation velocity from bob wave
            _rb.linearVelocity = new Vector2(newX, newY);

            // Keep Y position oscillating around startPos (don't drift forever)
            float currentY = transform.position.y;
            float targetY = _startPos.y + Mathf.Sin(_time * bobSpeed) * bobAmplitude;
            if (Mathf.Abs(currentY - targetY) > bobAmplitude * 2f)
            {
                transform.position = new Vector3(transform.position.x, targetY, transform.position.z);
            }

            // Sprite flip
            if (_sr != null)
            {
                Vector3 s = _sr.transform.localScale;
                s.x = _dir > 0 ? Mathf.Abs(s.x) : -Mathf.Abs(s.x);
                _sr.transform.localScale = s;
            }
        }

        private void OnTriggerEnter2D(Collider2D other)
        {
            if (_dead) return;
            if (!other.CompareTag("Player")) return;

            var playerRb = other.attachedRigidbody;
            bool movingDown = playerRb != null && playerRb.linearVelocity.y < -0.05f;
            var refBounds = _trigger != null ? _trigger.bounds : GetComponent<Collider2D>().bounds;
            bool aboveEnemy = other.bounds.min.y >= refBounds.center.y;
            bool stomped = movingDown && aboveEnemy;

            if (stomped)
            {
                _dead = true;
                Game.GameSessionState.AddScore(100);
                UI.ScorePopup.Spawn(transform.position, 100);
                UI.ParticleBurst.Spawn(transform.position, _sr != null ? _sr.color : new Color(0.4f, 0.8f, 1f), 8);
                if (playerRb != null)
                    playerRb.linearVelocity = new Vector2(playerRb.linearVelocity.x, 11f);
                Destroy(gameObject);
            }
            else
            {
                var ph = Player.PlayerHealth.Instance;
                ph?.TakeDamage(contactDamage);
                ph?.ApplyKnockback(transform.position);
            }
        }

        // ── Public API ─────────────────────────────────────────────────────────

        /// <summary>Apply a generated sprite to this enemy's SpriteRenderer.</summary>
        public void ApplySprite(Sprite sprite)
        {
            if (_sr != null) _sr.sprite = sprite;
        }

        /// <summary>
        /// Set the spawn/patrol origin.  The enemy will fly around this point.
        /// The Y value should be above ground so the bob wave does not clip the floor.
        /// </summary>
        public void SetSpawnOrigin(Vector3 worldPos)
        {
            transform.position = worldPos;
            _startPos          = worldPos;
        }

        /// <summary>Apply gameplay stats from the current level plan.</summary>
        public void ApplyStats(float speed, float range, int damage)
        {
            moveSpeed     = Mathf.Max(0.3f, speed);
            patrolRange   = Mathf.Max(1.0f, range);
            contactDamage = Mathf.Max(1, damage);
        }

        private void TrySetDirection(int newDir, bool force = false)
        {
            newDir = newDir >= 0 ? 1 : -1;

            if (!force && newDir == _dir) return;
            if (!force && Time.time < _nextTurnAllowedAt) return;

            _dir = newDir;
            _nextTurnAllowedAt = Time.time + minTurnInterval;
        }
    }
}
