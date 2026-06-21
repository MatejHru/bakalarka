using UnityEngine;

namespace Player
{
    /// <summary>
    /// Platformer player movement: horizontal walk, variable-height jump,
    /// coyote time, and jump buffering.
    /// Requires Rigidbody2D (gravity enabled) and a GroundCheck child transform.
    /// </summary>
    [RequireComponent(typeof(Rigidbody2D))]
    public sealed class PlayerMovement2D : MonoBehaviour
    {
        [Header("Movement")]
        [SerializeField] private float moveSpeed  = 7f;
        [SerializeField] private float jumpForce  = 17f;

        [Header("Feel")]
        [SerializeField, Range(0f, 0.3f)] private float coyoteTime      = 0.12f;
        [SerializeField, Range(0f, 0.3f)] private float jumpBuffer       = 0.10f;
        [SerializeField, Range(1f, 5f)]   private float fallMultiplier   = 2.5f;
        [SerializeField, Range(1f, 5f)]   private float lowJumpMultiplier = 2.0f;

        [Header("Ground Check")]
        [SerializeField] private Transform groundCheck;
        [SerializeField] private float     groundRadius = 0.2f;
        [SerializeField] private LayerMask groundLayers;

        // runtime
        private Rigidbody2D _rb;
        private float _horizontal;
        private float _coyoteTimer;
        private float _jumpBufferTimer;

        public bool IsGrounded    { get; private set; }
        public bool IsFacingRight { get; private set; } = true;
        public bool IsMoving      => Mathf.Abs(_horizontal) > 0.01f;

        // ── Animation ──────────────────────────────────────────────────────
        private SpriteRenderer _sr;
        private PlayerHealth   _playerHealth;        private Transform      _visualTransform;
        private Vector3        _visualBaseScale = Vector3.one;        private float _facingSign       = 1f;
        private float _animScaleX       = 1f;
        private float _animScaleY       = 1f;
        private float _animTilt         = 0f;
        private bool  _prevGrounded     = true;
        private float _landTimer        = 0f;
        private float _jumpTimer        = 0f;
        private float _damageFlashTimer = 0f;
        private int   _prevHealth       = 99;

        private void Awake()
        {
            _rb = GetComponent<Rigidbody2D>();
            _rb.freezeRotation = true;

            // Ensure a non-trigger collider exists for ground physics.
            // If only trigger colliders exist, the player passes through tilemaps.
            bool hasSolidCollider = false;
            foreach (Collider2D c in GetComponents<Collider2D>())
                if (!c.isTrigger) { hasSolidCollider = true; break; }

            if (!hasSolidCollider)
            {
                var col       = gameObject.AddComponent<CapsuleCollider2D>();
                col.isTrigger = false;
                col.size      = new Vector2(0.5f, 0.9f);
                Debug.LogWarning("[PlayerMovement2D] No non-trigger Collider2D found — " +
                                 "added CapsuleCollider2D for ground physics. " +
                                 "Add a proper collider in the prefab to remove this warning.");
            }

            _sr = GetComponentInChildren<SpriteRenderer>();

            if (_sr != null)
            {
                _visualTransform = _sr.transform;
                _visualBaseScale = _visualTransform.localScale;
            }

            if (groundLayers.value == 0)
                Debug.LogWarning("[PlayerMovement2D] groundLayers is empty. " +
                                 "Assign at least one layer so ground detection works.");
        }

        private void Start()
        {
            _playerHealth = GetComponent<PlayerHealth>() ?? PlayerHealth.Instance;
            if (_playerHealth != null)
            {
                _prevHealth = _playerHealth.CurrentHealth;
                _playerHealth.HealthChanged += OnHealthChanged;
            }
        }

        private void OnDestroy()
        {
            if (_playerHealth != null)
                _playerHealth.HealthChanged -= OnHealthChanged;
        }

        private void OnHealthChanged(int newHealth)
        {
            if (newHealth < _prevHealth)
                _damageFlashTimer = 0.22f;
            _prevHealth = newHealth;
        }

        private void Update()
        {
            _horizontal = Input.GetAxisRaw("Horizontal");

            Vector2 cp = groundCheck != null
                ? (Vector2)groundCheck.position
                : (Vector2)transform.position + Vector2.down * 0.5f;
            IsGrounded = Physics2D.OverlapCircle(cp, groundRadius, groundLayers);

            if (IsGrounded) _coyoteTimer = coyoteTime;
            else            _coyoteTimer -= Time.deltaTime;

            if (Input.GetButtonDown("Jump")) _jumpBufferTimer = jumpBuffer;
            else                             _jumpBufferTimer -= Time.deltaTime;

            if (_jumpBufferTimer > 0f && _coyoteTimer > 0f)
            {
                _rb.linearVelocity = new Vector2(_rb.linearVelocity.x, jumpForce);
                _jumpBufferTimer = 0f;
                _coyoteTimer     = 0f;
            }

            if (Input.GetButtonUp("Jump") && _rb.linearVelocity.y > 0f)
                _rb.linearVelocity = new Vector2(_rb.linearVelocity.x, _rb.linearVelocity.y * 0.5f);

            if (_horizontal > 0f  && !IsFacingRight) Flip();
            else if (_horizontal < 0f && IsFacingRight) Flip();

            UpdateAnimations();
        }

