using UnityEngine;

namespace Gameplay
{
    /// <summary>
    /// A stationary enemy that periodically fires a <see cref="Projectile"/> toward
    /// the player when the player enters detection range.  Does not patrol.
    /// Also damages the player on direct contact.
    ///
    /// Projectiles are created at runtime — no prefab required.
    /// </summary>
    public sealed class ShootingEnemyController : MonoBehaviour
    {
        [SerializeField] private int   contactDamage    = 1;
        [SerializeField] private int   projectileDamage = 1;
        [SerializeField] private float fireInterval     = 1.3f;
        [SerializeField] private float projectileSpeed  = 5.5f;
        [SerializeField] private float detectionRange   = 13.5f;
        [SerializeField] private float stompTopTolerance = 0.35f;
        [SerializeField] private float contactDamageCooldown = 0.65f;

        private SpriteRenderer _sr;
        private float          _fireTimer;
        private Transform      _playerTransform;
        private bool           _dead;
        private Collider2D     _trigger;
        private Collider2D     _physicsCollider;
        private static Texture2D _cachedProjectileTexture;
        private static Sprite    _cachedProjectileSprite;

        // ── Animation
        private float _recoilTimer = 0f;
        private float _swayPhase   = 0f;
        private float _nextContactDamageAt;

        // ──────────────────────────────────────────────────────────────────────
        private void Awake()
        {
            _sr = GetComponentInChildren<SpriteRenderer>();

            // Ensure a trigger collider for direct contact detection.
            bool hasTrigger = false;
            foreach (Collider2D c in GetComponents<Collider2D>())
                if (c.isTrigger) { hasTrigger = true; _trigger = c; break; }

            if (!hasTrigger)
            {
                var col       = gameObject.AddComponent<CapsuleCollider2D>();
                col.isTrigger = true;
                col.size      = new Vector2(0.8f, 1.8f);
                _trigger      = col;
            }

            // Also ensure a solid collider for standing on the ground.
            bool hasSolid = false;
            foreach (Collider2D c in GetComponents<Collider2D>())
                if (!c.isTrigger) { hasSolid = true; if (_physicsCollider == null) _physicsCollider = c; break; }

            if (!hasSolid)
            {
                var col       = gameObject.AddComponent<CapsuleCollider2D>();
                col.isTrigger = false;
                col.size      = new Vector2(0.8f, 1.8f);
                _physicsCollider = col;
            }
        }

        private void Start()
        {
            var player = GameObject.FindWithTag("Player");
            if (player != null) _playerTransform = player.transform;
            // Fire shortly after player enters range so ranged enemies feel active.
            _fireTimer = Random.Range(0.2f, 0.7f);
        }

        private void Update()
        {
            if (_dead) return;
            if (_playerTransform == null) return;

            float dx      = _playerTransform.position.x - transform.position.x;
            int   faceDir = dx >= 0 ? 1 : -1;

            // Face player
            if (_sr != null)
            {
                Vector3 s = _sr.transform.localScale;
                s.x = faceDir > 0 ? Mathf.Abs(s.x) : -Mathf.Abs(s.x);
                _sr.transform.localScale = s;
            }

            // Shoot when player is in range
            if (Mathf.Abs(dx) <= detectionRange)
            {
                _fireTimer -= Time.deltaTime;
                if (_fireTimer <= 0f)
                {
                    _fireTimer = fireInterval;
                    SpawnProjectile(faceDir);
                }
            }

            _swayPhase += Time.deltaTime;
            ApplyShootingVisuals();
        }

