using UnityEngine;

namespace Level
{
    /// <summary>
    /// Attaches to the Background GameObject that has a SpriteRenderer.
    ///
    /// Two responsibilities:
    ///  1. Scale the sprite so it always covers the full camera viewport
    ///     (even after the texture is swapped by LevelAssembler).
    ///  2. Move at a fraction of the camera's horizontal movement so it
    ///     appears to be far away (parallax effect).
    ///
    /// parallaxFactor: 0 = stationary, 1 = moves with camera (no parallax).
    ///   Use 0.85–0.95 for a distant background — gives slow drift while always
    ///   keeping the image in frame. Values below 0.7 require a very wide image.
    ///
    /// maxLevelWidth: approximate world-unit width of the level.
    ///   Used to pre-scale the background wide enough to avoid showing edges
    ///   as the camera scrolls.
    /// </summary>
    [RequireComponent(typeof(SpriteRenderer))]
    public class ParallaxBackground : MonoBehaviour
    {
        [Header("Parallax")]
        [Range(0f, 1f)]
        [Tooltip("0 = fixed, 1 = moves with camera. 0.9 gives subtle slow drift.")]
        public float parallaxFactor = 0.9f;

        [Range(0f, 1f)]
        [Tooltip("Vertical parallax fraction. 0 = no vertical movement (recommended).")]
        public float verticalParallax = 0f;

        [Tooltip("Expected maximum camera X travel in world units. Used to pre-scale background wide enough.")]
        public float maxLevelWidth = 220f;

        [Header("Refs")]
        public Camera targetCamera;

        // ── Private state ─────────────────────────────────────────────────────
        private SpriteRenderer _sr;
        private float _camStartX;
        private float _bgStartX;

        // ── Unity lifecycle ───────────────────────────────────────────────────

        private void Awake()
        {
            _sr = GetComponent<SpriteRenderer>();
            if (targetCamera == null) targetCamera = Camera.main;
            if (targetCamera == null)
                Debug.LogError("[ParallaxBackground] No camera found — tag your camera 'MainCamera'.");

            // Force-clamp any old scene values that would cause edge-reveal or no-move bugs
            parallaxFactor  = Mathf.Clamp(parallaxFactor, 0.8f, 1f);
            lockToCameraX   = false;
        }

        // Kept for Inspector serialization compatibility with old scenes.
        [HideInInspector] public bool lockToCameraX = false;

        private void Start()
        {
            if (targetCamera == null) return;
            _camStartX = targetCamera.transform.position.x;
            _bgStartX  = transform.position.x;
            FitToScreen();
        }

        private void LateUpdate()
        {
            if (targetCamera == null) return;

            // Standard parallax: background moves at parallaxFactor of camera speed.
            // Background X = camera X × parallaxFactor + (1-parallaxFactor) × startX
            float camX = targetCamera.transform.position.x;
            float bgX  = _bgStartX + (camX - _camStartX) * parallaxFactor;

            // Always follow camera Y exactly so top/bottom edges never become visible.
            // FitToScreen already scales height to 110% of the viewport, so this is safe.
            float camY = targetCamera.transform.position.y;

            transform.position = new Vector3(
                bgX,
                camY,
                transform.position.z);
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Scales the sprite so it covers the full camera view plus extra horizontal
        /// margin to avoid showing edges while the camera scrolls the full level.
        /// </summary>
        public void FitToScreen()
        {
            if (targetCamera == null || _sr == null || _sr.sprite == null) return;

            float camH = targetCamera.orthographicSize * 2f;
            float camW = camH * targetCamera.aspect;

            Vector2 spriteSize = _sr.sprite.bounds.size;
            if (spriteSize.x <= 0f || spriteSize.y <= 0f) return;

            // Scale to fill height with a small margin
            float scaleY = (camH / spriteSize.y) * 1.1f;

            // Horizontal: must cover camera width + full parallax drift over the level.
            // At factor f, background drifts (1-f)*maxLevelWidth behind camera at level end.
            float drift        = maxLevelWidth * (1f - parallaxFactor);
            float neededWidth  = camW + drift * 2f + 2f;   // symmetric margin, +2 safety
            float scaleXFill   = neededWidth / spriteSize.x;
            float scaleX       = Mathf.Max(scaleY, scaleXFill);

            transform.localScale = new Vector3(scaleX, scaleY, 1f);

            // Reset tracking so LateUpdate calculates from current camera position
            if (targetCamera != null)
            {
                _camStartX = targetCamera.transform.position.x;
                _bgStartX  = targetCamera.transform.position.x; // start centred on camera
                transform.position = new Vector3(_bgStartX, transform.position.y, transform.position.z);
            }
        }
    }
}
