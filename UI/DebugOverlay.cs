using UnityEngine;
using TMPro;

namespace UI
{
    /// <summary>
    /// Simple debug overlay toggled with F1.
    /// Shows: pipeline state, SD URL, theme, last error.
    ///
    /// Add this component anywhere in the Game scene.
    /// The panel is toggled with F1 — no complex wiring needed.
    /// </summary>
    public class DebugOverlay : MonoBehaviour
    {
        [Header("Panel")]
        [SerializeField] private GameObject panel;
        [SerializeField] private TMP_Text   infoText;
        [SerializeField] private bool       showOnStart = false;

        private AiLevelPipeline _pipeline;
        private StableDiffusion _sd;
        private RembgClient     _rembg;

        private void Awake()
        {
            _pipeline = FindFirstObjectByType<AiLevelPipeline>();
            _sd       = FindFirstObjectByType<StableDiffusion>();
            _rembg    = FindFirstObjectByType<RembgClient>();

            if (panel != null) panel.SetActive(showOnStart);
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F1))
                TogglePanel();

            if (panel != null && panel.activeSelf)
                Refresh();
        }

        private void Refresh()
        {
            if (infoText == null) return;

            string theme      = _pipeline != null ? _pipeline.theme      : "?";
            string checkpoint = _pipeline != null ? _pipeline.checkpoint : "?";
            string sdUrl      = _sd       != null ? _sd.baseUrl          : "?";
            bool   generating = AiLevelPipeline.IsGenerating;

            bool   hasPending   = Game.GameSessionState.PendingLevelBundle != null;
            int    levelIndex   = Game.GameSessionState.CurrentLevelIndex;
            string difficulty   = Game.GameSessionState.CurrentDifficulty.ToString();
            int    runSeed      = Game.GameSessionState.RunSeed;
            int    score        = Game.GameSessionState.Score;
            int    lives        = Game.GameSessionState.Lives;

            infoText.text =
                $"[F1] Debug Overlay\n" +
                $"Theme:       {theme}\n" +
                $"Checkpoint:  {checkpoint}\n" +
                $"SD URL:      {sdUrl}\n" +
                $"Generating:  {generating}\n" +
                $"Rembg:       {(_rembg != null ? (_rembg.IsEnabled ? "enabled @ " + _rembg.BaseUrl : "disabled") : "not in scene")}\n" +
                $"Level:       {levelIndex}/{Game.GameSessionState.MaxLevelIndex}\n" +
                $"Difficulty:  {difficulty}\n" +
                $"Seed:        {runSeed}\n" +
                $"Score:       {score}\n" +
                $"Lives:       {lives}\n" +
                $"PendingBundle:{hasPending}\n" +
                $"\nTip: Enable 'enableDebugLogs' on AiLevelPipeline\n" +
                $"     for per-step Console output.";
        }

        private void TogglePanel()
        {
            if (panel != null) panel.SetActive(!panel.activeSelf);
        }
    }
}
