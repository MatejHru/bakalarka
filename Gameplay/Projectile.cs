using UnityEngine;

namespace Gameplay
{
    /// <summary>
    /// A simple horizontally-flying projectile fired by <see cref="ShootingEnemyController"/>.
    /// Created at runtime (no prefab required).
    /// Destroys itself on player contact, on collision with any solid collider, or after
    /// its lifetime expires.
    /// </summary>
    [RequireComponent(typeof(Rigidbody2D))]
    public sealed class Projectile : MonoBehaviour
    {
        private int   _damage   = 1;
        private float _speed    = 5f;
        private int   _dirX     = 1;
        private float _lifetime = 3f;

        private Rigidbody2D _rb;

        // ──────────────────────────────────────────────────────────────────────
        private void Awake()
        {
            _rb                          = GetComponent<Rigidbody2D>();
            _rb.gravityScale             = 0f;
            _rb.freezeRotation           = true;
            _rb.collisionDetectionMode   = CollisionDetectionMode2D.Continuous;
        }

        /// <summary>
        /// Initialise after adding the component.  Must be called before the
        /// projectile's first FixedUpdate.
        /// </summary>
        /// <param name="dirX">Horizontal direction: +1 = right, -1 = left.</param>
        /// <param name="damage">Damage dealt to the player on contact.</param>
        /// <param name="speed">Travel speed in Unity units per second.</param>
        /// <param name="lifetime">Seconds until auto-destroy.</param>
        public void Init(int dirX, int damage, float speed, float lifetime = 3f)
        {
            _dirX    = dirX;
            _damage  = damage;
            _speed   = speed;
            _lifetime = lifetime;
            Destroy(gameObject, _lifetime);
        }

        private void FixedUpdate()
        {
            _rb.linearVelocity = new Vector2(_dirX * _speed, 0f);
        }

        private void OnTriggerEnter2D(Collider2D other)
        {
            if (other.CompareTag("Player"))
            {
                Player.PlayerHealth.Instance?.TakeDamage(_damage);
                Destroy(gameObject);
            }
            else if (!other.isTrigger)
            {
                // Solid collider (wall, floor) — disappear
                Destroy(gameObject);
            }
        }
    }
}
