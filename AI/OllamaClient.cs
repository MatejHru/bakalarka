using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Sends a theme word to Ollama and returns a <see cref="LevelPlan"/> with
/// SD prompts for every visual asset (background, tiles, player, enemy).
///
/// Usage (coroutine):
/// <code>
///   yield return ollamaClient.GeneratePlan("volcano",
///       plan  => myPlan  = plan,
///       error => Debug.LogWarning(error));
/// </code>
///
/// If Ollama is unreachable or returns invalid JSON the error callback is
/// called with an explanation and the caller should use <see cref="FallbackPlan.For"/>.
///
/// Inspector fields:
///   baseUrl   — Ollama HTTP endpoint (default http://localhost:11434/api/chat)
///   modelName — Ollama model tag    (default mistral:7b)
///   timeout   — seconds before giving up
///   debugLogs — verbose console output
/// </summary>
public class OllamaClient : MonoBehaviour
{
    [Header("Ollama Connection")]
    public string baseUrl   = "http://localhost:11434/api/chat";
    public string modelName = "mistral:7b";
    [Min(5)] public int timeoutSeconds = 40;

    [Header("Debug")]
    public bool debugLogs = true;

    // ── Internal JSON types ───────────────────────────────────────────────────

    [Serializable] private class OllamaRequest
    {
        public string          model;
        public OllamaMessage[] messages;
        public bool            stream  = false;
        public string          format  = "json";  // forces Ollama to emit valid JSON only
        public OllamaOptions   options = new OllamaOptions();
    }

    [Serializable] private class OllamaMessage
    {
        public string role;
        public string content;
    }

    [Serializable] private class OllamaOptions
    {
        public int   num_predict = 500;
        public float temperature = 0.1f;
    }

    [Serializable] private class OllamaResponse
    {
        public OllamaMessage message;
    }

    // Matches the JSON keys Ollama is asked to produce for the level plan
    [Serializable] private class RawPlan
    {
        public string bg, bgNeg, ground, groundNeg,
                      player, playerNeg, enemy, enemyNeg, pickup, pickupNeg,
                      material, surface, palette;
        // enemy-type specific prompts
        public string flyingEnemy, flyingEnemyNeg, shootingEnemy, shootingEnemyNeg, bossEnemy, bossEnemyNeg;
        // gameplay stats (LLM outputs numbers; JsonUtility parses them directly)
        public float  enemySpeed       = 2.0f;
        public int    enemyDamage      = 1;
        public float  enemyPatrolRange = 3.0f;
        public int    pickupHealAmount = 0;
        public int    pickupScoreValue = 50;
        public string enemyType        = "ground";
    }

    // Lore JSON schema returned by GenerateLore
    [Serializable] private class RawLore
    {
        public string title, intro, goal;
        public string level1, level2, level3, level4, level5;
        public string bossName, bossDesc;
    }

    // ── System prompt ─────────────────────────────────────────────────────────

