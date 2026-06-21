using System;
using UnityEngine;

// ── Data transferred between Ollama and AiLevelPipeline ──────────────────────

/// <summary>
/// SD prompts and gameplay stats for one level.
/// Produced by <see cref="OllamaClient"/> (or built by <see cref="FallbackPlan"/>).
/// </summary>
[Serializable]
public class LevelPlan
{
    // ── Visual prompts ────────────────────────────────────────────────────────
    public string backgroundPrompt   = "";
    public string backgroundNegative = "";
    public string groundPrompt       = "";
    public string groundNegative     = "";
    public string playerPrompt       = "";
    public string playerNegative     = "";
    public string enemyPrompt        = "";
    public string enemyNegative      = "";
    public string pickupPrompt       = "";
    public string pickupNegative     = "";

    // ── Enemy-type-specific SD prompts ────────────────────────────────────────
    /// <summary>SD prompt for the flying enemy variant (used when enemyType == "flying").</summary>
    public string flyingEnemyPrompt   = "";
    public string flyingEnemyNegative = "";
    /// <summary>SD prompt for the shooting enemy variant (used when enemyType == "shooting").</summary>
    public string shootingEnemyPrompt   = "";
    public string shootingEnemyNegative = "";
    /// <summary>SD prompt for level 5 boss sprite.</summary>
    public string bossEnemyPrompt   = "";
    public string bossEnemyNegative = "";

    // ── Gameplay stats (set by Ollama or FallbackPlan, scaled by DifficultyMultiplier) ──
    /// <summary>Enemy movement speed in Unity units/second.</summary>
    public float  enemySpeed       = 2.0f;
    /// <summary>Damage dealt per enemy contact hit.</summary>
    public int    enemyDamage      = 1;
    /// <summary>Enemy patrol radius in tiles.</summary>
    public float  enemyPatrolRange = 3.0f;
    /// <summary>HP healed per pickup. 0 = score-only collectible.</summary>
    public int    pickupHealAmount = 0;
    /// <summary>Score awarded per pickup collected.</summary>
    public int    pickupScoreValue = 50;
    /// <summary>Active enemy type for this level: "ground" | "flying" | "shooting".</summary>
    public string enemyType        = "ground";

    // ── Terrain material descriptors (used by ComposeTerrainPrompt) ───────────
    /// <summary>Primary material of the terrain surface (e.g. "stone", "ice", "wood planks").</summary>
    public string terrainMaterial = "";
    /// <summary>Surface quality description (e.g. "rough cracked", "smooth icy", "mossy").</summary>
    public string terrainSurface  = "";
    /// <summary>Color palette hint (e.g. "cool blue-grey tones", "warm amber hues").</summary>
    public string terrainPalette  = "";

    /// <summary>True if the minimum visual prompts are present.</summary>
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(backgroundPrompt) &&
        !string.IsNullOrWhiteSpace(groundPrompt) &&
        !string.IsNullOrWhiteSpace(playerPrompt);
}

/// <summary>
/// All AI-generated textures for one level.
/// Populated by <see cref="AiLevelPipeline"/> and consumed by <see cref="Level.LevelAssembler"/>.
/// Null fields mean generation failed — the scene's placeholder sprites remain visible.
/// </summary>
public class LevelBundle
{
    public Texture2D background;
    public Texture2D terrainTile;   // used for both floor and floating platforms
    public Texture2D playerSkin;
    public Texture2D groundEnemySkin;    // ground walking enemy
    public Texture2D flyingEnemySkin;    // flying/airborne enemy
    public Texture2D shootingEnemySkin;  // ranged/shooting enemy
    public Texture2D bossEnemySkin;      // level 5 boss skin
    public Texture2D shootingProjectileSkin; // projectile fired by shooting enemy
    public Texture2D pickupSkin;

    /// <summary>Metrics collected during generation. Null when bundle was pre-loaded or metrics were not collected.</summary>
    public Metrics.GenerationMetrics metrics;
}

