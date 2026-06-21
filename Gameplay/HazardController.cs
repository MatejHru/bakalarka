using UnityEngine;

namespace Gameplay
{
    /// <summary>
    /// Static hazard: damages the player on contact.
    /// Attach to spike / lava tiles or stand-alone hazard GameObjects.
    /// </summary>
    public sealed class HazardController : MonoBehaviour
    {
        [SerializeField] private int   damage            = 1;
        [SerializeField] private float knockbackForce    = 8f;

        private void OnTriggerEnter2D(Collider2D other)
        {
            if (!other.CompareTag("Player")) return;

            Player.PlayerHealth.Instance?.TakeDamage(damage);

            // Small knockback away from hazard
            if (knockbackForce > 0f)
            {
                var rb = other.GetComponent<Rigidbody2D>();
                if (rb != null)
                {
                    Vector2 dir = (other.transform.position - transform.position).normalized;
                    rb.linearVelocity = dir * knockbackForce + Vector2.up * knockbackForce * 0.5f;
                }
            }
        }
    }
}
