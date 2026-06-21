using TMPro;
using UnityEngine;

namespace UI
{
    /// <summary>
    /// Floating "+score" label that rises and fades out above a world position.
    /// Spawn via <see cref="ScorePopup.Spawn"/>.
    /// </summary>
    public sealed class ScorePopup : MonoBehaviour
    {
        private const float Duration  = 0.70f;
        private const float RiseSpeed = 1.8f;

        private TextMeshPro _tmp;
        private float       _elapsed;

        // Set by Spawn() immediately after AddComponent (before Start runs).
        internal string _scoreText  = "+0";
        internal Color  _startColor = Color.yellow;

        /// <summary>Spawn a score popup at the given world position.</summary>
        public static void Spawn(Vector3 worldPos, int score, Color? color = null)
        {
            var go    = new GameObject("ScorePopup");
            go.transform.position = worldPos + Vector3.up * 0.5f;
            var popup = go.AddComponent<ScorePopup>();
            popup._scoreText  = $"+{score}";
            popup._startColor = color ?? new Color(1f, 0.95f, 0.2f, 1f);
        }

        private void Start()
        {
            _tmp = gameObject.AddComponent<TextMeshPro>();
            _tmp.text              = _scoreText;
            _tmp.fontSize          = 3.6f;
            _tmp.fontStyle         = FontStyles.Bold;
            _tmp.alignment         = TextAlignmentOptions.Center;
            _tmp.color             = _startColor;
            _tmp.outlineWidth      = 0.14f;
            _tmp.outlineColor      = new Color32(0, 0, 0, 200);
            _tmp.sortingOrder      = 30;
            _tmp.textWrappingMode = TextWrappingModes.NoWrap;
        }

        private void Update()
        {
            _elapsed += Time.deltaTime;
            transform.position += Vector3.up * (RiseSpeed * Time.deltaTime);

            if (_tmp != null)
            {
                float t   = _elapsed / Duration;
                Color c   = _tmp.color;
                c.a = t < 0.45f ? 1f : Mathf.Lerp(1f, 0f, (t - 0.45f) / 0.55f);
                _tmp.color = c;
            }

            if (_elapsed >= Duration) Destroy(gameObject);
        }
    }
}
