using UnityEngine;

namespace UI
{
    /// <summary>
    /// Spawns a quick burst of colored dot sprites at a world position.
    /// No prefab required — everything is created at runtime.
    /// Call <see cref="ParticleBurst.Spawn"/> from any context.
    /// </summary>
    public static class ParticleBurst
    {
        private static Texture2D _sharedDotTex;
        private static Sprite    _sharedDotSprite;

        /// <summary>Emit <paramref name="count"/> dot particles from <paramref name="worldPos"/>.</summary>
        public static void Spawn(Vector3 worldPos, Color color, int count = 8, float speed = 3f)
        {
            for (int i = 0; i < count; i++)
            {
                float angle = i * (360f / count) + Random.Range(-22f, 22f);
                float spd   = Random.Range(speed * 0.55f, speed * 1.45f);
                Vector2 dir = new Vector2(
                    Mathf.Cos(angle * Mathf.Deg2Rad),
                    Mathf.Sin(angle * Mathf.Deg2Rad));

                var go = new GameObject("Dot");
                go.transform.position = worldPos;
                go.AddComponent<ParticleDot>().Init(dir * spd, color);
            }
        }

        // ── Shared texture + sprite ─────────────────────────────────────────────────────
        internal static Texture2D GetDotTexture()
        {
            if (_sharedDotTex != null) return _sharedDotTex;

            const int size = 6;
            _sharedDotTex = new Texture2D(size, size, TextureFormat.RGBA32, false)
                { hideFlags = HideFlags.HideAndDontSave };
            float cx = (size - 1) * 0.5f, r = size * 0.42f;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = x - cx, dy = y - cx;
                _sharedDotTex.SetPixel(x, y,
                    Mathf.Sqrt(dx * dx + dy * dy) <= r ? Color.white : Color.clear);
            }
            _sharedDotTex.Apply();
            return _sharedDotTex;
        }

        internal static Sprite GetDotSprite()
        {
            if (_sharedDotSprite != null) return _sharedDotSprite;

            var tex = GetDotTexture();
            _sharedDotSprite = Sprite.Create(
                tex,
                new Rect(0, 0, tex.width, tex.height),
                new Vector2(0.5f, 0.5f),
                tex.width);
            _sharedDotSprite.hideFlags = HideFlags.HideAndDontSave;
            return _sharedDotSprite;
        }
    }

    /// <summary>
    /// Single animated dot particle. Moves outward, decelerates and fades.
    /// Created and owned by <see cref="ParticleBurst"/>.
    /// </summary>
    internal sealed class ParticleDot : MonoBehaviour
    {
        private const float Duration = 0.38f;

        private SpriteRenderer _sr;
        private Vector2        _velocity;
        private float          _elapsed;

        public void Init(Vector2 velocity, Color color)
        {
            _velocity = velocity;

            _sr = gameObject.AddComponent<SpriteRenderer>();
            _sr.sprite       = ParticleBurst.GetDotSprite();
            _sr.color        = color;
            _sr.sortingOrder = 28;
            transform.localScale = Vector3.one * 0.28f;
        }

        private void Update()
        {
            _elapsed += Time.deltaTime;
            _velocity = Vector2.Lerp(_velocity, Vector2.zero, Time.deltaTime * 5f);
            transform.position += (Vector3)_velocity * Time.deltaTime;

            if (_sr != null)
            {
                Color c = _sr.color;
                c.a = Mathf.Lerp(1f, 0f, _elapsed / Duration);
                _sr.color = c;
            }

            if (_elapsed >= Duration) Destroy(gameObject);
        }
    }
}