        private void ApplyShootingVisuals()
        {
            if (_sr == null) return;

            float dt    = Time.deltaTime;
            float signX = Mathf.Sign(_sr.transform.localScale.x);
            if (signX == 0f) signX = 1f;

            // Idle sway: gentle rocking side to side
            float idleSway = 0.7f * Mathf.Sin(_swayPhase * 0.6f);

            if (_recoilTimer > 0f)
            {
                _recoilTimer -= dt;
                float t = Mathf.Clamp01(_recoilTimer / 0.10f);
                // Squash back on fire: compress toward camera (X wide, Y short)
                _sr.transform.localScale       = new Vector3(signX * Mathf.Lerp(1f, 1.08f, t), Mathf.Lerp(1f, 0.93f, t), 1f);
                _sr.transform.localEulerAngles = new Vector3(0f, 0f, idleSway);
            }
            else
            {
                _sr.transform.localScale       = new Vector3(signX, 1f, 1f);
                _sr.transform.localEulerAngles = new Vector3(0f, 0f, idleSway);
            }
        }

        private void OnTriggerEnter2D(Collider2D other)
        {
            if (_dead) return;
            if (!other.CompareTag("Player")) return;

            var playerRb = other.attachedRigidbody;
            var refCollider = _physicsCollider != null ? _physicsCollider : _trigger;
            var refBounds = refCollider != null ? refCollider.bounds : GetComponent<Collider2D>().bounds;

            bool movingDown = playerRb != null && playerRb.linearVelocity.y <= 0.15f;
            bool playerCenterAboveTop = other.bounds.center.y > refBounds.max.y - stompTopTolerance;
            bool playerFeetNearTop = other.bounds.min.y >= refBounds.max.y - stompTopTolerance;
            bool stomped = movingDown && (playerCenterAboveTop || playerFeetNearTop);

            if (stomped)
            {
                _dead = true;
                Game.GameSessionState.AddScore(100);
                UI.ScorePopup.Spawn(transform.position, 100);
                UI.ParticleBurst.Spawn(transform.position, _sr != null ? _sr.color : new Color(1f, 0.4f, 0.8f), 8);
                if (playerRb != null)
                    playerRb.linearVelocity = new Vector2(playerRb.linearVelocity.x, 11f);
                Destroy(gameObject);
                return;
            }

            // Near-top contact that wasn’t quite a clean stomp: boost player up to avoid unfair damage
            bool topContactButNotCleanStomp = playerCenterAboveTop || playerFeetNearTop;
            if (topContactButNotCleanStomp)
            {
                if (playerRb != null)
                    playerRb.linearVelocity = new Vector2(playerRb.linearVelocity.x, 8f);
                return;
            }

            // Side/bottom contact damage with cooldown
            if (Time.time < _nextContactDamageAt) return;
            _nextContactDamageAt = Time.time + contactDamageCooldown;

            var ph = Player.PlayerHealth.Instance;
            ph?.TakeDamage(contactDamage);
            ph?.ApplyKnockback(transform.position);
        }

        // ── Internal ──────────────────────────────────────────────────────────

        private void SpawnProjectile(int dir)
        {
            _recoilTimer = 0.10f;
            var go = new GameObject("Projectile");
            go.transform.position = transform.position + new Vector3(dir * 0.65f, 0f, 0f);
            var sprite = GetProjectileSprite();

            var sr           = go.AddComponent<SpriteRenderer>();
            sr.sprite        = sprite;
            sr.sortingOrder  = 3;

            var rb           = go.AddComponent<Rigidbody2D>();
            rb.gravityScale  = 0f;
            rb.freezeRotation = true;

            var col          = go.AddComponent<CircleCollider2D>();
            col.radius       = 0.20f;
            col.isTrigger    = true;

            var proj = go.AddComponent<Projectile>();
            proj.Init(dir, projectileDamage, projectileSpeed);
        }