    private const string SystemPrompt =
        "You write very short Stable Diffusion prompts AND gameplay stats for a 2D side-scrolling platformer game. " +
        "The theme word defines ALL style, color, and mood — match it exactly. " +
        "Never rely on hardcoded theme catalogs, fixed biome lists, or special-case branches for specific theme words. " +
        "Player, enemy and pickup must always be one single subject only (never multiple). " +
        "All characters must face right (side view, looking right). " +
        "OUTPUT: minified JSON only. No markdown, no prose. " +
        "Schema: {\"bg\":\"\",\"bgNeg\":\"\",\"ground\":\"\",\"groundNeg\":\"\",\"player\":\"\",\"playerNeg\":\"\",\"enemy\":\"\",\"enemyNeg\":\"\",\"pickup\":\"\",\"pickupNeg\":\"\",\"material\":\"\",\"surface\":\"\",\"palette\":\"\",\"flyingEnemy\":\"\",\"flyingEnemyNeg\":\"\",\"shootingEnemy\":\"\",\"shootingEnemyNeg\":\"\",\"bossEnemy\":\"\",\"bossEnemyNeg\":\"\",\"enemySpeed\":2.0,\"enemyDamage\":1,\"enemyPatrolRange\":3.0,\"pickupHealAmount\":0,\"pickupScoreValue\":50,\"enemyType\":\"ground\"} " +
        "bg = 4-6 vivid words for the distant background scenery (no characters). " +
        "ground = ignored. " +
        "material = 1-3 words GEOLOGY/ARCHITECTURE only: physical substance platforms are made of. Examples: basalt rock, sandstone, oak planks, marble tile, iron metal, packed ice, terracotta brick, coral reef, obsidian. FORBIDDEN: character names, food, brands, fabric, candy, ribbon. " +
        "surface = 1-3 words texture adjectives: cracked, smooth, rough, layered, polished, weathered, mossy, rusty, glossy, porous, grainy, carved. " +
        "palette = 2-4 color words for terrain colors. " +
        "player = 4-6 words for the playable hero (MUST be a living humanoid or creature; NEVER an object). facing right. " +
        "enemy = 4-6 words for a ground-walking enemy creature. facing right. " +
        "flyingEnemy = 4-6 words for an airborne flying creature (ONLY fill if enemyType=flying, else leave empty). facing right. " +
        "shootingEnemy = 4-6 words for a ranged/shooting creature (ONLY fill if enemyType=shooting, else leave empty). facing right. " +
        "bossEnemy = 4-8 words for a giant level-5 boss creature. always provide it. facing right. " +
        "pickup = 3-5 words for a small collectible item. " +
        "bgNeg/groundNeg/playerNeg/enemyNeg/pickupNeg/flyingEnemyNeg/shootingEnemyNeg/bossEnemyNeg = 3-5 words max. " +
        "enemySpeed = float 0.5-5.0 matching theme danger. enemyDamage = integer 1 or 2. enemyPatrolRange = float 1.0-6.0. " +
        "pickupHealAmount = 0 or 1. pickupScoreValue = integer 25-200. " +
        "enemyType = exactly one of: ground, flying, shooting. Choose based on level number and theme. " +
        "Keep visual prompts under 10 words each. Output ONLY the JSON object.";

    private const string LoreSystemPrompt =
        "You generate short narrative text for a 2D platformer video game. " +
        "The theme word defines the setting, style, and tone — match it exactly. " +
        "Narrative must stay compatible with abstract gameplay systems: enemies, hazards, platforms, generic collectibles, and level exit. " +
        "Do NOT require specific collectible item names, quest items, NPC dialog interactions, or mechanics not guaranteed in-game. " +
        "Use objective wording like collect energy, survive, reach the exit, defeat the boss. " +
        "OUTPUT: minified JSON only. No markdown, no extra prose. " +
        "Schema: {\"title\":\"\",\"intro\":\"\",\"goal\":\"\",\"level1\":\"\",\"level2\":\"\",\"level3\":\"\",\"level4\":\"\",\"level5\":\"\",\"bossName\":\"\",\"bossDesc\":\"\"} " +
        "title: 3-6 word game title inspired by the theme. " +
        "intro: exactly 2 sentences setting the scene with stakes and atmosphere. 45-90 words total. " +
        "goal: 1-2 sentences describing objective and consequence of failure. Keep objectives generic and system-compatible. 20-45 words. " +
        "level1-5: exactly 1 sentence for each level, escalating danger and variety. 18-35 words each. " +
        "bossName: 2-5 word boss name. bossDesc: 2 sentences describing behavior, threat, and visual identity. 30-60 words total. " +
        "Output ONLY the JSON object.";

