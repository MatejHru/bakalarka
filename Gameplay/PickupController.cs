using UnityEngine;

namespace Gameplay
{
    /// <summary>
    /// Collectible pickup.  Plays a simple animation, heals the player
    /// (optional), and awards score on collection.
    /// </summary>
    public sealed class PickupController : MonoBehaviour
    {
        [SerializeField] private int   healAmount    = 0;   // 0 = score-only pickup
        [SerializeField] private int   scoreValue    = 50;  // score awarded on collect
        [SerializeField] private float bobAmplitude  = 0.15f;
        [SerializeField] private float bobSpeed      = 2f;

        private Vector3 _startPos;
        private bool    _positionCaptured;
        private bool    _collected;

        private void Update()
        {
            // Capture position on first frame (after LevelGenerator.PlaceObjects has run)
            if (!_positionCaptured)
            {
                _startPos         = transform.position;
                _positionCaptured = true;
            }

            // Gentle bob animation
            float y = _startPos.y + Mathf.Sin(Time.time * bobSpeed) * bobAmplitude;
            transform.position = new Vector3(_startPos.x, y, _startPos.z);
        }

        private void OnTriggerEnter2D(Collider2D other)
        {
            if (_collected) return;
            if (!other.CompareTag("Player")) return;

            _collected = true;

            if (healAmount > 0)
                Player.PlayerHealth.Instance?.Heal(healAmount);

            if (scoreValue > 0)
            {
                Game.GameSessionState.AddScore(scoreValue);
                UI.ScorePopup.Spawn(transform.position, scoreValue, new Color(0.35f, 1f, 0.45f));
            }

            var sr = GetComponentInChildren<SpriteRenderer>();
            UI.ParticleBurst.Spawn(transform.position,
                sr != null ? sr.color : new Color(0.35f, 1f, 0.45f), 10, 2.5f);

            Destroy(gameObject);
        }

        /// <summary>
        /// Apply gameplay stats from the current <see cref="LevelPlan"/>.
        /// Call this once after the level plan is loaded (e.g. from GameController).
        /// </summary>
        public void ApplyStats(int heal, int score)
        {
            healAmount = Mathf.Max(0, heal);
            scoreValue = Mathf.Max(0, score);
        }
    }
}
