using TMPro;
using UnityEngine;

namespace UI
{
    /// <summary>
    /// Displays persistent run stats (score and lives) in the game HUD.
    /// </summary>
    public sealed class GameHudController : MonoBehaviour
    {
        [SerializeField] private TMP_Text scoreText;
        [SerializeField] private TMP_Text livesText;
        [SerializeField] private TMP_Text levelText;

        public void BindTexts(TMP_Text score, TMP_Text lives, TMP_Text level)
        {
            scoreText = score;
            livesText = lives;
            levelText = level;
            RefreshAll();
        }

        private void OnEnable()
        {
            Game.GameSessionState.EnsureInitialized();
            Game.GameSessionState.ScoreChanged += OnScoreChanged;
            Game.GameSessionState.LivesChanged += OnLivesChanged;
            Game.GameSessionState.LevelChanged += OnLevelChanged;

            RefreshAll();
        }

        private void OnDisable()
        {
            Game.GameSessionState.ScoreChanged -= OnScoreChanged;
            Game.GameSessionState.LivesChanged -= OnLivesChanged;
            Game.GameSessionState.LevelChanged -= OnLevelChanged;
        }

        private void OnScoreChanged(int score)
        {
            if (scoreText != null) scoreText.text = $"Score: {score}";
        }

        private void OnLivesChanged(int lives)
        {
            if (livesText == null) return;
            int max = Game.GameSessionState.MaxLives;
            var sb  = new System.Text.StringBuilder();
            for (int i = 0; i < max; i++)
                sb.Append(i < lives ? "\u2665 " : "\u2661 ");
            livesText.text = sb.ToString().TrimEnd();
        }

        private void OnLevelChanged(int level)
        {
            if (levelText != null) levelText.text = $"Level: {level}/{Game.GameSessionState.MaxLevelIndex}";
        }

        private void RefreshAll()
        {
            OnScoreChanged(Game.GameSessionState.Score);
            OnLivesChanged(Game.GameSessionState.Lives);
            OnLevelChanged(Game.GameSessionState.CurrentLevelIndex);
        }
    }
}
