using TMPro;
using UnityEngine;

namespace Gameplay
{
    /// <summary>
    /// Boss variant used on level 5. Supports multi-stomp HP, boss name label,
    /// and notifies UI/GameController through static events.
    /// </summary>
    [RequireComponent(typeof(Rigidbody2D))]
    public sealed class BossController : MonoBehaviour
    {
        public static event System.Action<string, int, int> BossSpawned;
        public static event System.Action<int, int> BossHealthChanged;
        public static event System.Action BossDefeated;

        [SerializeField] private int maxHp = 5;
        [SerializeField] private float moveSpeed = 3.4f;
        [SerializeField] private float patrolDistance = 8f;
        [SerializeField] private int contactDamage = 2;
        [SerializeField] private float stompBounceVelocity = 12f;
        [SerializeField] private int defeatScore = 1500;
        [SerializeField] private LayerMask groundMask = ~0;
        [SerializeField] private float jumpVelocity = 12.8f;
        [SerializeField] private float jumpCooldown = 0.55f;
        [SerializeField] private float chaseRange = 28f;
        [SerializeField] private float turnDeadZone = 0.75f;
        [SerializeField] private float minTurnInterval = 0.22f;
        [SerializeField] private float stompDamageCooldown = 0.55f;
        [SerializeField] private float postHitInvulnerability = 0.70f;
        [SerializeField] private float postHitLeapVelocity = 13.5f;
        [SerializeField] private float postHitDashVelocity = 7.5f;
        [SerializeField] private float rageSpeedMultiplier = 1.35f;
        [SerializeField] private float lowHpThreshold = 0.45f;
        [SerializeField] private float contactDamageCooldown = 0.75f;

        public int CurrentHp => _hp;
        public int MaxHp => Mathf.Max(1, maxHp);

        private Rigidbody2D _rb;
        private SpriteRenderer _sr;
        private Collider2D _trigger;
        private Collider2D _physicsCollider;
        private Vector3 _startPos;
        private int _dir = 1;
        private bool _dead;
        private int _hp;
        private TextMeshPro _nameLabel;
        private float _nextJumpAt;

        // Stomp damage cooldown — prevents multi-HP loss from a single landing (OnTriggerStay)
        private float _nextStompDamageAt;
        private float _invulnerableUntil;
        private float _nextContactDamageAt;
        private float _nextTurnAllowedAt;
        private float _baseMoveSpeed;
        private float _baseJumpCooldown;

        // ── Animation
        private float _hitFlashTimer = 0f;
        private float _breathPhase   = 0f;

        private void Awake()
        {
            _rb = GetComponent<Rigidbody2D>();
            _rb.freezeRotation = true;

            _sr = GetComponentInChildren<SpriteRenderer>();
            _startPos = transform.position;
            _hp = Mathf.Max(1, maxHp);
            _baseMoveSpeed = moveSpeed;
            _baseJumpCooldown = jumpCooldown;

            foreach (Collider2D c in GetComponents<Collider2D>())
            {
                if (c == null) continue;
                if (c.isTrigger && _trigger == null) _trigger = c;
                if (!c.isTrigger && _physicsCollider == null) _physicsCollider = c;
            }

            if (_trigger == null)
            {
                var col = gameObject.AddComponent<CapsuleCollider2D>();
                col.isTrigger = true;
                col.size = new Vector2(1.2f, 2.1f);
                _trigger = col;
            }

            if (_physicsCollider == null)
            {
                var col = gameObject.AddComponent<CapsuleCollider2D>();
                col.isTrigger = false;
                col.size = new Vector2(1.2f, 2.1f);
                _physicsCollider = col;
            }

            EnsureNameLabel();
        }

        private void OnEnable()
        {
            _dead = false;
            _hp = Mathf.Max(1, maxHp);
            _startPos = transform.position;

            string bossName = Game.GameSessionState.CurrentLore != null &&
                              !string.IsNullOrWhiteSpace(Game.GameSessionState.CurrentLore.bossName)
                ? Game.GameSessionState.CurrentLore.bossName
                : "Boss";

            if (_nameLabel != null)
                _nameLabel.text = bossName;

            BossSpawned?.Invoke(bossName, _hp, _hp);
            BossHealthChanged?.Invoke(_hp, _hp);
        }

        private void Update()
        {
            if (_dead || _sr == null) return;

            _breathPhase += Time.deltaTime;
            float dt = Time.deltaTime;

            // Breathing: subtle scale sin wave
            float breatheY = 1f + 0.018f * Mathf.Sin(_breathPhase * 0.8f);
            float breatheX = 1f - 0.012f * Mathf.Sin(_breathPhase * 0.8f);
            _sr.transform.localScale = new Vector3(breatheX, breatheY, 1f);

            // Hit flash (red tint when stomped)
            if (_hitFlashTimer > 0f)
            {
                _hitFlashTimer -= dt;
                float t = Mathf.Clamp01(_hitFlashTimer / 0.14f);
                _sr.color = Color.Lerp(Color.white, new Color(1f, 0.75f, 0.75f, 1f), t);
            }
            else
            {
                _sr.color = Color.white;
            }
        }

