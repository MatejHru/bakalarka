using UnityEngine;

namespace Gameplay
{
    /// <summary>
    /// Simple patrol enemy.  Walks back and forth within patrolDistance of its
    /// spawn position.  Kills itself when the player jumps on top; damages the
    /// player if they touch from the side or below.
    /// </summary>
    [RequireComponent(typeof(Rigidbody2D))]
    public sealed class EnemyController : MonoBehaviour
    {
        [SerializeField] private float moveSpeed      = 2f;
        [SerializeField] private float patrolDistance = 3f;
        [SerializeField] private int   contactDamage  = 1;
        [SerializeField] private float stompBounceVelocity = 11f;
        [SerializeField] private int   stompScore = 100;
        [SerializeField] private LayerMask groundMask = ~0;
        [SerializeField] private float edgeCheckDistance = 0.55f;
        [SerializeField] private float wallCheckDistance = 0.48f;
        [SerializeField] private float chaseRangeMultiplier = 1.5f;
        [SerializeField] private float chaseVerticalTolerance = 2.25f;
        [SerializeField] private float turnDeadZone = 0.55f;
        [SerializeField] private float minTurnInterval = 0.20f;
        [SerializeField] private float speedSmoothing = 8f;

        private Rigidbody2D    _rb;
        private SpriteRenderer _sr;
        private Collider2D     _physicsCollider;  // non-trigger collider — used for accurate stomp detection
        private Vector3        _startPos;
        private int            _dir = 1;
        private bool           _dead;
        private Transform      _playerTransform;
        private float          _speedJitter;
        private float          _motionPhase;
        private float          _nextTurnAllowedAt;
        private float          _currentMoveSpeed;

        // ── Animation ──────────────────────────────────────────────────
        private int   _prevDir     = 1;
        private float _squashTimer = 0f;

        // ──────────────────────────────────────────────────────────────────
        private void Awake()
        {
            _rb       = GetComponent<Rigidbody2D>();
            _rb.freezeRotation = true;
            _sr       = GetComponentInChildren<SpriteRenderer>();
            _startPos = transform.position;

            // OnTriggerEnter2D requires a trigger collider for player interaction.
            // But a SEPARATE non-trigger collider is also needed for ground physics.
            // If the scene only has a trigger collider, the enemy falls through everything.
            bool hasSolidCollider = false;
            foreach (Collider2D c in GetComponents<Collider2D>())
            {
                if (!c.isTrigger)
                {
                    hasSolidCollider = true;
                    if (_physicsCollider == null) _physicsCollider = c;  // cache first solid collider
                }
            }

            if (!hasSolidCollider)
            {
                var col       = gameObject.AddComponent<CapsuleCollider2D>();
                col.isTrigger = false;
                // Height 1.8 matches the 2-unit-tall enemy sprite (center pivot, 1.8-unit body)
                // so the physics body and visual bottom align with the floor surface.
                col.size      = new Vector2(0.8f, 1.8f);
                _physicsCollider = col;
                Debug.LogWarning("[EnemyController] No non-trigger Collider2D found — " +
                                 "added CapsuleCollider2D for ground physics. " +
                                 "Add a proper collider in the prefab to remove this warning.");
            }

            // Also ensure a trigger collider exists for OnTriggerEnter2D player contact.
            bool hasTriggerCollider = false;
            foreach (Collider2D c in GetComponents<Collider2D>())
                if (c.isTrigger) { hasTriggerCollider = true; break; }

            if (!hasTriggerCollider)
            {
                var col       = gameObject.AddComponent<CapsuleCollider2D>();
                col.isTrigger = true;
                col.size      = new Vector2(0.9f, 1.9f);
            }
        }

        private void Start()
        {
            var player = GameObject.FindWithTag("Player");
            if (player != null) _playerTransform = player.transform;

            // Per-instance variation makes enemy movement less robotic.
            _speedJitter = Random.Range(0.85f, 1.15f);
            _motionPhase = Random.Range(0f, Mathf.PI * 2f);
            _currentMoveSpeed = moveSpeed;
        }

        private void FixedUpdate()
        {
            if (_dead) return;

            // Pursuit with dead-zone to prevent rapid direction flipping
            if (_playerTransform != null)
            {
                float dx = _playerTransform.position.x - transform.position.x;
                float dy = _playerTransform.position.y - transform.position.y;
                float detectionDist = patrolDistance * chaseRangeMultiplier;

                bool canChase =
                    Mathf.Abs(dx) < detectionDist &&
                    Mathf.Abs(dy) <= chaseVerticalTolerance &&
                    Mathf.Abs(dx) > turnDeadZone;

                if (canChase)
                {
                    TrySetDirection(dx > 0f ? 1 : -1);
                }
                else
                {
                    float dist = transform.position.x - _startPos.x;
                    if (dist >  patrolDistance) TrySetDirection(-1, force: true);
                    if (dist < -patrolDistance) TrySetDirection(1,  force: true);
                }
            }
            else
            {
                // Fallback patrol if player not found
                float dist = transform.position.x - _startPos.x;
                if (dist >  patrolDistance) TrySetDirection(-1, force: true);
                if (dist < -patrolDistance) TrySetDirection(1,  force: true);
            }

            if (HasWallAhead(_dir) || !HasGroundAhead(_dir))
                TrySetDirection(-_dir, force: true);

            // Squash on direction change
            if (_dir != _prevDir)
                _squashTimer = 0.08f;
            _prevDir = _dir;

            float pulse = 1.00f + 0.015f * Mathf.Sin(Time.time * 1.6f + _motionPhase);
            float targetSpeed = moveSpeed * _speedJitter * pulse;
            _currentMoveSpeed = Mathf.Lerp(_currentMoveSpeed, targetSpeed, Time.fixedDeltaTime * speedSmoothing);
            _rb.linearVelocity = new Vector2(_dir * _currentMoveSpeed, _rb.linearVelocity.y);

            if (_sr != null)
            {
                Vector3 s = _sr.transform.localScale;
                s.x = _dir > 0 ? Mathf.Abs(s.x) : -Mathf.Abs(s.x);
                _sr.transform.localScale = s;
            }
        }