    // ── Public API ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Coroutine: ask Ollama to generate a <see cref="LevelPlan"/> for <paramref name="theme"/>.
    /// Calls <paramref name="onDone"/> on success, <paramref name="onError"/> on failure.
    /// </summary>
    public IEnumerator GeneratePlan(string theme,
                                    Action<LevelPlan> onDone,
                                    Action<string>    onError)
    {
        Log($"Asking Ollama ({modelName}) for theme \"{theme}\"...");

        var request = new OllamaRequest
        {
            model    = modelName,
            messages = new[]
            {
                new OllamaMessage { role = "system", content = SystemPrompt },
                new OllamaMessage
                {
                    role = "user",
                    content =
                        $"Theme: {theme}. Match style, color, mood. " +
                        "Terrain material: think like a geologist — what PHYSICAL SUBSTANCE are the platforms made of? " +
                        "volcano=basalt rock, hotel=marble tile, minecraft=dirt and grass, space=metal grating, jungle=mossy stone, ocean=coral stone. " +
                        "Material must be a real physical substance, NOT a character/brand/food name. " +
                        "Background: distant scenery only. Characters: describe appearance only."
                }
            }
        };

        string bodyJson = JsonUtility.ToJson(request);
        byte[] bodyBytes = Encoding.UTF8.GetBytes(bodyJson);

        using var www = new UnityWebRequest(baseUrl, "POST");
        www.uploadHandler   = new UploadHandlerRaw(bodyBytes);
        www.downloadHandler = new DownloadHandlerBuffer();
        www.SetRequestHeader("Content-Type", "application/json");
        www.timeout = timeoutSeconds;

        yield return www.SendWebRequest();

        if (www.result != UnityWebRequest.Result.Success)
        {
            string err = $"Ollama network error {www.responseCode}: {www.error}";
            Log(err);
            onError?.Invoke(err);
            yield break;
        }

        string responseText = www.downloadHandler.text;
        Log($"Ollama raw response: {responseText.Substring(0, Mathf.Min(300, responseText.Length))}");

        // Parse outer Ollama envelope
        OllamaResponse ollamaResp;
        try { ollamaResp = JsonUtility.FromJson<OllamaResponse>(responseText); }
        catch (Exception ex)
        {
            string err = $"Ollama outer JSON parse failed: {ex.Message}";
            Log(err);
            onError?.Invoke(err);
            yield break;
        }

        string content = ollamaResp?.message?.content;
        if (string.IsNullOrWhiteSpace(content))
        {
            const string err = "Ollama returned empty message content.";
            Log(err);
            onError?.Invoke(err);
            yield break;
        }

        RawPlan raw = ParsePlanContent(content, out string extractedDebug, out string parseError);
        Log($"Extracted plan JSON: {extractedDebug}");

        if (!string.IsNullOrEmpty(parseError))
        {
            Log(parseError);
            onError?.Invoke(parseError);
            yield break;
        }

        if (raw == null || string.IsNullOrWhiteSpace(raw.bg))
        {
            const string err = "Plan JSON is missing required fields.";
            Log(err);
            onError?.Invoke(err);
            yield break;
        }

        string normalizedTheme = NormalizeTheme(theme);

        // Keep prompts SHORT, but never let the core theme noun disappear.
        string bgHint  = NormalizePhrase(raw.bg);
        string bg      = string.IsNullOrWhiteSpace(bgHint) ? "game background scenery" : bgHint;
        string bgNeg   = NormalizePhrase(raw.bgNeg);

        string enHint  = NormalizePhrase(raw.enemy);
        string en      = string.IsNullOrWhiteSpace(enHint) ? "enemy creature" : enHint;
        string enNeg   = NormalizePhrase(raw.enemyNeg);

        string puHint  = NormalizePhrase(raw.pickup);
        string pu      = string.IsNullOrWhiteSpace(puHint) ? "collectible item" : puHint;
        string puNeg   = NormalizePhrase(raw.pickupNeg);

        const string bgFixed  = "2D side-scroller background, distant scenery";
        const string bgNegFix = "character, person, ui, text, watermark, UI";
        const string spNegFix = "sprite sheet, reference sheet, grid, mosaic, split image, multiple, duplicates, twins, duo, frame, border, badge, label, scenery, environment, landscape, sky, background, text, ground, shadows, pedestal, base";

        // Enemy-type specific prompts
        string flyHint  = NormalizePhrase(raw.flyingEnemy);
        string flyNeg   = NormalizePhrase(raw.flyingEnemyNeg);
        string shotHint = NormalizePhrase(raw.shootingEnemy);
        string shotNeg  = NormalizePhrase(raw.shootingEnemyNeg);
        string bossHint = NormalizePhrase(raw.bossEnemy);
        string bossNeg  = NormalizePhrase(raw.bossEnemyNeg);

        string eType = (raw.enemyType == "flying" || raw.enemyType == "shooting")
            ? raw.enemyType : "ground";

        var plan = new LevelPlan
        {
            backgroundPrompt   = $"{normalizedTheme}, {bg}, {bgFixed}",
            backgroundNegative = string.IsNullOrWhiteSpace(bgNeg)  ? bgNegFix  : bgNeg  + ", " + bgNegFix,
            groundPrompt       = BuildTerrainAssetPrompt(normalizedTheme, raw.material, raw.surface, raw.palette, raw.ground),
            groundNegative     = BuildTerrainAssetNegative(raw.groundNeg),
            playerPrompt       = BuildPlayerPrompt(normalizedTheme, raw.player),
            playerNegative     = BuildPlayerNegative(raw.playerNeg),
            enemyPrompt        = $"one single {en} (theme: {normalizedTheme}). side view, facing right, 2D game creature sprite, full body visible, centered, completely isolated character design, uniform flat simple solid-color background, no shadows, no scenery",
            enemyNegative      = string.IsNullOrWhiteSpace(enNeg) ? spNegFix : enNeg + ", " + spNegFix,
            pickupPrompt       = $"one single {pu} (theme: {normalizedTheme}). 2D game collectible item sprite, centered, completely isolated, no holder, no pedestal, uniform flat simple solid-color background, no shadows, no scenery",
            pickupNegative     = string.IsNullOrWhiteSpace(puNeg) ? spNegFix : puNeg + ", " + spNegFix,

            // Flying/shooting enemy visual prompts
            flyingEnemyPrompt   = string.IsNullOrWhiteSpace(flyHint) ? "" :
                $"one single {flyHint} (theme: {normalizedTheme}). airborne, wings visible, side view, facing right, 2D game sprite, full body visible, centered, completely isolated, uniform flat simple solid-color background, no shadows, no scenery",
            flyingEnemyNegative = string.IsNullOrWhiteSpace(flyNeg)  ? spNegFix : flyNeg  + ", " + spNegFix,
            shootingEnemyPrompt = string.IsNullOrWhiteSpace(shotHint) ? "" :
                $"one single {shotHint} (theme: {normalizedTheme}). ranged fighter, side view, facing right, 2D game sprite, full body visible, centered, completely isolated, uniform flat simple solid-color background, no shadows, no scenery",
            shootingEnemyNegative = string.IsNullOrWhiteSpace(shotNeg) ? spNegFix : shotNeg + ", " + spNegFix,
            bossEnemyPrompt = string.IsNullOrWhiteSpace(bossHint)
                ? $"one single giant boss monster (theme: {normalizedTheme}). intimidating silhouette, side view, facing right, 2D game sprite, full body visible, centered, completely isolated, uniform flat simple solid-color background, no shadows, no scenery"
                : $"one single {bossHint} (theme: {normalizedTheme}). giant intimidating boss, side view, facing right, 2D game sprite, full body visible, centered, completely isolated, uniform flat simple solid-color background, no shadows, no scenery",
            bossEnemyNegative = string.IsNullOrWhiteSpace(bossNeg) ? spNegFix : bossNeg + ", " + spNegFix,

            // Terrain structured fields (used by ComposeTerrainPrompt)
            terrainMaterial  = NormalizePhrase(raw.material),
            terrainSurface   = NormalizePhrase(raw.surface),
            terrainPalette   = NormalizePhrase(raw.palette),

            // Gameplay stats from LLM (clamp to sane ranges)
            enemySpeed       = Mathf.Clamp(raw.enemySpeed,       0.5f, 6.0f),
            enemyDamage      = Mathf.Clamp(raw.enemyDamage,      1,    3),
            enemyPatrolRange = Mathf.Clamp(raw.enemyPatrolRange, 1.0f, 7.0f),
            pickupHealAmount = Mathf.Clamp(raw.pickupHealAmount, 0,    2),
            pickupScoreValue = Mathf.Clamp(raw.pickupScoreValue, 10,   500),
            enemyType        = eType,
        };

        Log("LevelPlan generated successfully.");
        onDone?.Invoke(plan);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static RawPlan ParsePlanContent(string text, out string extractedDebug, out string error)
    {
        extractedDebug = "{}";
        error = null;

        List<string> objects = ExtractJsonObjects(text);
        if (objects.Count == 0)
        {
            error = "Plan JSON parse failed: no JSON object found in Ollama output.";
            return null;
        }

        extractedDebug = string.Join("\n", objects);

        var merged = new RawPlan();
        bool anyParsed = false;
        foreach (string json in objects)
        {
            try
            {
                RawPlan part = JsonUtility.FromJson<RawPlan>(json);
                if (part == null) continue;
                MergeInto(merged, part);
                anyParsed = true;
            }
            catch (Exception ex)
            {
                error = $"Plan JSON parse failed: {ex.Message}";
                return null;
            }
        }

        if (!anyParsed)
        {
            error = "Plan JSON parse failed: extracted JSON objects could not be read.";
            return null;
        }

        return merged;
    }

    private static List<string> ExtractJsonObjects(string text)
    {
        var objects = new List<string>();
        if (string.IsNullOrEmpty(text)) return objects;

        int depth = 0;
        int start = -1;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch == '{')
            {
                if (depth == 0) start = i;
                depth++;
            }
            else if (ch == '}')
            {
                if (depth <= 0) continue;
                depth--;
                if (depth == 0 && start >= 0)
                {
                    objects.Add(text.Substring(start, i - start + 1));
                    start = -1;
                }
            }
        }