        private void FixedUpdate()
        {
            if (_rb.linearVelocity.y < 0f)
                _rb.linearVelocity += Vector2.up * (Physics2D.gravity.y * (fallMultiplier - 1f) * Time.fixedDeltaTime);
            else if (_rb.linearVelocity.y > 0f && !Input.GetButton("Jump"))
                _rb.linearVelocity += Vector2.up * (Physics2D.gravity.y * (lowJumpMultiplier - 1f) * Time.fixedDeltaTime);

            _rb.linearVelocity = new Vector2(_horizontal * moveSpeed, _rb.linearVelocity.y);
        }

        private void Flip()
        {
            IsFacingRight = !IsFacingRight;
            _facingSign   = IsFacingRight ? 1f : -1f;
            // Full scale is written each frame in UpdateAnimations().
        }

        public void TriggerDamageFlash() => _damageFlashTimer = 0.22f;

        private void UpdateAnimations()
        {
            float dt = Time.deltaTime;

            bool justLanded = IsGrounded && !_prevGrounded;
            bool jumping    = !IsGrounded && _rb.linearVelocity.y >  0.5f;
            bool falling    = !IsGrounded && _rb.linearVelocity.y < -0.5f;

            // ── Coyote edge walk-off pulse ──────────────────────────────────
            // Detect walking off a ledge (not a jump — velocity still near zero).
            bool walkedOffEdge = !IsGrounded && _prevGrounded && _rb.linearVelocity.y <= 0.1f;
            if (walkedOffEdge)
            {
                _animScaleX = 1.06f;
                _animScaleY = 0.94f;
            }

            // ── Takeoff squash ──────────────────────────────────────────────
            if (!IsGrounded && _prevGrounded && _rb.linearVelocity.y > 0.5f)
            {
                _animScaleX = 1.06f;
                _animScaleY = 0.95f;
                _jumpTimer  = 0.045f;
            }

            // ── Landing squash ──────────────────────────────────────────
            if (justLanded)
            {
                _animScaleX = 1.07f;
                _animScaleY = 0.94f;
                _landTimer  = 0.08f;
            }

            // ── Air shapes ─────────────────────────────────────────────────
            if (_jumpTimer > 0f)
            {
                _jumpTimer -= dt;           // brief hold on takeoff squash
            }
            else if (jumping)
            {
                _animScaleX = Mathf.Lerp(_animScaleX, 0.94f, dt * 12f);
                _animScaleY = Mathf.Lerp(_animScaleY, 1.07f, dt * 12f);
            }
            else if (falling)
            {
                _animScaleX = Mathf.Lerp(_animScaleX, 0.96f, dt * 9f);
                _animScaleY = Mathf.Lerp(_animScaleY, 1.05f, dt * 9f);
            }

            // ── Ground shapes ──────────────────────────────────────────────
            if (_landTimer > 0f)
            {
                _landTimer -= dt;                               // hold land squash briefly
            }
            else if (IsGrounded)
            {
                // Subtle walk bob while moving; lerp back to 1 when idle.
                float bobY = IsMoving ? 1f + 0.006f * Mathf.Sin(Time.time * 11f) : 1f;
                _animScaleX = Mathf.Lerp(_animScaleX, 1f,    dt * 18f);
                _animScaleY = Mathf.Lerp(_animScaleY, bobY,  dt * 18f);
            }

            // ── Run tilt ───────────────────────────────────────────────────
            float targetTilt = IsGrounded && IsMoving ? _horizontal * (-1.2f) : 0f;
            _animTilt = Mathf.Lerp(_animTilt, targetTilt, dt * 12f);

            _prevGrounded = IsGrounded;

            // ── Damage flash / invincibility blink ─────────────────────────
            if (_sr != null)
            {
                if (_damageFlashTimer > 0f)
                {
                    _damageFlashTimer -= dt;
                    float t = Mathf.Clamp01(_damageFlashTimer / 0.22f);
                    _sr.color = Color.Lerp(Color.white, new Color(1f, 0.72f, 0.72f, 1f), t * t);
                }
                else if (_playerHealth != null && _playerHealth.IsInvincible)
                {
                    // Fast blink during iframe window
                    _sr.color = Mathf.Sin(Time.time * 14f) > 0f
                        ? Color.white
                        : new Color(1f, 1f, 1f, 0.55f);
                }
                else
                {
                    _sr.color = Color.white;
                }

                // Visual tilt on sprite (pure visual — doesn't rotate the physics body)
                _sr.transform.localEulerAngles = new Vector3(0f, 0f, _animTilt);
            }

            // ── Apply scale to visual sprite child only (flip + squash/stretch) ─────────
            if (_visualTransform != null)
            {
                _visualTransform.localScale = new Vector3(
                    Mathf.Abs(_visualBaseScale.x) * _facingSign * _animScaleX,
                    Mathf.Abs(_visualBaseScale.y) * _animScaleY,
                    _visualBaseScale.z);
            }
        }

        private void OnDrawGizmosSelected()
        {
            if (groundCheck == null) return;
            Gizmos.color = IsGrounded ? Color.green : Color.red;
            Gizmos.DrawWireSphere(groundCheck.position, groundRadius);
        }
    }
}
