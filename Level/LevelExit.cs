using UnityEngine;
using UnityEngine.Events;

namespace Level
{
    /// <summary>
    /// Marks the end of a level.  Fires PlayerReachedExit (static event listened
    /// to by GameController) and an optional per-instance UnityEvent for local FX.
    /// Place on a trigger collider tagged appropriately.
    /// </summary>
    public sealed class LevelExit : MonoBehaviour
    {
        /// <summary>Static event — GameController subscribes to this.</summary>
        public static event System.Action PlayerReachedExit;

        [SerializeField] private UnityEvent onPlayerEntered;

        private bool _triggered;

        private void OnTriggerEnter2D(Collider2D other)
        {
            if (_triggered) return;
            if (!other.CompareTag("Player")) return;

            _triggered = true;
            onPlayerEntered.Invoke();
            PlayerReachedExit?.Invoke();
        }

        /// <summary>Call when the level resets / player respawns.</summary>
        public void ResetTrigger() => _triggered = false;
    }
}