        private void FixedUpdate()
        {
            if (_dead) return;

            Transform player = FindPlayer();
            if (player != null)
            {
                float dx = player.position.x - transform.position.x;
                if (Mathf.Abs(dx) <= chaseRange)
                {
                    if (Mathf.Abs(dx) > turnDeadZone)
                        TrySetDirection(dx >= 0f ? 1 : -1);
                    // else keep current direction — player is almost directly above/below
                }
                else
                {
                    PatrolAroundOrigin();
                }
            }
            else
            {
                PatrolAroundOrigin();
            }

            bool wallAhead = HasWallAhead(_dir);
            bool groundAhead = HasGroundAhead(_dir);

            if (wallAhead || !groundAhead)
            {
                if (CanJump())
                    TryJump();
                else
                    TrySetDirection(-_dir, force: true);
            }

            // If player is above us, attempt a jump to reach upper platforms.
            Transform player2 = FindPlayer();
            if (player2 != null && player2.position.y > transform.position.y + 1.4f && CanJump())
                TryJump();

            if (!groundAhead && !CanJump())
                TrySetDirection(-_dir, force: true);

            // Low HP rage phase
            float hpRatio = (float)_hp / Mathf.Max(1, maxHp);
            float phaseSpeed = hpRatio <= lowHpThreshold ? _baseMoveSpeed * rageSpeedMultiplier : _baseMoveSpeed;
            moveSpeed = Mathf.Max(moveSpeed, phaseSpeed);
            if (hpRatio <= lowHpThreshold)
                jumpCooldown = Mathf.Min(jumpCooldown, _baseJumpCooldown * 0.75f);

            _rb.linearVelocity = new Vector2(_dir * moveSpeed, _rb.linearVelocity.y);

            if (_sr != null)
            {
                _sr.flipX = _dir < 0;
            }

            if (_nameLabel != null)
                _nameLabel.transform.localPosition = new Vector3(0f, 1.85f, 0f);
        }

        private void OnTriggerEnter2D(Collider2D other)
        {
            ResolvePlayerContact(other);
        }

        private void OnTriggerStay2D(Collider2D other)
        {
            ResolvePlayerContact(other);
        }

        private void OnCollisionEnter2D(Collision2D collision)
        {
            if (collision != null)
                ResolvePlayerContact(collision.collider);
        }

        private void OnCollisionStay2D(Collision2D collision)
        {
            if (collision != null)
                ResolvePlayerContact(collision.collider);
        }

        private void ResolvePlayerContact(Collider2D other)
        {
            if (_dead) return;
            if (!other.CompareTag("Player")) return;

            var playerRb = other.attachedRigidbody;
            var refCollider = _physicsCollider != null ? _physicsCollider : _trigger;
            var b = refCollider != null ? refCollider.bounds : GetComponent<Collider2D>().bounds;

            bool movingDown = playerRb != null && playerRb.linearVelocity.y <= -0.01f;
            bool aboveBoss = other.bounds.min.y >= b.max.y - 0.1f;
            bool strongTop = other.transform.position.y > b.center.y + 0.25f;
            bool stomped = aboveBoss && (movingDown || strongTop);

            if (stomped)
            {
                // Invulnerability window — bounce player but don't count damage
                if (Time.time < _nextStompDamageAt || Time.time < _invulnerableUntil)
                {
                    if (playerRb != null)
                        playerRb.linearVelocity = new Vector2(playerRb.linearVelocity.x, stompBounceVelocity * 0.65f);
                    return;
                }

                _nextStompDamageAt = Time.time + stompDamageCooldown;
                _invulnerableUntil = Time.time + postHitInvulnerability;

                _hp = Mathf.Max(0, _hp - 1);
                _hitFlashTimer = 0.22f;
                UI.ParticleBurst.Spawn(transform.position + Vector3.up * 0.5f,
                    new Color(1f, 0.3f, 0.3f), 6, 2.5f);
                BossHealthChanged?.Invoke(_hp, Mathf.Max(1, maxHp));

                if (playerRb != null)
                    playerRb.linearVelocity = new Vector2(playerRb.linearVelocity.x, stompBounceVelocity);

                if (_hp <= 0)
                {
                    _dead = true;
                    Game.GameSessionState.AddScore(defeatScore);
                    UI.ScorePopup.Spawn(transform.position + Vector3.up,
                        defeatScore, new Color(1f, 0.85f, 0.1f));
                    UI.ParticleBurst.Spawn(transform.position, new Color(1f, 0.55f, 0.1f), 18, 5f);
                    BossDefeated?.Invoke();
                    Destroy(gameObject);
                    return;
                }

                // Leap/dash away from player after surviving a stomp
                int awayFromPlayer = playerRb != null && playerRb.transform.position.x < transform.position.x ? 1 : -1;
                _dir = awayFromPlayer;
                _rb.linearVelocity = new Vector2(_dir * postHitDashVelocity, postHitLeapVelocity);
                return;
            }

            // Side/bottom contact damage with cooldown
            if (Time.time < _nextContactDamageAt) return;
            _nextContactDamageAt = Time.time + contactDamageCooldown;

            var phInst = Player.PlayerHealth.Instance;
            phInst?.TakeDamage(contactDamage);
            phInst?.ApplyKnockback(transform.position, 4.5f);
        }