        private void Update()
        {
            if (_dead || _sr == null) return;

            float dt    = Time.deltaTime;
            float signX = Mathf.Sign(_sr.transform.localScale.x);
            if (signX == 0f) signX = 1f;

            float targetSX = 1f;
            float targetSY = 1f;

            if (_squashTimer > 0f)
            {
                _squashTimer -= dt;
                float t  = Mathf.Clamp01(_squashTimer / 0.08f);
                targetSX = Mathf.Lerp(1f, 1.10f, t);
                targetSY = Mathf.Lerp(1f, 0.92f, t);
            }
            else if (!_dead && Mathf.Abs(_rb.linearVelocity.x) > 0.1f)
            {
                // Subtle walk bob
                float bob = 0.012f * Mathf.Sin(Time.time * 9f + _motionPhase);
                targetSY  = 1f + bob;
            }

            _sr.transform.localScale = new Vector3(signX * targetSX, targetSY, 1f);
        }

        private void TrySetDirection(int newDir, bool force = false)
        {
            newDir = newDir >= 0 ? 1 : -1;

            if (!force && newDir == _dir) return;
            if (!force && Time.time < _nextTurnAllowedAt) return;

            _dir = newDir;
            _nextTurnAllowedAt = Time.time + minTurnInterval;
        }

        private bool HasGroundAhead(int dir)
        {
            if (_physicsCollider == null) return true;

            Bounds b = _physicsCollider.bounds;
            Vector2 origin = new Vector2(
                b.center.x + dir * b.extents.x * 0.95f,
                b.min.y + 0.05f);

            RaycastHit2D hit = Physics2D.Raycast(origin, Vector2.down, edgeCheckDistance, groundMask);
            if (!hit.collider) return false;
            if (hit.collider.transform == transform || hit.collider.transform.IsChildOf(transform)) return false;
            return true;
        }

        private bool HasWallAhead(int dir)
        {
            if (_physicsCollider == null) return false;

            Bounds b = _physicsCollider.bounds;
            Vector2 origin = new Vector2(
                b.center.x,
                b.center.y);

            RaycastHit2D hit = Physics2D.Raycast(origin, Vector2.right * dir, wallCheckDistance, groundMask);
            if (!hit.collider) return false;
            if (hit.collider.transform == transform || hit.collider.transform.IsChildOf(transform)) return false;
            return true;
        }

        private void OnTriggerEnter2D(Collider2D other)
        {
            if (_dead) return;
            if (!other.CompareTag("Player")) return;

            var playerRb = other.attachedRigidbody;
            bool movingDown = playerRb != null && playerRb.linearVelocity.y < -0.05f;
            var refCollider = _physicsCollider != null ? _physicsCollider : GetComponent<Collider2D>();
            bool aboveEnemy = other.bounds.min.y >= refCollider.bounds.center.y;
            bool stomped = movingDown && aboveEnemy;

            if (stomped)
            {
                _dead = true;
                Game.GameSessionState.AddScore(stompScore);
                UI.ScorePopup.Spawn(transform.position, stompScore);
                UI.ParticleBurst.Spawn(transform.position, _sr != null ? _sr.color : new Color(1f, 0.6f, 0.15f), 8);
                if (playerRb != null)
                    playerRb.linearVelocity = new Vector2(playerRb.linearVelocity.x, stompBounceVelocity);
                Destroy(gameObject);
            }
            else
            {
                var ph = Player.PlayerHealth.Instance;
                ph?.TakeDamage(contactDamage);
                ph?.ApplyKnockback(transform.position);
            }
        }

        /// <summary>Assign a generated sprite to this enemy at runtime.</summary>
        public void ApplySprite(Sprite sprite)
        {
            if (_sr != null) _sr.sprite = sprite;
        }

        /// <summary>
        /// Move the enemy to <paramref name="worldPos"/> and reset its patrol anchor
        /// so it patrols around the new position. Called by <see cref="Level.LevelGenerator"/>.
        /// </summary>
        public void SetPatrolOrigin(Vector3 worldPos)
        {
            transform.position = worldPos;
            _startPos          = worldPos;
        }

        /// <summary>
        /// Apply gameplay stats from the current <see cref="LevelPlan"/>.
        /// Call this after <see cref="SetPatrolOrigin"/> so the patrol anchor is already set.
        /// </summary>
        public void ApplyStats(float speed, float patrolRange, int damage)
        {
            moveSpeed      = Mathf.Max(0.3f, speed);
            patrolDistance = Mathf.Max(1.0f, patrolRange);
            contactDamage  = Mathf.Max(1, damage);
        }
    }
}