/// <summary>
/// Generic fallback prompts used when Ollama is unavailable or returns invalid JSON.
/// Every asset prompt is derived from the user theme so visuals stay coherent for
/// arbitrary inputs instead of relying on a small set of hardcoded theme presets.
/// </summary>
public static class FallbackPlan
{
    public static LevelPlan For(string theme, int levelIndex, int runSeed)
    {
        string normalizedTheme = NormalizeTheme(theme);
        string variantTag = ThemeVariationComposer.ComposeVariantTag(normalizedTheme, levelIndex, runSeed);
        string themed = $"{normalizedTheme}, {variantTag}";

        // Gameplay stats scale gently with level progression (1 = easiest, 5 = hardest).
        int   lvl        = UnityEngine.Mathf.Clamp(levelIndex, 1, 5);
        float speedScale = 1f + (lvl - 1) * 0.25f; // 1.0 at level 1 → 2.0 at level 5
        string eType     = SelectEnemyType(levelIndex, runSeed);

        string flyingHint   = $"{themed}, one single flying creature, wings visible, airborne, full body, centered, side view, facing right, game sprite, isolated on pure white background";
        string shootingHint = $"{themed}, one single ranged creature, armed, alert stance, full body, centered, side view, facing right, game sprite, isolated on pure white background";
        string bossHint = $"{themed}, one single giant boss monster, intimidating silhouette, full body visible, centered, side view, facing right, game sprite, isolated on pure white background";

        return new LevelPlan
        {
            // ── Visual prompts ─────────────────────────────────────────────────
            backgroundPrompt   = $"{themed}, wide distant 2D game background, fitting scenery, readable side scroller scene",
            backgroundNegative = "character, ui, text, watermark, close-up, 3d render",
            groundPrompt       = $"ground surface microtexture, natural irregular material grain, non-directional fine detail, inspired by {normalizedTheme} color mood, top-down close-up, texture sample, full frame fill, high detail, sharp focus, even lighting, surface only, no scene composition, no map view, no coastline, no forest canopy, no roads, no paths, no horizon, no islands, no channels, no trees, no leaves, no objects, no characters, no faces",
            groundNegative     = "3d scene, 3d render, perspective, vanishing point, angle, tilted, side view, raised surface, elevation, bump, depth, diagonal stripes, seamless tile, tiling artifact, ornamental, decorative pattern, symmetrical pattern, floral pattern, abstract pattern, radial pattern, starburst, mandala, spokes, kaleidoscope, character, person, face, portrait, skull, mask, logo, text, watermark, border, frame, white background, black background, sky, horizon, landscape, aerial photo, drone shot, satellite view, map view, coastline, shoreline, beach photo, ocean waves, lagoon, island, river delta, forest canopy, treetops, leaves, branches, road network, path intersection, trail, crossroads, isometric, checkerboard",
            playerPrompt       = $"{normalizedTheme}, one single humanoid protagonist, full body visible, centered, side view, facing right, game sprite, isolated on pure white background",
            playerNegative     = "multiple characters, duplicates, twins, duo, group, sprite sheet, reference sheet, background, scenery, cropped, out of frame, text, logo, watermark",
            enemyPrompt        = $"{themed}, one single enemy creature, full body visible, centered, side view, facing right, game sprite, isolated on pure white background",
            enemyNegative      = "multiple enemies, duplicates, twins, sprite sheet, reference sheet, background, scenery, cropped, text, watermark",
            pickupPrompt       = $"{themed}, one single collectible item, centered, readable silhouette, isolated game sprite, pure white background",
            pickupNegative     = "multiple items, sprite sheet, reference sheet, holder, pedestal, character, creature, background, text, watermark",

            flyingEnemyPrompt   = flyingHint,
            flyingEnemyNegative = "multiple enemies, duplicates, sprite sheet, background, scenery, text, watermark",
            shootingEnemyPrompt   = shootingHint,
            shootingEnemyNegative = "multiple enemies, duplicates, sprite sheet, background, scenery, text, watermark",
            bossEnemyPrompt       = bossHint,
            bossEnemyNegative     = "human, person, multiple enemies, duplicates, sprite sheet, background, scenery, text, watermark",

            // ── Gameplay stats ─────────────────────────────────────────────────
            enemySpeed       = 1.5f * speedScale,
            enemyDamage      = lvl >= 4 ? 2 : 1,
            enemyPatrolRange = 2.5f + (lvl - 1) * 0.4f,
            pickupHealAmount = lvl <= 2 ? 1 : 0,
            pickupScoreValue = 40 + lvl * 15,
            enemyType        = eType,

            // ── Terrain descriptors ────────────────────────────────────────────
            terrainMaterial = "durable stone composite platform material",
            terrainSurface  = "rough weathered cracked",
            terrainPalette  = $"{normalizedTheme} inspired colors",
        };
    }

    private static string SelectEnemyType(int levelIndex, int runSeed)
    {
        if (levelIndex <= 2) return "ground"; // introductory levels always use ground enemies
        if (levelIndex >= 5) return "ground"; // boss level overrides enemy type at runtime
        int hash = System.Math.Abs(levelIndex * 31 + runSeed);
        int val  = hash % 3;
        return val == 1 ? "flying" : val == 2 ? "shooting" : "ground";
    }

    public static LevelPlan For(string theme)
    {
        return For(theme, 1, 0);
    }

    private static string NormalizeTheme(string theme)
    {
        string trimmed = string.IsNullOrWhiteSpace(theme) ? "fantasy" : theme.Trim();
        return trimmed.ToLowerInvariant();
    }
}

public static class ThemeVariationComposer
{
    public static string ComposeVariantTag(string baseTheme, int levelIndex, int runSeed)
    {
        string normalizedTheme = string.IsNullOrWhiteSpace(baseTheme) ? "fantasy" : baseTheme.Trim().ToLowerInvariant();
        int normalizedLevel = Mathf.Max(1, levelIndex);

        int hash = (normalizedTheme + "|" + normalizedLevel + "|" + runSeed).GetHashCode();
        int a = PositiveIndex(hash, 97);
        int b = PositiveIndex(hash / 17, 97);
        int c = PositiveIndex(hash / 37, 97);

        // Neutral variation tokens: preserve user theme and avoid forcing unrelated art directions.
        return $"stage {normalizedLevel}, visual variation {a}-{b}-{c}";
    }

    private static int PositiveIndex(int value, int size)
    {
        int mod = value % size;
        if (mod < 0) mod += size;
        return mod;
    }
}