        private Transform FindPlayer()
        {
            var ph = Player.PlayerHealth.Instance;
            if (ph != null) return ph.transform;
            var go = GameObject.FindWithTag("Player");
            return go != null ? go.transform : null;
        }

        private void PatrolAroundOrigin()
        {
            float dist = transform.position.x - _startPos.x;
            if (dist > patrolDistance) TrySetDirection(-1, force: true);
            if (dist < -patrolDistance) TrySetDirection(1, force: true);
        }

        private void TrySetDirection(int newDir, bool force = false)
        {
            newDir = newDir >= 0 ? 1 : -1;

            if (!force && newDir == _dir) return;
            if (!force && Time.time < _nextTurnAllowedAt) return;

            _dir = newDir;
            _nextTurnAllowedAt = Time.time + minTurnInterval;
        }

        private bool CanJump()
        {
            return Time.time >= _nextJumpAt && IsGrounded();
        }

        private void TryJump()
        {
            _nextJumpAt = Time.time + jumpCooldown;
            _rb.linearVelocity = new Vector2(_rb.linearVelocity.x, jumpVelocity);
        }

        private bool IsGrounded()
        {
            if (_physicsCollider == null) return false;
            Bounds b = _physicsCollider.bounds;
            Vector2 origin = new Vector2(b.center.x, b.min.y + 0.05f);
            RaycastHit2D hit = Physics2D.Raycast(origin, Vector2.down, 0.35f, groundMask);
            if (!hit.collider) return false;
            if (hit.collider.transform == transform || hit.collider.transform.IsChildOf(transform)) return false;
            return true;
        }

        public void Configure(string bossName, int hp, float speed, float patrol, int damage)
        {
            maxHp = Mathf.Max(1, hp);
            _hp = maxHp;
            moveSpeed = Mathf.Max(1f, speed);
            patrolDistance = Mathf.Max(2f, patrol);
            contactDamage = Mathf.Max(1, damage);

            if (_nameLabel != null && !string.IsNullOrWhiteSpace(bossName))
                _nameLabel.text = bossName;

            BossSpawned?.Invoke(string.IsNullOrWhiteSpace(bossName) ? "Boss" : bossName, _hp, _hp);
            BossHealthChanged?.Invoke(_hp, _hp);
        }

        public void SetPatrolOrigin(Vector3 worldPos)
        {
            transform.position = worldPos;
            _startPos = worldPos;
        }

        private void EnsureNameLabel()
        {
            if (_nameLabel != null) return;

            Transform existing = transform.Find("BossNameLabel");
            if (existing != null)
            {
                _nameLabel = existing.GetComponent<TextMeshPro>();
                if (_nameLabel != null) return;
            }

            var go = new GameObject("BossNameLabel", typeof(TextMeshPro));
            go.transform.SetParent(transform, false);
            go.transform.localPosition = new Vector3(0f, 1.85f, 0f);

            _nameLabel = go.GetComponent<TextMeshPro>();
            _nameLabel.alignment = TextAlignmentOptions.Center;
            _nameLabel.fontSize = 4.2f;
            _nameLabel.color = Color.white;
            _nameLabel.outlineWidth = 0.2f;
            _nameLabel.outlineColor = new Color(0f, 0f, 0f, 0.8f);
            _nameLabel.sortingOrder = 20;
            _nameLabel.text = "Boss";
        }

        private bool HasGroundAhead(int dir)
        {
            if (_physicsCollider == null) return true;
            Bounds b = _physicsCollider.bounds;
            Vector2 origin = new Vector2(b.center.x + dir * b.extents.x * 0.95f, b.min.y + 0.05f);
            RaycastHit2D hit = Physics2D.Raycast(origin, Vector2.down, 1.1f, groundMask);
            if (!hit.collider) return false;
            if (hit.collider.transform == transform || hit.collider.transform.IsChildOf(transform)) return false;
            return true;
        }

        private bool HasWallAhead(int dir)
        {
            if (_physicsCollider == null) return false;
            Bounds b = _physicsCollider.bounds;
            Vector2 origin = new Vector2(b.center.x, b.center.y + 0.1f);
            RaycastHit2D hit = Physics2D.Raycast(origin, Vector2.right * dir, 0.8f, groundMask);
            if (!hit.collider) return false;
            if (hit.collider.transform == transform || hit.collider.transform.IsChildOf(transform)) return false;
            return true;
        }
    }
}