        return objects;
    }

    private static void MergeInto(RawPlan target, RawPlan source)
    {
        target.bg              = PreferFilled(target.bg,              source.bg);
        target.bgNeg           = PreferFilled(target.bgNeg,           source.bgNeg);
        target.ground          = PreferFilled(target.ground,          source.ground);
        target.groundNeg       = PreferFilled(target.groundNeg,       source.groundNeg);
        target.player          = PreferFilled(target.player,          source.player);
        target.playerNeg       = PreferFilled(target.playerNeg,       source.playerNeg);
        target.enemy           = PreferFilled(target.enemy,           source.enemy);
        target.enemyNeg        = PreferFilled(target.enemyNeg,        source.enemyNeg);
        target.pickup          = PreferFilled(target.pickup,          source.pickup);
        target.pickupNeg       = PreferFilled(target.pickupNeg,       source.pickupNeg);
        target.material        = PreferFilled(target.material,        source.material);
        target.surface         = PreferFilled(target.surface,         source.surface);
        target.palette         = PreferFilled(target.palette,         source.palette);
        target.flyingEnemy     = PreferFilled(target.flyingEnemy,     source.flyingEnemy);
        target.flyingEnemyNeg  = PreferFilled(target.flyingEnemyNeg,  source.flyingEnemyNeg);
        target.shootingEnemy   = PreferFilled(target.shootingEnemy,   source.shootingEnemy);
        target.shootingEnemyNeg = PreferFilled(target.shootingEnemyNeg, source.shootingEnemyNeg);
        target.bossEnemy       = PreferFilled(target.bossEnemy,       source.bossEnemy);
        target.bossEnemyNeg    = PreferFilled(target.bossEnemyNeg,    source.bossEnemyNeg);
        target.enemyType       = PreferFilled(target.enemyType,       source.enemyType);
        // Numeric stats: only overwrite if source has a non-default meaningful value
        if (source.enemySpeed       > 0.01f) target.enemySpeed       = source.enemySpeed;
        if (source.enemyDamage      > 0)     target.enemyDamage      = source.enemyDamage;
        if (source.enemyPatrolRange > 0.01f) target.enemyPatrolRange = source.enemyPatrolRange;
        if (source.pickupHealAmount >= 0)    target.pickupHealAmount = source.pickupHealAmount;
        if (source.pickupScoreValue > 0)     target.pickupScoreValue = source.pickupScoreValue;
    }

    private static string PreferFilled(string current, string candidate)
        => string.IsNullOrWhiteSpace(current) ? (candidate ?? "") : current;

    private static string BuildTerrainAssetPrompt(string theme, string material, string surface, string palette, string llmSuggestion)
    {
        string mat = string.IsNullOrWhiteSpace(material) ? "stone" : material.Trim();
        string sur = string.IsNullOrWhiteSpace(surface)  ? "rough"  : surface.Trim();
        string pal = string.IsNullOrWhiteSpace(palette)  ? ""       : ", " + palette.Trim();
        string them = NormalizeTheme(theme);

        // "stylized 2D game art style, cartoon game texture, bold outlines, cel shading" —
        // this vocabulary reliably produces flat bird's-eye game ground tiles in dreamshaper.
        // "straight down bird's eye view, flat lay" prevents 3D perspective without triggering
        // the diagonal-stripe artifact that "seamless tile" caused.
        // IMPORTANT: Do not inject llmSuggestion here.
        // Free-form suggestion frequently contains scene nouns (mountain/beach/etc.)
        // and causes perspective/photo tiles. Keep prompt material-only.
        // Also avoid leading with the raw theme word; it often pushes SD to generate
        // full scene photos (beach shoreline, forest canopy) instead of a tile texture.
        return $"{mat} ground surface microtexture, {sur}{pal}, natural irregular material grain, non-directional fine detail, top-down close-up, texture sample, full frame fill, high detail, sharp focus, even lighting, surface only, no scene composition, no map view, no coastline, no forest canopy, no roads, no paths, no horizon, no islands, no channels, no trees, no leaves, no objects, no characters, no faces";
    }

    private static string BuildTerrainAssetNegative(string baseNegative)
    {
        const string hardNeg = "3d scene, 3d render, perspective, vanishing point, angle, tilted, side view, raised surface, elevation, bump, depth, diagonal stripes, seamless tile, tiling artifact, decorative pattern, ornamental, symmetrical pattern, floral pattern, abstract pattern, radial pattern, starburst, mandala, spokes, kaleidoscope, cartoon, cel shading, anime, character, person, face, portrait, skull, mask, logo, text, watermark, border, frame, white background, black background, sky, horizon, landscape, aerial photo, drone shot, satellite view, map view, coastline, shoreline, beach photo, ocean waves, lagoon, island, river delta, forest canopy, treetops, leaves, branches, road network, path intersection, trail, crossroads, isometric, checkerboard";
        return string.IsNullOrWhiteSpace(baseNegative) ? hardNeg : baseNegative + ", " + hardNeg;
    }

    private static string BuildPlayerPrompt(string theme, string basePrompt)
    {
        string hint = NormalizePhrase(basePrompt);
        string normalizedTheme = NormalizeTheme(theme);
        string appearance = string.IsNullOrWhiteSpace(hint) ? "hero character" : hint;
        return $"one single humanoid protagonist, person, {appearance} (theme: {normalizedTheme}), standing straight, facing sideways. 2D game sprite, full body visible, centered, completely isolated character design, uniform flat simple solid-color background, no shadows, no scenery";
    }

    private static string BuildPlayerNegative(string baseNegative)
    {
        const string hardNeg = "sprite sheet, reference sheet, multiple, duplicates, twins, duo, group, collage, circle, window, badge, frame, split image, inanimate object, cropped, cut-off, gray background, black background, gradient background, background, landscape, environment, outdoors, room, ground, shadow, pedestal, stand, base";
        return string.IsNullOrWhiteSpace(baseNegative) ? hardNeg : baseNegative + ", " + hardNeg;
    }

    private static string NormalizePhrase(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        return text.Trim().Trim(',');
    }

    private static string NormalizeTheme(string theme)
    {
        if (string.IsNullOrWhiteSpace(theme)) return "fantasy";
        return theme.Trim().ToLowerInvariant();
    }

    // ── Lore generation ───────────────────────────────────────────────────────

    /// <summary>
    /// Coroutine: ask Ollama to generate a <see cref="LoreData"/> narrative for
    /// <paramref name="theme"/>.  Calls <paramref name="onDone"/> on success,
    /// <paramref name="onError"/> on failure (caller should use <see cref="FallbackLore"/>).
    /// </summary>
    public IEnumerator GenerateLore(string theme,
                                    Action<LoreData> onDone,
                                    Action<string>   onError)
    {
        Log($"Asking Ollama for lore: theme \"{theme}\"...");

        var request = new OllamaRequest
        {
            model    = modelName,
            messages = new[]
            {
                new OllamaMessage { role = "system",  content = LoreSystemPrompt },
                new OllamaMessage { role = "user",
                    content = $"Theme: {theme}. Generate the narrative JSON." }
            },
            options = new OllamaOptions { num_predict = 600, temperature = 0.4f }
        };

        string bodyJson  = JsonUtility.ToJson(request);
        byte[] bodyBytes = Encoding.UTF8.GetBytes(bodyJson);

        using var www = new UnityWebRequest(baseUrl, "POST");
        www.uploadHandler   = new UploadHandlerRaw(bodyBytes);
        www.downloadHandler = new DownloadHandlerBuffer();
        www.SetRequestHeader("Content-Type", "application/json");
        www.timeout = timeoutSeconds;

        yield return www.SendWebRequest();

        if (www.result != UnityWebRequest.Result.Success)
        {
            string err = $"[OllamaClient] Lore network error {www.responseCode}: {www.error}";
            Log(err);
            onError?.Invoke(err);
            yield break;
        }

        OllamaResponse resp;
        try { resp = JsonUtility.FromJson<OllamaResponse>(www.downloadHandler.text); }
        catch (Exception ex)
        {
            onError?.Invoke($"Lore outer JSON parse failed: {ex.Message}");
            yield break;
        }

        string content = resp?.message?.content;
        if (string.IsNullOrWhiteSpace(content))
        {
            onError?.Invoke("Lore: Ollama returned empty content.");
            yield break;
        }

        // Extract first JSON object from response
        var objects = ExtractJsonObjects(content);
        if (objects.Count == 0)
        {
            onError?.Invoke("Lore: no JSON object found in Ollama output.");
            yield break;
        }

        RawLore raw;
        try { raw = JsonUtility.FromJson<RawLore>(objects[0]); }
        catch (Exception ex)
        {
            onError?.Invoke($"Lore JSON parse failed: {ex.Message}");
            yield break;
        }

        if (raw == null || string.IsNullOrWhiteSpace(raw.title))
        {
            onError?.Invoke("Lore: missing required fields.");
            yield break;
        }

        var lore = new LoreData
        {
            title    = raw.title   ?? "",
            intro    = raw.intro   ?? "",
            goal     = raw.goal    ?? "",
            bossName = raw.bossName ?? "",
            bossDesc = raw.bossDesc ?? "",
            levelFlavors = new[]
            {
                raw.level1 ?? "",
                raw.level2 ?? "",
                raw.level3 ?? "",
                raw.level4 ?? "",
                raw.level5 ?? ""
            }
        };

        Log($"Lore generated: \"{lore.title}\"");
        onDone?.Invoke(lore);
    }

    private void Log(string msg)
    {
        if (debugLogs) Debug.Log($"[OllamaClient] {msg}");
    }
}