        private static Sprite GetProjectileSprite()
        {
            Texture2D tex = Game.GameSessionState.CurrentProjectileSkin;

            if (tex == null)
            {
                // No skin for this run — clear any cached skin from a previous run.
                _cachedProjectileTexture = null;
                _cachedProjectileSprite  = null;
            }

            if (tex != null)
            {
                if (_cachedProjectileTexture == tex && _cachedProjectileSprite != null)
                    return _cachedProjectileSprite;

                _cachedProjectileTexture = tex;
                Texture2D orb = BuildOrbFromSourceTexture(tex, 64);
                _cachedProjectileSprite = Sprite.Create(
                    orb,
                    new Rect(0, 0, orb.width, orb.height),
                    new Vector2(0.5f, 0.5f),
                    orb.width);
                return _cachedProjectileSprite;
            }

            if (_cachedProjectileSprite != null)
                return _cachedProjectileSprite;

            int size = 48;
            var fallback = new Texture2D(size, size, TextureFormat.RGBA32, false);
            fallback.hideFlags = HideFlags.HideAndDontSave;
            float cx = (size - 1) * 0.5f;
            float rx = size * 0.28f;
            float ry = size * 0.16f;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x - cx;
                    float dy = y - cx;
                    float n = (dx * dx) / (rx * rx) + (dy * dy) / (ry * ry);
                    if (n <= 1f)
                    {
                        float glow = Mathf.Clamp01(1f - n);
                        Color c = Color.Lerp(new Color(1f, 0.55f, 0f, 1f), new Color(1f, 0.9f, 0.6f, 1f), glow);
                        fallback.SetPixel(x, y, c);
                    }
                    else
                    {
                        fallback.SetPixel(x, y, Color.clear);
                    }
                }
            }

            fallback.Apply();
            _cachedProjectileTexture = fallback;
            _cachedProjectileSprite = Sprite.Create(fallback, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
            return _cachedProjectileSprite;
        }

        private static Texture2D BuildOrbFromSourceTexture(Texture2D source, int size)
        {
            Color[] src = source.GetPixels();
            float sumR = 0f, sumG = 0f, sumB = 0f;
            int count = 0;
            for (int i = 0; i < src.Length; i++)
            {
                if (src[i].a <= 0.1f) continue;
                sumR += src[i].r;
                sumG += src[i].g;
                sumB += src[i].b;
                count++;
            }

            Color avg = count > 0
                ? new Color(sumR / count, sumG / count, sumB / count, 1f)
                : new Color(1f, 0.5f, 0.1f, 1f);
            Color core = Color.Lerp(avg, Color.white, 0.35f);
            Color edge = Color.Lerp(avg, Color.black, 0.20f);

            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.hideFlags = HideFlags.HideAndDontSave;

            float cx = (size - 1) * 0.5f;
            float rOuter = size * 0.46f;
            float rInner = size * 0.21f;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x - cx;
                    float dy = y - cx;
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);
                    if (dist > rOuter)
                    {
                        tex.SetPixel(x, y, Color.clear);
                        continue;
                    }

                    float t = Mathf.InverseLerp(rOuter, 0f, dist);
                    Color c = Color.Lerp(edge, core, t);
                    if (dist < rInner)
                        c = Color.Lerp(c, Color.white, 0.25f);
                    c.a = Mathf.SmoothStep(0f, 1f, t);
                    tex.SetPixel(x, y, c);
                }
            }

            tex.Apply();
            return tex;
        }

        // ── Public API ─────────────────────────────────────────────────────────

        /// <summary>Apply a generated sprite to this enemy's SpriteRenderer.</summary>
        public void ApplySprite(Sprite sprite)
        {
            if (_sr != null) _sr.sprite = sprite;
        }

        /// <summary>Reposition the enemy (does not affect patrol; shooting enemies are stationary).</summary>
        public void SetSpawnOrigin(Vector3 worldPos)
        {
            transform.position = worldPos;
        }

        /// <summary>Apply gameplay stats from the current level plan.</summary>
        public void ApplyStats(float speed, float range, int damage)
        {
            projectileSpeed  = Mathf.Max(2f, speed * 2.7f);
            detectionRange   = Mathf.Max(8f, range * 3.2f);
            fireInterval     = Mathf.Clamp(2.2f - speed * 0.35f, 0.75f, 2.0f);
            projectileDamage = Mathf.Max(1, damage);
            contactDamage    = Mathf.Max(1, damage);
        }
    }
}
