using System;
using UnityEngine;

namespace Player
{
    /// <summary>
    /// Manages the player's health pool with brief invincibility frames
    /// after taking damage and events for the UI and death handling.
    /// </summary>
    public sealed class PlayerHealth : MonoBehaviour
    {
        public static PlayerHealth Instance { get; private set; }

        [SerializeField] private int maxLives = Game.GameSessionState.MaxLives;

        public int CurrentHealth { get; private set; }

        public event Action<int> HealthChanged;
        public event Action      PlayerDied;

        private float _invincibleTimer;
        private bool  _dead;
        private const float InvincibleDuration = 1.5f;

        // ──────────────────────────────────────────────────────────────────
        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            Game.GameSessionState.EnsureInitialized();
            CurrentHealth = Game.GameSessionState.Lives;
            HealthChanged?.Invoke(CurrentHealth);
        }

        private void Update()
        {
            if (_invincibleTimer > 0f) _invincibleTimer -= Time.deltaTime;
        }

        // ── Public API ─────────────────────────────────────────────────────
        public void TakeDamage(int amount, bool ignoreInvincibility = false)
        {
            if (_dead || amount <= 0) return;
            if (!ignoreInvincibility && _invincibleTimer > 0f) return;

            CurrentHealth    = Game.GameSessionState.LoseLives(amount);
            _invincibleTimer = InvincibleDuration;
            HealthChanged?.Invoke(CurrentHealth);
            if (CurrentHealth <= 0)
            {
                _dead = true;
                PlayerDied?.Invoke();
            }
        }

        public void Heal(int amount)
        {
            CurrentHealth = Game.GameSessionState.AddLives(amount);
            HealthChanged?.Invoke(CurrentHealth);
        }

        public void ResetHealth()
        {
            // Keep session lives on level reload; only clear invincibility.
            CurrentHealth    = Mathf.Clamp(Game.GameSessionState.Lives, 0, maxLives);
            _invincibleTimer = 0f;
            _dead            = CurrentHealth <= 0;
            HealthChanged?.Invoke(CurrentHealth);
        }

        public void SetInvincibleFor(float seconds)
        {
            _invincibleTimer = Mathf.Max(_invincibleTimer, Mathf.Max(0f, seconds));
        }

        /// <summary>
        /// Applies a small physics impulse away from <paramref name="attackerWorldPos"/>.
        /// Safe to call even when the player has no Rigidbody2D (does nothing).
        /// </summary>
        public void ApplyKnockback(Vector2 attackerWorldPos, float force = 3.5f)
        {
            var rb = GetComponent<Rigidbody2D>();
            if (rb == null || !rb.simulated) return;

            Vector2 dir = ((Vector2)transform.position - attackerWorldPos);
            if (dir.sqrMagnitude < 0.001f) dir = Vector2.right;
            dir.Normalize();
            // Always nudge upward a bit so knockback feels like a hit and not just a slide.
            dir.y = Mathf.Max(dir.y, 0.25f);
            rb.AddForce(dir * force, ForceMode2D.Impulse);
        }

        public bool IsInvincible => _invincibleTimer > 0f;
    }
}
