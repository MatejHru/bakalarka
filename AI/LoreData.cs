using System;
using UnityEngine;

// ── Narrative data produced by Ollama for the current run ─────────────────────

/// <summary>
/// AI-generated story/narrative for one run.
/// Produced by <see cref="OllamaClient.GenerateLore"/> (or built by <see cref="FallbackLore"/>).
/// Stored in <see cref="Game.GameSessionState.CurrentLore"/> for the duration of the run.
/// </summary>
[Serializable]
public class LoreData
{
    public string   title       = "";
    public string   intro       = "";
    public string   goal        = "";
    public string[] levelFlavors = new string[5];
    public string   bossName    = "";
    public string   bossDesc    = "";

    /// <summary>True when the minimum required fields are present.</summary>
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(title) &&
        !string.IsNullOrWhiteSpace(intro);

    /// <summary>Returns the flavor text for a given 1-based level index (clamps to array bounds).</summary>
    public string GetLevelFlavor(int levelIndex)
    {
        if (levelFlavors == null || levelFlavors.Length == 0) return "";
        int idx = Mathf.Clamp(levelIndex - 1, 0, levelFlavors.Length - 1);
        return levelFlavors[idx] ?? "";
    }
}
