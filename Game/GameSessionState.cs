using System;
using UnityEngine;

    /// <summary>Player-selectable difficulty level.</summary>
    public enum Difficulty { Easy, Normal, Hard }

namespace Game
{
    /// <summary>
    /// Lightweight runtime session state that persists across scene reloads.
    /// </summary>
    public static class GameSessionState
    {
        public const int MaxLives = 3;
        public const int MaxLevelIndex = 5;

        public static int Score { get; private set; }
        public static int Lives { get; private set; }
        public static string BaseTheme { get; private set; } = "forest";
        public static int CurrentLevelIndex { get; private set; } = 1;
        public static int RunSeed { get; private set; }
        public static LevelBundle PendingLevelBundle { get; private set; }
        public static int PendingLevelIndex { get; private set; }
        public static Texture2D LockedPlayerSkin { get; private set; }
        public static Texture2D CurrentProjectileSkin { get; private set; }

        // ── Run-wide settings & AI-generated data ─────────────────────────────
        public static Difficulty  CurrentDifficulty  { get; private set; } = Difficulty.Normal;
        public static LoreData    CurrentLore        { get; private set; }
        public static LevelPlan   CurrentLevelPlan   { get; private set; }

        public static void SetDifficulty(Difficulty d)   => CurrentDifficulty = d;
        public static void SetLore(LoreData lore)         => CurrentLore = lore;
        public static void SetCurrentLevelPlan(LevelPlan plan) => CurrentLevelPlan = plan;
        public static void SetCurrentProjectileSkin(Texture2D texture) => CurrentProjectileSkin = texture;

        public static event Action<int> ScoreChanged;
        public static event Action<int> LivesChanged;
        public static event Action<int> LevelChanged;

        private static bool _initialized;

        public static void EnsureInitialized()
        {
            if (_initialized) return;
            ResetRun();
            _initialized = true;
        }

        public static void ResetRun()
        {
            Score = 0;
            Lives = MaxLives;
            CurrentLevelIndex = 1;
            RunSeed = 0;
            PendingLevelBundle = null;
            PendingLevelIndex = 0;
            LockedPlayerSkin = null;
            CurrentProjectileSkin = null;
            CurrentLore = null;
            CurrentLevelPlan = null;
            ScoreChanged?.Invoke(Score);
            LivesChanged?.Invoke(Lives);
            LevelChanged?.Invoke(CurrentLevelIndex);
            Debug.Log("[GameSessionState] Run reset: score=0 lives=3");
        }

        public static void BeginRun(string baseTheme)
        {
            Difficulty selectedDifficulty = CurrentDifficulty;
            ResetRun();
            CurrentDifficulty = selectedDifficulty;
            BaseTheme = string.IsNullOrWhiteSpace(baseTheme) ? "forest" : baseTheme.Trim();
            CurrentLevelIndex = 1;
            RunSeed = UnityEngine.Random.Range(int.MinValue, int.MaxValue);
            LevelChanged?.Invoke(CurrentLevelIndex);
            Debug.Log($"[GameSessionState] Begin run: theme=\"{BaseTheme}\" seed={RunSeed} difficulty={CurrentDifficulty} level=1");
        }

        public static void AdvanceLevel()
        {
            EnsureInitialized();
            CurrentLevelIndex = Mathf.Clamp(CurrentLevelIndex + 1, 1, MaxLevelIndex);
            LevelChanged?.Invoke(CurrentLevelIndex);
            Debug.Log($"[GameSessionState] Advance level -> {CurrentLevelIndex}");
        }

        public static void StorePendingBundle(int levelIndex, LevelBundle bundle)
        {
            if (bundle == null || levelIndex < 1) return;
            PendingLevelBundle = bundle;
            PendingLevelIndex = levelIndex;
            Debug.Log($"[GameSessionState] Stored pre-generated bundle for level {levelIndex}");
        }

        public static bool TryConsumePendingBundle(int levelIndex, out LevelBundle bundle)
        {
            if (PendingLevelBundle != null && PendingLevelIndex == levelIndex)
            {
                bundle = PendingLevelBundle;
                PendingLevelBundle = null;
                PendingLevelIndex = 0;
                return true;
            }

            bundle = null;
            return false;
        }

        public static bool TryGetLockedPlayerSkin(out Texture2D texture)
        {
            texture = LockedPlayerSkin;
            return texture != null;
        }

        public static void StoreLockedPlayerSkin(Texture2D texture)
        {
            if (texture == null) return;
            LockedPlayerSkin = texture;
            Debug.Log("[GameSessionState] Locked player skin stored for this run.");
        }

        public static string ComposeCurrentLevelTheme()
            => ThemeVariationComposer.ComposeVariantTag(BaseTheme, CurrentLevelIndex, RunSeed);

        public static void AddScore(int amount)
        {
            EnsureInitialized();
            if (amount <= 0) return;
            Score += amount;
            ScoreChanged?.Invoke(Score);
        }

        public static int LoseLives(int amount)
        {
            EnsureInitialized();
            if (amount <= 0) return Lives;
            Lives = Mathf.Max(0, Lives - amount);
            LivesChanged?.Invoke(Lives);
            return Lives;
        }

        public static int AddLives(int amount)
        {
            EnsureInitialized();
            if (amount <= 0) return Lives;
            Lives = Mathf.Min(MaxLives, Lives + amount);
            LivesChanged?.Invoke(Lives);
            return Lives;
        }
    }
}
