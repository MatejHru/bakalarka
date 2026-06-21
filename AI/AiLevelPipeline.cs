using System;
using System.Collections;
using UnityEngine;

/// <summary>
/// Full AI level generation pipeline for one level:
///   1. Read <see cref="LevelPlan"/> from PlayerPrefs (set by <see cref="UI.MainMenuController"/>
///      after calling Ollama).  Falls back to <see cref="FallbackPlan.For"/> if missing.
///   2. Send one SD request per asset to Stable Diffusion (A1111).
///   3. Fire <see cref="OnBundleReady"/> with the completed <see cref="LevelBundle"/>.
///
/// Assets generated:
///   • Background  — 768 × 384  (landscape, no tiling)
///   • Ground tile — 128 × 128  (1x1 terrain block)
///   • Platform tile — reused from ground tile
///   • Player skin — 256 × 256  (sprite, white bg removed)
///   • Enemy skin  — 256 × 256  (sprite, white bg removed)
///   • Pickup skin — 128 × 128  (sprite, white bg removed)
///
/// Inspector:
///   sd          — StableDiffusion component
///   checkpoint  — A1111 model checkpoint name
///   enableDebugLogs — verbose Console output (debug mode)
/// </summary>
public class AiLevelPipeline : MonoBehaviour
{
    // ── Events ────────────────────────────────────────────────────────────────
    public static event Action<LevelBundle> OnBundleReady;
    public static event Action<string>      OnGenerationFailed;
    public static event Action<float, string> OnGenerationProgress;

    private struct SpriteBgAttempt
    {
        public readonly string Name;
        public readonly string PromptBg;
        public readonly string NegativeBg;
        public readonly Color32 KeyColor;
        public readonly int KeyThreshold;

        public SpriteBgAttempt(string name, string promptBg, string negativeBg, Color32 keyColor, int keyThreshold)
        {
            Name = name;
            PromptBg = promptBg;
            NegativeBg = negativeBg;
            KeyColor = keyColor;
            KeyThreshold = keyThreshold;
        }
    }

    // ── Inspector ─────────────────────────────────────────────────────────────
    [Header("SD Client")]
    public StableDiffusion sd;

    [Header("Checkpoint")]
    [Tooltip("A1111 checkpoint name — must match exactly what the WebUI shows.")]
    public string checkpoint = "dreamshaper_8";

    [Header("Quality (steps per asset)")]
    [Min(1)] public int backgroundSteps = 20;
    [Min(1)] public int tileSteps       = 20;
    [Min(1)] public int spriteSteps     = 20;
    [Min(1)] public int pickupSteps     = 15;

    [Header("Sizes")]
    public int bgWidth    = 1024;
    public int bgHeight   = 512;
    public int tileWidth  = 512;
    public int tileHeight = 512;
    [Tooltip("Player/enemy texture dimensions. Stable diffusion cannot generate good images at 128px.")]
    public int playerWidth  = 512;
    public int playerHeight = 512;
    public int enemyWidth   = 512;
    public int enemyHeight  = 512;
    public int pickupSize   = 512;

    [Header("Debug Mode")]
    [Tooltip("Enable to see per-step Console output.")]
    public bool enableDebugLogs = true;

    [Header("Background Removal")]
    [SerializeField] private RembgClient rembgClient;
    [SerializeField] private bool useRembgForSpriteCleanup = true;

    private bool _rembgMissingLogged;

    // ── State ─────────────────────────────────────────────────────────────────
    public static bool IsGenerating { get; private set; }

    /// <summary>Force-reset the static IsGenerating flag. Kept for compatibility — prefer <see cref="CancelActiveGeneration"/>.</summary>
    public static void IsGeneratingPublicReset() => IsGenerating = false;

    private int _generationVersion = 0;
    private bool IsStaleGeneration(int version) => version != _generationVersion;

    /// <summary>
    /// Cancel any active generation coroutine. The running coroutine will exit at its next
    /// staleness check and will not invoke <see cref="OnBundleReady"/> with stale results.
    /// </summary>
    public void CancelActiveGeneration()
    {
        _generationVersion++;
        IsGenerating = false;
    }

    // Theme set by MainMenu via PlayerPrefs
    [HideInInspector] public string theme = "forest";

    // ── PlayerPrefs keys ──────────────────────────────────────────────────────
    public const string ThemePrefKey = "AI_Theme";
    public const string PlanPrefKey  = "AI_LevelPlan";

    // ── Unity lifecycle ───────────────────────────────────────────────────────

    private void Awake()
    {
        if (sd == null) sd = GetComponent<StableDiffusion>();
        if (sd == null) sd = FindFirstObjectByType<StableDiffusion>();
        if (rembgClient == null) rembgClient = FindFirstObjectByType<RembgClient>();

        // Read theme saved by MainMenu
        string savedTheme = PlayerPrefs.GetString(ThemePrefKey, "");
        if (!string.IsNullOrWhiteSpace(savedTheme))
            theme = savedTheme;
    }

    private void OnDisable()
    {
        // Prevent IsGenerating from staying stuck across scene reloads
        IsGenerating = false;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Start generating all level textures.
    /// Reads the <see cref="LevelPlan"/> from PlayerPrefs (set by MainMenu → Ollama).
    /// Falls back to <see cref="FallbackPlan.For"/> when no plan is available.
    /// </summary>
    public void Generate()
    {
        if (IsGenerating)
        {
            Debug.LogWarning("[AiLevelPipeline] Already generating — call ignored.");
            return;
        }

        StableDiffusion.ClearCache();
        _generationVersion++;
        int version = _generationVersion;
        StartCoroutine(GenerateCoroutine(null, null, null, fireGlobalEvents: true, version: version));
    }

    /// <summary>
    /// Generate a bundle from a specific pre-built plan without touching PlayerPrefs.
    /// Used for background pre-generation of the next level.
    /// </summary>
    public bool GenerateFromPlan(LevelPlan plan,
                                 Action<LevelBundle> onReady,
                                 Action<string> onFailed)
    {
        if (IsGenerating)
        {
            Debug.LogWarning("[AiLevelPipeline] Already generating — pre-generation request ignored.");
            return false;
        }

        if (plan == null || !plan.IsValid)
        {
            onFailed?.Invoke("Invalid explicit LevelPlan for GenerateFromPlan.");
            return false;
        }

        StableDiffusion.ClearCache();
        _generationVersion++;
        int version = _generationVersion;
        StartCoroutine(GenerateCoroutine(plan, onReady, onFailed, fireGlobalEvents: false, version: version));
        return true;
    }

    // ── Core coroutine ────────────────────────────────────────────────────────

    private IEnumerator GenerateCoroutine(LevelPlan explicitPlan,
                                          Action<LevelBundle> onReady,
                                          Action<string> onFailed,
                                          bool fireGlobalEvents,
                                          int version = 0)
    {
        IsGenerating = true;
        ReportProgress(0.02f, "Preparing generation...");

        // ── 1. Load plan ───────────────────────────────────────────────────
        LevelPlan plan = explicitPlan ?? LoadPlan();
        Log($"=== AiLevelPipeline START  theme=\"{theme}\" ===");
        Log($"Plan source: {(plan.IsValid ? "Ollama" : "Fallback")}");

        if (sd == null)
        {
            Debug.LogError("[AiLevelPipeline] StableDiffusion not assigned! " +
                           "Add a StableDiffusion component or wire it in the Inspector.");
            IsGenerating = false;
            if (fireGlobalEvents) OnGenerationFailed?.Invoke("StableDiffusion not assigned");
            onFailed?.Invoke("StableDiffusion not assigned");
            yield break;
        }

        var bundle = new LevelBundle();

        var metrics = new Metrics.GenerationMetrics
        {
            theme      = theme,
            levelIndex = Game.GameSessionState.CurrentLevelIndex,
            runSeed    = Game.GameSessionState.RunSeed,
        };
        float _genStart = Time.realtimeSinceStartup;

        // ── 2. Background ──────────────────────────────────────────────────
        Log("[1/5] Background...");
        ReportProgress(0.10f, "Generating background...");
        float _t0 = Time.realtimeSinceStartup;
        yield return RequestTexture(
            plan.backgroundPrompt,
            plan.backgroundNegative,
            bgWidth, bgHeight, backgroundSteps,
            tiling: false,
            tex => bundle.background = tex);
        metrics.backgroundSeconds = Time.realtimeSinceStartup - _t0;
        metrics.backgroundOk = bundle.background != null;
        Log($"      → {Info(bundle.background)}");

        if (IsStaleGeneration(version)) { IsGenerating = false; yield break; }

        // ── 3. Ground tile ─────────────────────────────────────────────────
        Log("[2/4] Terrain tile...");
        ReportProgress(0.24f, "Generating terrain...");
        // tiling: false — A1111 seamless-tile mode produces diagonal stripe
        // artifacts on every theme. Unity tiles via WrapMode.Repeat instead.
        _t0 = Time.realtimeSinceStartup;
        string terrainPrompt = BuildStrictTerrainPrompt(plan);
        string terrainNegative = BuildStrictTerrainNegative(plan);

        yield return RequestTexture(
            terrainPrompt,
            terrainNegative,
            tileWidth, tileHeight, tileSteps,
            tiling: false,
            tex => bundle.terrainTile = tex,
            cfgScale: 6.8f);

        // If first terrain pass fails or looks scene-like, run stricter retries before fallback.
        if (!IsAcceptableTerrainTexture(bundle.terrainTile))
        {
            metrics.terrainRetryCount++;
            string retryPrompt =
                terrainPrompt +
                ", uniform stochastic material grain, non-directional micro-detail, smaller surface detail, no large shapes, no focal point";
            string retryNegative =
                terrainNegative +
                ", scenery, landscape composition, large object, central object, repeated icon, diagonal stripe, tile grid";

            Log("      terrain: first pass weak/scene-like, running strict retry...");
            yield return RequestTexture(
                retryPrompt,
                retryNegative,
                tileWidth, tileHeight, Mathf.Max(tileSteps + 6, 26),
                tiling: false,
                tex => bundle.terrainTile = tex,
                cfgScale: 7.8f);
        }

        if (!IsAcceptableTerrainTexture(bundle.terrainTile))
        {
            metrics.terrainRetryCount++;
            string safeTheme = string.IsNullOrWhiteSpace(Game.GameSessionState.BaseTheme) ? theme : Game.GameSessionState.BaseTheme;
            string emergencyPrompt =
                $"terrain material microtexture sample, inspired by {safeTheme} colors, uniform stochastic grain, non-directional fine detail, top-down close-up, full frame fill, even lighting, no focal point, no scene composition, no map view, no coastline, no forest canopy, no roads, no paths, no horizon, no islands, no channels, no trees, no leaves";
            string emergencyNegative =
                "photo, photorealistic scene, aerial photo, drone shot, satellite view, map view, coastline, shoreline, beach photo, ocean waves, lagoon, island, river delta, forest canopy, treetops, leaves, branches, road network, path intersection, crossroads, perspective, vanishing point, diagonal stripes, central emblem, radial pattern, starburst, mandala, logo, text, watermark";

            Log("      terrain: strict retry still weak/scene-like, running emergency microtexture retry...");
            yield return RequestTexture(
                emergencyPrompt,
                emergencyNegative,
                tileWidth, tileHeight, Mathf.Max(tileSteps + 8, 28),
                tiling: false,
                tex => bundle.terrainTile = tex,
                cfgScale: 8.2f);
        }

        if (!IsAcceptableTerrainTexture(bundle.terrainTile))
        {
            Log("      terrain: all retries still weak/scene-like -> letting LevelAssembler use themed fallback tile.");
            bundle.terrainTile = null;
        }
        metrics.terrainSeconds = Time.realtimeSinceStartup - _t0;
        metrics.terrainOk = bundle.terrainTile != null;
        metrics.terrainFallbackUsed = bundle.terrainTile == null;
        Log($"      → {Info(bundle.terrainTile)}");

        if (IsStaleGeneration(version)) { IsGenerating = false; yield break; }

        // ── 4. Player skin ─────────────────────────────────────────────────
        _t0 = Time.realtimeSinceStartup;
        if (Game.GameSessionState.TryGetLockedPlayerSkin(out var lockedPlayerSkin))
        {
            Log("[3/4] Player skin... reusing locked run character.");
            bundle.playerSkin = lockedPlayerSkin;
            ReportProgress(0.48f, "Player skin ready.");
        }
        else
        {
            Log("[3/4] Player skin...");
            ReportProgress(0.38f, "Generating player...");
            yield return RequestSpriteWithBackgroundRetries(
                label: "player",
                basePrompt: plan.playerPrompt,
                baseNegative: plan.playerNegative,
                width: playerWidth,
                height: playerHeight,
                steps: spriteSteps,
                onDone: tex => bundle.playerSkin = tex);

            // Rare startup hiccup: first generation can miss player skin while later requests succeed.
            // Do one emergency relaxed retry before accepting null.
            if (bundle.playerSkin == null)
            {
                Log("      player: primary attempts failed, running emergency retry...");
                yield return RequestSpriteEmergencyFallback(
                    normalizedTheme: Game.GameSessionState.BaseTheme,
                    steps: Mathf.Max(spriteSteps, 22),
                    onDone: tex => bundle.playerSkin = tex);
            }

            // Final safety: never allow a null player texture in gameplay.
            if (bundle.playerSkin == null)
            {
                Log("      player: emergency retry failed, using procedural fallback skin.");
                bundle.playerSkin = BuildProceduralPlayerFallback(playerWidth, playerHeight);
                metrics.playerFallbackUsed = true;
                metrics.fallbackCount++;
            }

            if (bundle.playerSkin != null)
                Game.GameSessionState.StoreLockedPlayerSkin(bundle.playerSkin);
        }
        metrics.playerSeconds = Time.realtimeSinceStartup - _t0;
        metrics.playerOk = bundle.playerSkin != null;
        metrics.playerRetryCount = bundle.playerSkin == null ? 2 : (metrics.playerFallbackUsed ? 2 : 0);
        Log($"      → {Info(bundle.playerSkin)}");

        if (IsStaleGeneration(version)) { IsGenerating = false; yield break; }

        string groundPrompt = string.IsNullOrWhiteSpace(plan.enemyPrompt)
            ? $"{Game.GameSessionState.BaseTheme}, one single chunky ground monster creature, full body visible, centered, side view, stylized cartoon 2D game sprite, isolated on pure white background"
            : plan.enemyPrompt;
        string groundNegative = string.IsNullOrWhiteSpace(plan.enemyNegative)
            ? "human, person, realistic anatomy, multiple enemies, duplicates, sprite sheet, reference sheet, background, scenery, cropped, text, watermark"
            : plan.enemyNegative;

        string flyingPrompt = string.IsNullOrWhiteSpace(plan.flyingEnemyPrompt)
            ? groundPrompt + ", airborne, wings visible, clearly flying silhouette"
            : plan.flyingEnemyPrompt;
        string flyingNegative = string.IsNullOrWhiteSpace(plan.flyingEnemyNegative)
            ? groundNegative + ", no wings, grounded stance"
            : plan.flyingEnemyNegative;

        string shootingPrompt = string.IsNullOrWhiteSpace(plan.shootingEnemyPrompt)
            ? groundPrompt + ", ranged attacker, weapon visible in hands, aiming stance"
            : plan.shootingEnemyPrompt;
        string shootingNegative = string.IsNullOrWhiteSpace(plan.shootingEnemyNegative)
            ? groundNegative + ", unarmed"
            : plan.shootingEnemyNegative;

        string bossPrompt = string.IsNullOrWhiteSpace(plan.bossEnemyPrompt)
            ? groundPrompt + ", giant boss creature, imposing and terrifying silhouette"
            : plan.bossEnemyPrompt;
        string bossNegative = string.IsNullOrWhiteSpace(plan.bossEnemyNegative)
            ? groundNegative + ", tiny creature"
            : plan.bossEnemyNegative;

        // ── 6a. Ground enemy skin ───────────────────────────────────────────
        Log("[4/4a] Ground enemy skin...");
        ReportProgress(0.52f, "Generating ground enemy...");
        _t0 = Time.realtimeSinceStartup;
        yield return RequestSpriteWithBackgroundRetries(
            label: "ground-enemy",
            basePrompt: groundPrompt,
            baseNegative: groundNegative,
            width: enemyWidth,
            height: enemyHeight,
            steps: spriteSteps,
            onDone: tex => bundle.groundEnemySkin = tex);
        if (bundle.groundEnemySkin == null)
        {
            bundle.groundEnemySkin = BuildProceduralEnemyFallback(enemyWidth, enemyHeight, "ground");
            metrics.groundEnemyFallbackUsed = true;
            metrics.fallbackCount++;
        }
        metrics.groundEnemySeconds = Time.realtimeSinceStartup - _t0;
        metrics.groundEnemyOk = bundle.groundEnemySkin != null;
        Log($"      → {Info(bundle.groundEnemySkin)}");

        if (IsStaleGeneration(version)) { IsGenerating = false; yield break; }

        // ── 6b. Flying enemy skin ───────────────────────────────────────────
        Log("[4/4b] Flying enemy skin...");
        ReportProgress(0.62f, "Generating flying enemy...");
        _t0 = Time.realtimeSinceStartup;
        yield return RequestSpriteWithBackgroundRetries(
            label: "flying-enemy",
            basePrompt: flyingPrompt,
            baseNegative: flyingNegative,
            width: enemyWidth,
            height: enemyHeight,
            steps: spriteSteps,
            onDone: tex => bundle.flyingEnemySkin = tex);
        if (bundle.flyingEnemySkin == null)
        {
            bundle.flyingEnemySkin = BuildProceduralEnemyFallback(enemyWidth, enemyHeight, "flying");
            metrics.flyingEnemyFallbackUsed = true;
            metrics.fallbackCount++;
        }
        metrics.flyingEnemySeconds = Time.realtimeSinceStartup - _t0;
        metrics.flyingEnemyOk = bundle.flyingEnemySkin != null;
        Log($"      → {Info(bundle.flyingEnemySkin)}");

        if (IsStaleGeneration(version)) { IsGenerating = false; yield break; }

        // ── 6c. Shooting enemy skin ─────────────────────────────────────────
        Log("[4/4c] Shooting enemy skin...");
        ReportProgress(0.72f, "Generating shooting enemy...");
        _t0 = Time.realtimeSinceStartup;
        yield return RequestSpriteWithBackgroundRetries(
            label: "shooting-enemy",
            basePrompt: shootingPrompt,
            baseNegative: shootingNegative,
            width: enemyWidth,
            height: enemyHeight,
            steps: spriteSteps,
            onDone: tex => bundle.shootingEnemySkin = tex);
        if (bundle.shootingEnemySkin == null)
        {
            bundle.shootingEnemySkin = BuildProceduralEnemyFallback(enemyWidth, enemyHeight, "shooting");
            metrics.shootingEnemyFallbackUsed = true;
            metrics.fallbackCount++;
        }
        metrics.shootingEnemySeconds = Time.realtimeSinceStartup - _t0;
        metrics.shootingEnemyOk = bundle.shootingEnemySkin != null;
        Log($"      → {Info(bundle.shootingEnemySkin)}");

        if (IsStaleGeneration(version)) { IsGenerating = false; yield break; }

        // ── 6d. Boss enemy skin (level 5) ──────────────────────────────────
        bool bossLevel = Game.GameSessionState.CurrentLevelIndex >= 5;
        if (bossLevel)
        {
            Log("[4/4d] Boss enemy skin...");
            ReportProgress(0.78f, "Generating boss...");
            _t0 = Time.realtimeSinceStartup;
            yield return RequestSpriteWithBackgroundRetries(
                label: "boss-enemy",
                basePrompt: bossPrompt,
                baseNegative: bossNegative,
                width: enemyWidth,
                height: enemyHeight,
                steps: Mathf.Max(spriteSteps, 20),
                onDone: tex => bundle.bossEnemySkin = tex);
            if (bundle.bossEnemySkin == null)
            {
                bundle.bossEnemySkin = BuildProceduralEnemyFallback(enemyWidth, enemyHeight, "shooting");
                metrics.bossFallbackUsed = true;
                metrics.fallbackCount++;
            }
            metrics.bossSeconds = Time.realtimeSinceStartup - _t0;
            metrics.bossOk = bundle.bossEnemySkin != null;
            Log($"      → {Info(bundle.bossEnemySkin)}");
        }

        if (IsStaleGeneration(version)) { IsGenerating = false; yield break; }

        // ── 6e. Shooting projectile skin ───────────────────────────────────
        Log("[4/4e] Shooting projectile skin...");
        ReportProgress(0.80f, "Generating projectile...");
        string projectileTheme = string.IsNullOrWhiteSpace(Game.GameSessionState.BaseTheme)
            ? theme
            : Game.GameSessionState.BaseTheme;
        string projectilePrompt =
            $"{projectileTheme}, one single glowing round energy orb projectile, perfect circular silhouette, centered, 2D game icon sprite, isolated on pure white background";
        string projectileNegative =
            "human, person, face, body, character, creature, weapon holder, hand, bullet casing, gun, multiple projectiles, sprite sheet, reference sheet, background, scenery, cropped, text, watermark";

        _t0 = Time.realtimeSinceStartup;
        yield return RequestSpriteWithBackgroundRetries(
            label: "shooting-projectile",
            basePrompt: projectilePrompt,
            baseNegative: projectileNegative,
            width: 256,
            height: 256,
            steps: Mathf.Max(16, pickupSteps),
            onDone: tex => bundle.shootingProjectileSkin = tex,
            cropMinRatio: 0.02f);

        if (bundle.shootingProjectileSkin == null)
        {
            bundle.shootingProjectileSkin = BuildProceduralProjectileFallback(128, projectileTheme);
            metrics.projectileFallbackUsed = true;
            metrics.fallbackCount++;
        }
        metrics.projectileSeconds = Time.realtimeSinceStartup - _t0;
        metrics.projectileOk = bundle.shootingProjectileSkin != null;
        Log($"      → {Info(bundle.shootingProjectileSkin)}");

        if (IsStaleGeneration(version)) { IsGenerating = false; yield break; }

        // ── Pickup skin ───────────────────────────────────────────────────
        Log("[4/4] Pickup skin...");
        ReportProgress(0.88f, "Generating pickups...");
        _t0 = Time.realtimeSinceStartup;
        yield return RequestSpriteWithBackgroundRetries(
            label: "pickup",
            basePrompt: plan.pickupPrompt,
            baseNegative: plan.pickupNegative,
            width: pickupSize,
            height: pickupSize,
            steps: pickupSteps,
            onDone: tex => bundle.pickupSkin = tex,
            cropMinRatio: 0.02f);

        if (bundle.pickupSkin == null)
        {
            Log("      pickup: primary attempts failed, running emergency retry...");
            yield return RequestPickupEmergencyFallback(
                normalizedTheme: Game.GameSessionState.BaseTheme,
                steps: Mathf.Max(pickupSteps, 20),
                onDone: tex => bundle.pickupSkin = tex);
        }

        if (bundle.pickupSkin == null)
        {
            Log("      pickup: emergency retry failed, using procedural fallback icon.");
            bundle.pickupSkin = BuildProceduralPickupFallback(pickupSize);
            metrics.pickupFallbackUsed = true;
            metrics.fallbackCount++;
        }
        metrics.pickupSeconds = Time.realtimeSinceStartup - _t0;
        metrics.pickupOk = bundle.pickupSkin != null;
        Log($"      → {Info(bundle.pickupSkin)}");

        // ── Finalize metrics ────────────────────────────────────────
        metrics.totalGenerationSeconds = Time.realtimeSinceStartup - _genStart;
        metrics.generationSucceeded = bundle.background != null ||
            bundle.terrainTile != null || bundle.playerSkin != null;
        bundle.metrics = metrics;

        // ── Done ───────────────────────────────────────────────────────────
        IsGenerating = false;

        bool anySuccess = bundle.background != null ||
                  bundle.terrainTile != null ||
                  bundle.playerSkin != null ||
                  bundle.groundEnemySkin != null ||
                  bundle.flyingEnemySkin != null ||
                  bundle.shootingEnemySkin != null ||
                  bundle.bossEnemySkin != null ||
                  bundle.shootingProjectileSkin != null ||
                  bundle.pickupSkin != null;

        if (!anySuccess)
        {
            ReportProgress(1f, "Generation failed.");
            Debug.LogError("[AiLevelPipeline] ALL textures failed. " +
                           $"Is Stable Diffusion running at {sd.baseUrl}?");
            if (fireGlobalEvents) OnGenerationFailed?.Invoke("All SD requests failed — is A1111 running?");
            onFailed?.Invoke("All SD requests failed — is A1111 running?");
        }
        else
        {
            ReportProgress(1f, "Generation complete.");
            Log("=== AiLevelPipeline DONE — bundle ready ===");
            if (fireGlobalEvents) OnBundleReady?.Invoke(bundle);
            onReady?.Invoke(bundle);
        }
    }

    private static void ReportProgress(float value, string stage)
    {
        OnGenerationProgress?.Invoke(Mathf.Clamp01(value), stage ?? "Generating...");
    }

    // ── Plan loading ──────────────────────────────────────────────────────────

    private LevelPlan LoadPlan()
    {
        string json = PlayerPrefs.GetString(PlanPrefKey, "");
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                var plan = JsonUtility.FromJson<LevelPlan>(json);
                if (plan != null && plan.IsValid)
                {
                    Log("Loaded LevelPlan from PlayerPrefs (Ollama).");
                    return plan;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AiLevelPipeline] Could not parse stored plan: {ex.Message}");
            }
        }

        Log($"Using fallback plan for theme \"{theme}\".");
        string baseTheme = Game.GameSessionState.BaseTheme;
        int levelIndex = Game.GameSessionState.CurrentLevelIndex;
        int runSeed = Game.GameSessionState.RunSeed;

        if (string.IsNullOrWhiteSpace(baseTheme))
            baseTheme = theme;

        return FallbackPlan.For(baseTheme, levelIndex, runSeed);
    }

    // ── Single SD request ─────────────────────────────────────────────────────

    private IEnumerator RequestTexture(string prompt, string negative,
                                       int width, int height, int steps,
                                       bool tiling,
                                       Action<Texture2D> onDone,
                                       float cfgScale = 7f)
    {
        // SD REQUIREMENT: Unity Inspector caches old values (like 128). Generating anything 
        // below 384-512 in SD creates compressed latent noise (the "chaos" you see). 
        // We MUST force a minimum of 512 here to override the Unity Editor's saved scene values.
        width  = Mathf.Max(512, RoundTo64(width));
        height = Mathf.Max(512, RoundTo64(height));
        steps  = Mathf.Max(20, steps);

        Log($"  SD request: \"{Truncate(prompt, 70)}\" | {width}x{height} steps={steps} tile={tiling} cfg={cfgScale}");

        var req = new A1111Txt2ImgRequest
        {
            prompt           = prompt,
            negative_prompt  = negative,
            width            = width,
            height           = height,
            steps            = steps,
            cfg_scale        = cfgScale,
            seed             = -1,
            sampler_name     = "DPM++ 2M Karras",
            tiling           = tiling,
            do_not_save_samples = true,
            do_not_save_grid    = true,
            save_images         = false,
            override_settings   = new A1111OverrideSettings
            {
                sd_model_checkpoint = checkpoint
            },
            override_settings_restore_afterwards = false
        };

        Texture2D result = null;
        string    error  = null;

        yield return sd.Txt2Img(req, tex => result = tex, e => error = e);

        if (error != null)
        {
            Debug.LogWarning($"[AiLevelPipeline] SD error (non-fatal, using null): {error}");
            onDone?.Invoke(null);
            yield break;
        }

        onDone?.Invoke(result);
    }

    // ── NEW SPRITE APPROACH ───────────────────────────────────────────────────
    // Single chroma color per attempt; high CFG forces background adherence.
    // No flood-fill RemoveBackground after color-key (it corrupts edges).
    // Two attempts: electric violet then hot magenta — both are rare in real subjects.
    private IEnumerator RequestSpriteWithBackgroundRetries(string label,
                                                           string basePrompt,
                                                           string baseNegative,
                                                           int width,
                                                           int height,
                                                           int steps,
                                                           Action<Texture2D> onDone,
                                                           float cropMinRatio = 0.12f)
    {
        basePrompt = StripBackgroundInstructions(basePrompt);
        baseNegative = baseNegative ?? "";
        var attempts = new[]
        {
            new SpriteBgAttempt(
                name: "violet",
                promptBg: "solid flat electric violet #7F00FF background",
                negativeBg: "white background, black background, green background, gray background, gradient background",
                keyColor: new Color32(127, 0, 255, 255),
                keyThreshold: 18),

            new SpriteBgAttempt(
                name: "magenta",
                promptBg: "solid flat hot magenta #FF00AA background",
                negativeBg: "white background, black background, green background, violet background, gradient background",
                keyColor: new Color32(255, 0, 170, 255),
                keyThreshold: 20),

            // Fallback: plain white — SD handles this most reliably for dark/complex subjects
            new SpriteBgAttempt(
                name: "white",
                promptBg: "isolated on plain solid pure white background",
                negativeBg: "black background, gray background, violet background, colored background, gradient background",
                keyColor: new Color32(255, 255, 255, 255),
                keyThreshold: 22),
        };

        const float spriteCfg = 9.5f;   // CFG 9.5: good background adherence without portrait-crop artifacts

        // Common hard negatives for both attempts
        const string hardNeg = ", multiple subjects, duplicates, twins, duo, group, sprite sheet, reference sheet, frame, border, split image, collage, text, watermark, scenery, environment, landscape, sky, beach, ocean, sea, waves, forest, jungle, city, room, outdoor, gradient background, radial gradient, vignette, circular background, soft blurred background, bokeh background, circle frame, shadow background, vignetted image, colored shadow, portrait, close-up, headshot, bust shot, face only, upper body only, cropped legs, cut off feet";

        Texture2D lastRaw = null;

        for (int i = 0; i < attempts.Length; i++)
        {
            SpriteBgAttempt a = attempts[i];

            // Subject stripped of location context to prevent scene generation.
            string subjectPrompt = StripSceneContext(basePrompt);

            string prompt =
                subjectPrompt +
                ", full body from head to toe, whole body visible, full length, " +
                a.PromptBg +
                ", one single subject only, centered, no scene, no environment, no background objects, no floor, no shadow, no glow, no backdrop panel, no frame, no square card";

            string negative = baseNegative + hardNeg + ", " + a.NegativeBg;

            Texture2D raw = null;
            yield return RequestTexture(
                prompt,
                negative,
                width,
                height,
                steps,
                tiling: false,
                tex => raw = tex,
                cfgScale: spriteCfg);

            if (raw == null)
            {
                Debug.LogWarning($"[AiLevelPipeline] {label}: attempt {i + 1}/{attempts.Length} ({a.Name}) returned NULL from SD.");
                continue;
            }

            lastRaw = raw;

            // ── Optional rembg pre-cleanup ─────────────────────────────────
            if (useRembgForSpriteCleanup)
            {
                if (rembgClient == null)
                    rembgClient = FindFirstObjectByType<RembgClient>();

                if (rembgClient != null && rembgClient.IsEnabled)
                {
                    Texture2D rembgTex = null;
                    string    rembgError = null;

                    yield return rembgClient.RemoveBackground(
                        raw,
                        tex => rembgTex = tex,
                        err => rembgError = err);

                    if (rembgTex != null && HasUsefulAlpha(rembgTex))
                    {
                        raw = rembgTex;
                        Log($"      {label}: rembg cleanup applied.");
                    }
                    else
                    {
                        string reason;
                        if (rembgTex != null)
                            reason = "rembg output has no useful alpha";
                        else if (!string.IsNullOrWhiteSpace(rembgError))
                            reason = rembgError;
                        else
                            reason = "unknown rembg failure";

                        Debug.LogWarning($"[AiLevelPipeline] {label}: rembg cleanup unavailable/failed: {reason}. Falling back to existing color-key cleanup.");
                    }
                }
                else if (!_rembgMissingLogged)
                {
                    _rembgMissingLogged = true;
                    Debug.LogWarning("[AiLevelPipeline] RembgClient not found. Using existing sprite cleanup only.");
                }
            }

            // Adaptive key: if corners are not the expected chroma color use estimated corner color
            Color32 effectiveKey = a.KeyColor;
            if (!AI.TextureUtils.CornersMostlyNearColor(raw, effectiveKey, threshold: a.KeyThreshold + 6, ratio: 0.55f))
                effectiveKey = AI.TextureUtils.EstimateCornerColor(raw);

            // Reject if almost all pixels are background (SD ignored the character entirely)
            if (AI.TextureUtils.IsMostlyNearColor(raw, effectiveKey, threshold: a.KeyThreshold + 12, ratio: 0.90f))
            {
                Debug.LogWarning($"[AiLevelPipeline] {label}: attempt {i + 1}/{attempts.Length} ({a.Name}) almost solid background — SD ignored character.");
                continue;
            }

            // 1. Remove chroma key with edge erosion
            AI.TextureUtils.RemoveBackgroundByColorKey(raw, effectiveKey, threshold: a.KeyThreshold + 12, edgeThreshold: a.KeyThreshold + 22, edgePasses: 2);

            // If corners are still opaque, SD likely returned gradient/off-white background.
            // Run a broader corner-key cleanup and only then fallback to flood-fill.
            if (AI.TextureUtils.HasOpaqueCorners(raw, alphaThreshold: 12, cornerSize: 12, opaqueRatio: 0.03f))
            {
                Color32 cornerKey = AI.TextureUtils.EstimateCornerColor(raw);
                AI.TextureUtils.RemoveBackgroundByColorKey(raw, cornerKey, threshold: 36, edgeThreshold: 52, edgePasses: 3);

                if (AI.TextureUtils.HasOpaqueCorners(raw, alphaThreshold: 12, cornerSize: 10, opaqueRatio: 0.02f))
                    AI.TextureUtils.RemoveBackground(raw);
            }

            // 2. Keep only the largest opaque region (remove duplicate characters)
            AI.TextureUtils.KeepDominantOpaqueComponent(raw);

            // 2.5 Residual corner cleanup for tinted/gradient cards behind the subject.
            AI.TextureUtils.RemoveResidualCornerBackground(raw, threshold: 42, edgeThreshold: 58, maxPasses: 2);
            AI.TextureUtils.KeepDominantOpaqueComponent(raw);

            // 3. Feather alpha edges for clean smooth borders
            AI.TextureUtils.FeatherAlphaEdges(raw, radius: 1);

            if (!AI.TextureUtils.HasOpaqueCoverage(raw, alphaThreshold: 12, minRatio: 0.02f, minPixels: 96))
            {
                Debug.LogWarning($"[AiLevelPipeline] {label}: attempt {i + 1}/{attempts.Length} ({a.Name}) removed too much foreground (coverage below 2%).");
                continue;
            }

            Texture2D cropped = AI.TextureUtils.CropToOpaqueBounds(raw) ?? raw;

            if (cropped.width < 18 || cropped.height < 18)
            {
                Debug.LogWarning($"[AiLevelPipeline] {label}: attempt {i + 1}/{attempts.Length} ({a.Name}) produced tiny sprite {cropped.width}x{cropped.height}.");
                continue;
            }

            // Soft full-card rejection: only reject if the crop is still almost the full image
            // AND corners are still opaque — meaning background removal failed entirely.
            float cropWidthRatio  = (float)cropped.width  / width;
            float cropHeightRatio = (float)cropped.height / height;
            bool almostFullSquare = cropWidthRatio > 0.90f && cropHeightRatio > 0.90f;
            bool cornersStillOpaque = AI.TextureUtils.HasOpaqueCorners(
                cropped, alphaThreshold: 12, cornerSize: 10, opaqueRatio: 0.02f);
            if (almostFullSquare && cornersStillOpaque)
            {
                Debug.LogWarning($"[AiLevelPipeline] {label}: attempt {i + 1}/{attempts.Length} ({a.Name}) rejected full square card/background after crop.");
                continue;
            }

            Log($"      {label}: attempt {i + 1}/{attempts.Length} ({a.Name}) succeeded -> {cropped.width}x{cropped.height}");
            onDone?.Invoke(cropped);
            yield break;
        }

        // All attempts failed validation — use the last raw texture rather than returning null.
        if (lastRaw != null)
        {
            Debug.LogWarning($"[AiLevelPipeline] {label} sprite failed all background-key attempts — using last raw texture as fallback.");
            onDone?.Invoke(lastRaw);
        }
        else
        {
            Debug.LogWarning($"[AiLevelPipeline] {label} sprite failed all background-key attempts (violet/magenta).");
            onDone?.Invoke(null);
        }
    }

    // ── Utilities ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns true if the texture has a meaningful alpha channel —
    /// some transparent pixels (background removed) and some opaque pixels (subject).
    /// Rejects fully opaque or fully transparent images.
    /// </summary>
    private static bool HasUsefulAlpha(Texture2D tex)
    {
        if (tex == null || !tex.isReadable) return false;

        Color32[] px = tex.GetPixels32();
        if (px == null || px.Length == 0) return false;

        int transparent = 0;
        int opaque      = 0;

        for (int i = 0; i < px.Length; i++)
        {
            if (px[i].a < 16)  transparent++;
            if (px[i].a > 220) opaque++;
        }

        float transparentRatio = (float)transparent / px.Length;
        float opaqueRatio      = (float)opaque      / px.Length;

        return transparentRatio > 0.05f && opaqueRatio > 0.01f;
    }

    private static int RoundTo64(int v) => Mathf.Max(64, Mathf.RoundToInt(v / 64f) * 64);

    private static bool IsAcceptableTerrainTexture(Texture2D tex)
    {
        if (tex == null || !tex.isReadable) return false;

        Color32[] px = tex.GetPixels32();
        if (px == null || px.Length == 0) return false;

        int minR = 255, minG = 255, minB = 255;
        int maxR = 0, maxG = 0, maxB = 0;
        int nearWhite = 0;

        for (int i = 0; i < px.Length; i++)
        {
            Color32 c = px[i];
            if (c.r < minR) minR = c.r; if (c.r > maxR) maxR = c.r;
            if (c.g < minG) minG = c.g; if (c.g > maxG) maxG = c.g;
            if (c.b < minB) minB = c.b; if (c.b > maxB) maxB = c.b;
            if (c.r >= 242 && c.g >= 242 && c.b >= 242) nearWhite++;
        }

        int channelRange = (maxR - minR) + (maxG - minG) + (maxB - minB);
        if (channelRange < 32) return false;

        float whiteRatio = (float)nearWhite / px.Length;
        if (whiteRatio > 0.45f) return false;

        if (LooksLikeTerrainScene(tex)) return false;
        if (HasStrongCenterFocalRegion(tex)) return false;

        return true;
    }

    private static bool LooksLikeTerrainScene(Texture2D tex)
    {
        Color top = AverageRegion(tex, 0f, 0f, 1f, 0.22f);
        Color mid = AverageRegion(tex, 0f, 0.39f, 1f, 0.61f);
        Color bot = AverageRegion(tex, 0f, 0.78f, 1f, 1f);

        float topMid = Mathf.Abs(top.r - mid.r) + Mathf.Abs(top.g - mid.g) + Mathf.Abs(top.b - mid.b);
        float midBot = Mathf.Abs(mid.r - bot.r) + Mathf.Abs(mid.g - bot.g) + Mathf.Abs(mid.b - bot.b);
        float topBot = Mathf.Abs(top.r - bot.r) + Mathf.Abs(top.g - bot.g) + Mathf.Abs(top.b - bot.b);

        // Raised thresholds: volcanic/lava tiles legitimately have high contrast
        // between dark basalt and glowing orange fissures — don't reject them.
        return topBot > 0.65f && (topMid > 0.28f || midBot > 0.28f);
    }

    private static bool HasStrongCenterFocalRegion(Texture2D tex)
    {
        Color center = AverageRegion(tex, 0.36f, 0.36f, 0.64f, 0.64f);
        Color edgeA = AverageRegion(tex, 0.00f, 0.00f, 0.18f, 0.18f);
        Color edgeB = AverageRegion(tex, 0.82f, 0.00f, 1.00f, 0.18f);
        Color edgeC = AverageRegion(tex, 0.00f, 0.82f, 0.18f, 1.00f);
        Color edgeD = AverageRegion(tex, 0.82f, 0.82f, 1.00f, 1.00f);
        Color edge = (edgeA + edgeB + edgeC + edgeD) * 0.25f;

        float colorDist = Mathf.Abs(center.r - edge.r) + Mathf.Abs(center.g - edge.g) + Mathf.Abs(center.b - edge.b);
        float lumCenter = center.r * 0.2126f + center.g * 0.7152f + center.b * 0.0722f;
        float lumEdge = edge.r * 0.2126f + edge.g * 0.7152f + edge.b * 0.0722f;
        float lumDist = Mathf.Abs(lumCenter - lumEdge);

        // Raised thresholds: material tiles with a central bright crack/fissure
        // should not be rejected as "focal point scenes".
        return colorDist > 0.75f && lumDist > 0.22f;
    }

    private static Color AverageRegion(Texture2D tex, float u0, float v0, float u1, float v1)
    {
        int w = tex.width;
        int h = tex.height;
        if (w <= 0 || h <= 0) return Color.gray;

        int x0 = Mathf.Clamp(Mathf.FloorToInt(u0 * (w - 1)), 0, w - 1);
        int y0 = Mathf.Clamp(Mathf.FloorToInt(v0 * (h - 1)), 0, h - 1);
        int x1 = Mathf.Clamp(Mathf.CeilToInt(u1 * (w - 1)), x0, w - 1);
        int y1 = Mathf.Clamp(Mathf.CeilToInt(v1 * (h - 1)), y0, h - 1);

        float r = 0f, g = 0f, b = 0f;
        int count = 0;
        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                Color c = tex.GetPixel(x, y);
                r += c.r;
                g += c.g;
                b += c.b;
                count++;
            }
        }

        if (count <= 0) return Color.gray;
        return new Color(r / count, g / count, b / count, 1f);
    }

    private IEnumerator RequestPickupEmergencyFallback(string normalizedTheme,
                                                       int steps,
                                                       Action<Texture2D> onDone)
    {
        string safeTheme = string.IsNullOrWhiteSpace(normalizedTheme) ? "fantasy" : normalizedTheme.Trim().ToLowerInvariant();
        string prompt =
            $"one single collectible item symbol (theme: {safeTheme}), centered, clean silhouette, iconic shape, " +
            "uniform pure white background, no holder, no pedestal, no frame";
        string negative =
            "multiple items, sprite sheet, reference sheet, collage, background scene, landscape, character, creature, " +
            "text, logo, watermark, frame, border, pedestal, stand";

        Texture2D raw = null;
        yield return RequestTexture(
            prompt,
            negative,
            pickupSize,
            pickupSize,
            steps,
            tiling: false,
            tex => raw = tex,
            cfgScale: 9.0f);

        if (raw == null)
        {
            onDone?.Invoke(null);
            yield break;
        }

        AI.TextureUtils.RemoveBackgroundByColorKey(raw, new Color32(255, 255, 255, 255), threshold: 28, edgeThreshold: 44, edgePasses: 2);
        AI.TextureUtils.RemoveResidualCornerBackground(raw, threshold: 44, edgeThreshold: 60, maxPasses: 2);
        AI.TextureUtils.KeepDominantOpaqueComponent(raw);
        AI.TextureUtils.FeatherAlphaEdges(raw, radius: 1);

        Texture2D cropped = AI.TextureUtils.CropToOpaqueBounds(raw, alphaThreshold: 10, padding: 8);
        if (cropped != null &&
            AI.TextureUtils.HasOpaqueCoverage(cropped, alphaThreshold: 12, minRatio: 0.03f, minPixels: 48) &&
            !AI.TextureUtils.HasOpaqueCorners(cropped, alphaThreshold: 12, cornerSize: 8, opaqueRatio: 0.01f))
        {
            onDone?.Invoke(cropped);
            yield break;
        }

        onDone?.Invoke(null);
    }

    private IEnumerator RequestSpriteEmergencyFallback(string normalizedTheme,
                                                       int steps,
                                                       Action<Texture2D> onDone)
    {
        string safeTheme = string.IsNullOrWhiteSpace(normalizedTheme) ? "fantasy" : normalizedTheme.Trim().ToLowerInvariant();
        string prompt =
            $"one single humanoid protagonist (theme: {safeTheme}), full body visible, centered, side view, clean silhouette, " +
            "uniform pure white background, no floor, no shadows, no props, no frame, no card";
        string negative =
            "multiple characters, duplicates, twins, duo, group, sprite sheet, reference sheet, collage, " +
            "background scene, landscape, environment, text, logo, watermark, frame, border, pedestal, stand";

        Texture2D raw = null;
        yield return RequestTexture(
            prompt,
            negative,
            playerWidth,
            playerHeight,
            steps,
            tiling: false,
            tex => raw = tex,
            cfgScale: 9.5f);

        if (raw == null)
        {
            onDone?.Invoke(null);
            yield break;
        }

        AI.TextureUtils.RemoveBackgroundByColorKey(raw, new Color32(255, 255, 255, 255), threshold: 26, edgeThreshold: 42, edgePasses: 2);
        AI.TextureUtils.RemoveResidualCornerBackground(raw, threshold: 42, edgeThreshold: 58, maxPasses: 2);
        AI.TextureUtils.KeepDominantOpaqueComponent(raw);
        AI.TextureUtils.FeatherAlphaEdges(raw, radius: 1);

        Texture2D cropped = AI.TextureUtils.CropToOpaqueBounds(raw);
        if (cropped != null &&
            AI.TextureUtils.HasOpaqueCoverage(cropped, alphaThreshold: 12, minRatio: 0.03f, minPixels: 128) &&
            !AI.TextureUtils.HasOpaqueCorners(cropped, alphaThreshold: 12, cornerSize: 8, opaqueRatio: 0.01f))
        {
            onDone?.Invoke(cropped);
            yield break;
        }

        onDone?.Invoke(null);
    }

    private static Texture2D BuildProceduralPlayerFallback(int width, int height)
    {
        int w = Mathf.Clamp(width, 128, 512);
        int h = Mathf.Clamp(height, 128, 512);

        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
        tex.hideFlags = HideFlags.HideAndDontSave;
        tex.filterMode = FilterMode.Bilinear;
        tex.wrapMode = TextureWrapMode.Clamp;

        Color32 clear = new Color32(0, 0, 0, 0);
        Color32 body = new Color32(88, 173, 255, 255);
        Color32 accent = new Color32(36, 88, 170, 255);
        Color32 outline = new Color32(20, 30, 55, 255);

        var px = new Color32[w * h];
        for (int i = 0; i < px.Length; i++) px[i] = clear;

        int cx = Mathf.RoundToInt(w * 0.5f);
        int headR = Mathf.Max(6, Mathf.RoundToInt(w * 0.08f));
        int headCy = Mathf.RoundToInt(h * 0.78f);

        FillCircle(px, w, h, cx, headCy, headR, body);
        FillRect(px, w, h, cx - headR / 2, Mathf.RoundToInt(h * 0.42f), headR, Mathf.RoundToInt(h * 0.28f), body);
        FillRect(px, w, h, cx - headR - 2, Mathf.RoundToInt(h * 0.22f), headR - 1, Mathf.RoundToInt(h * 0.22f), accent);
        FillRect(px, w, h, cx + 3, Mathf.RoundToInt(h * 0.22f), headR - 1, Mathf.RoundToInt(h * 0.22f), accent);
        FillRect(px, w, h, cx - headR - 3, Mathf.RoundToInt(h * 0.48f), headR - 1, Mathf.RoundToInt(h * 0.18f), accent);
        FillRect(px, w, h, cx + 4, Mathf.RoundToInt(h * 0.48f), headR - 1, Mathf.RoundToInt(h * 0.16f), accent);

        // simple outline pass around opaque pixels
        var outPx = new Color32[px.Length];
        System.Array.Copy(px, outPx, px.Length);
        for (int y = 1; y < h - 1; y++)
        {
            for (int x = 1; x < w - 1; x++)
            {
                int idx = y * w + x;
                if (px[idx].a > 0) continue;
                bool near = px[idx - 1].a > 0 || px[idx + 1].a > 0 || px[idx - w].a > 0 || px[idx + w].a > 0;
                if (near) outPx[idx] = outline;
            }
        }

        tex.SetPixels32(outPx);
        tex.Apply(false, false);
        return tex;
    }

    private static Texture2D BuildProceduralPickupFallback(int size)
    {
        int s = Mathf.Clamp(size, 64, 256);
        var tex = new Texture2D(s, s, TextureFormat.RGBA32, false);
        tex.hideFlags = HideFlags.HideAndDontSave;
        tex.filterMode = FilterMode.Bilinear;
        tex.wrapMode = TextureWrapMode.Clamp;

        Color32 clear = new Color32(0, 0, 0, 0);
        Color32 fill = new Color32(255, 210, 64, 255);
        Color32 glow = new Color32(255, 240, 170, 255);
        Color32 outline = new Color32(140, 88, 12, 255);

        var px = new Color32[s * s];
        for (int i = 0; i < px.Length; i++) px[i] = clear;

        int cx = s / 2;
        int cy = s / 2;
        int r = Mathf.Max(10, Mathf.RoundToInt(s * 0.20f));

        // Diamond gem
        for (int y = 0; y < s; y++)
        {
            int dy = Mathf.Abs(y - cy);
            int half = Mathf.Max(0, r - dy);
            int x0 = Mathf.Max(0, cx - half);
            int x1 = Mathf.Min(s - 1, cx + half);
            for (int x = x0; x <= x1; x++)
                px[y * s + x] = fill;
        }

        // Inner glow
        int innerR = Mathf.Max(4, r - 4);
        for (int y = 0; y < s; y++)
        {
            int dy = Mathf.Abs(y - cy);
            int half = Mathf.Max(0, innerR - dy);
            int x0 = Mathf.Max(0, cx - half);
            int x1 = Mathf.Min(s - 1, cx + half);
            for (int x = x0; x <= x1; x++)
                px[y * s + x] = glow;
        }

        // Outline
        var outPx = new Color32[px.Length];
        System.Array.Copy(px, outPx, px.Length);
        for (int y = 1; y < s - 1; y++)
        {
            for (int x = 1; x < s - 1; x++)
            {
                int idx = y * s + x;
                if (px[idx].a > 0) continue;
                bool near = px[idx - 1].a > 0 || px[idx + 1].a > 0 || px[idx - s].a > 0 || px[idx + s].a > 0;
                if (near) outPx[idx] = outline;
            }
        }

        tex.SetPixels32(outPx);
        tex.Apply(false, false);
        return tex;
    }

    private static Texture2D BuildProceduralProjectileFallback(int size, string theme)
    {
        int s = Mathf.Clamp(size, 32, 128);
        var tex = new Texture2D(s, s, TextureFormat.RGBA32, false);
        tex.hideFlags = HideFlags.HideAndDontSave;
        tex.filterMode = FilterMode.Bilinear;
        tex.wrapMode = TextureWrapMode.Clamp;

        Color32 clear = new Color32(0, 0, 0, 0);
        Color32 core = new Color32(255, 150, 40, 255);
        Color32 rim = new Color32(255, 235, 160, 255);

        string t = string.IsNullOrWhiteSpace(theme) ? "" : theme.ToLowerInvariant();
        if (t.Contains("ice") || t.Contains("frost"))
        {
            core = new Color32(90, 180, 255, 255);
            rim = new Color32(210, 245, 255, 255);
        }
        else if (t.Contains("dark") || t.Contains("shadow"))
        {
            core = new Color32(140, 70, 220, 255);
            rim = new Color32(220, 170, 255, 255);
        }
        else if (t.Contains("nature") || t.Contains("forest"))
        {
            core = new Color32(80, 200, 100, 255);
            rim = new Color32(200, 250, 170, 255);
        }

        var px = new Color32[s * s];
        for (int i = 0; i < px.Length; i++) px[i] = clear;

        int cx = s / 2;
        int cy = s / 2;
        int rx = Mathf.RoundToInt(s * 0.28f);
        int ry = Mathf.RoundToInt(s * 0.16f);

        for (int y = 0; y < s; y++)
        {
            int dy = y - cy;
            for (int x = 0; x < s; x++)
            {
                int dx = x - cx;
                float n = (dx * dx) / (float)(rx * rx) + (dy * dy) / (float)(ry * ry);
                if (n <= 1f)
                {
                    float glow = Mathf.Clamp01(1f - n);
                    px[y * s + x] = Color32.Lerp(core, rim, glow);
                }
            }
        }

        tex.SetPixels32(px);
        tex.Apply(false, false);
        return tex;
    }

    private static void FillRect(Color32[] px, int w, int h, int x0, int y0, int rw, int rh, Color32 c)
    {
        int minX = Mathf.Clamp(x0, 0, w - 1);
        int minY = Mathf.Clamp(y0, 0, h - 1);
        int maxX = Mathf.Clamp(x0 + rw, 0, w);
        int maxY = Mathf.Clamp(y0 + rh, 0, h);
        for (int y = minY; y < maxY; y++)
            for (int x = minX; x < maxX; x++)
                px[y * w + x] = c;
    }

    private static void FillCircle(Color32[] px, int w, int h, int cx, int cy, int r, Color32 c)
    {
        int r2 = r * r;
        int minY = Mathf.Max(0, cy - r);
        int maxY = Mathf.Min(h - 1, cy + r);
        int minX = Mathf.Max(0, cx - r);
        int maxX = Mathf.Min(w - 1, cx + r);

        for (int y = minY; y <= maxY; y++)
        {
            int dy = y - cy;
            for (int x = minX; x <= maxX; x++)
            {
                int dx = x - cx;
                if (dx * dx + dy * dy <= r2)
                    px[y * w + x] = c;
            }
        }
    }

    private static string Info(Texture2D t)     => t != null ? $"{t.width}x{t.height} OK" : "NULL";
    private static string Truncate(string s, int n) => s?.Length > n ? s.Substring(0, n) + "…" : s ?? "";

    private static Texture2D BuildProceduralEnemyFallback(int width, int height, string enemyType)
    {
        var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
        tex.hideFlags = HideFlags.HideAndDontSave;

        var fill = new Color32(80, 120, 200, 255);    // Blue base
        var accent = new Color32(255, 200, 50, 255);   // Gold accent
        
        if (enemyType == "flying")
        {
            fill = new Color32(150, 100, 200, 255);    // Purple for flying
            accent = new Color32(255, 150, 200, 255);  // Light purple accent
        }
        else if (enemyType == "shooting")
        {
            fill = new Color32(180, 80, 80, 255);      // Red for shooting
            accent = new Color32(255, 150, 100, 255);  // Orange accent
        }

        var px = new Color32[width * height];
        for (int i = 0; i < px.Length; i++)
            px[i] = new Color32(255, 255, 255, 0);    // Transparent background

        // Draw a simple creature shape (rounded rectangle body + circle head)
        int bodyW = Mathf.RoundToInt(width * 0.35f);
        int bodyH = Mathf.RoundToInt(height * 0.45f);
        int bodyX = (width - bodyW) / 2;
        int bodyY = Mathf.RoundToInt(height * 0.35f);

        FillRect(px, width, height, bodyX, bodyY, bodyW, bodyH, fill);

        // Head (circle)
        int headR = Mathf.RoundToInt(width * 0.18f);
        int headX = width / 2;
        int headY = Mathf.RoundToInt(height * 0.22f);
        FillCircle(px, width, height, headX, headY, headR, fill);

        // Eyes (accent circles)
        int eyeR = Mathf.Max(2, headR / 3);
        int eyeY = headY - headR / 4;
        int eyeX1 = headX - headR / 2;
        int eyeX2 = headX + headR / 2;
        FillCircle(px, width, height, eyeX1, eyeY, eyeR, accent);
        FillCircle(px, width, height, eyeX2, eyeY, eyeR, accent);

        tex.SetPixels32(px);
        tex.Apply(false, false);
        return tex;
    }

    // ── Terrain prompt builders ────────────────────────────────────────────────

    private string BuildStrictTerrainPrompt(LevelPlan plan)
    {
        string safeTheme = string.IsNullOrWhiteSpace(Game.GameSessionState.BaseTheme)
            ? theme
            : Game.GameSessionState.BaseTheme;

        string material = string.IsNullOrWhiteSpace(plan.terrainMaterial)
            ? "durable stone composite platform material"
            : plan.terrainMaterial.Trim();

        string surface = string.IsNullOrWhiteSpace(plan.terrainSurface)
            ? "rough weathered cracked"
            : plan.terrainSurface.Trim();

        string palette = string.IsNullOrWhiteSpace(plan.terrainPalette)
            ? $"{safeTheme} inspired colors"
            : plan.terrainPalette.Trim();

        return $"{material}, {surface} surface, {palette}, " +
               "physical 2D platform surface material, full frame flat texture sample, " +
               "top-down close-up surface, non-directional micro detail, small irregular details, " +
               "no large shapes, no focal point, no scene, no landscape, no horizon, " +
               "no perspective, no objects, no characters";
    }

    private string BuildStrictTerrainNegative(LevelPlan plan)
    {
        string hardNegative =
            "scene, landscape, background scenery, horizon, sky, perspective, vanishing point, " +
            "aerial view, map view, road, path, river, coastline, island, mountain, forest canopy, " +
            "trees, leaves, branches, building, room, character, creature, face, animal, vehicle, " +
            "object, icon, logo, text, watermark, frame, border, large central object, diagonal band, " +
            "side view platform, floor with depth, 3d render, " +
            "checkerboard, chess pattern, regular grid, grid pattern, alternating squares, " +
            "mosaic grid, tile grid, repeating square blocks, uniform square cells";

        if (!string.IsNullOrWhiteSpace(plan.groundNegative))
            return plan.groundNegative + ", " + hardNegative;

        return hardNegative;
    }

    // ── Sprite prompt sanitizers ──────────────────────────────────────────────

    private static string StripBackgroundInstructions(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return "";

        string p = prompt;

        string[] remove =
        {
            "isolated on pure white background",
            "pure white background",
            "white background",
            "uniform flat solid-color background",
            "flat solid-color background",
            "solid-color background",
            "transparent background",
            "plain background"
        };

        foreach (string r in remove)
            p = p.Replace(r, "", StringComparison.OrdinalIgnoreCase);

        return p.Trim().Trim(',', '.', ' ');
    }

    /// <summary>
    /// Strips location/scene-setting phrases that cause SD to render a full environment
    /// instead of an isolated character on a solid background.
    /// </summary>
    private static string StripSceneContext(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return "";
        string p = prompt;

        string[] remove =
        {
            "with background",
            "background scene",
            "full scene",
            "with scenery",
            "scenic view",
            "panoramic",
            "portrait",
            "headshot",
            "profile picture",
            "character portrait",
            "card art",
            "trading card",
            "icon card",
            "with floor",
            "on floor",
            "standing on platform",
            "with shadow",
            "with backdrop",
        };

        foreach (string r in remove)
            p = p.Replace(r, "", StringComparison.OrdinalIgnoreCase);

        return p.Trim().Trim(',', '.', ' ');
    }

    private void Log(string msg)
    {
        if (enableDebugLogs) Debug.Log($"[AiLevelPipeline] {msg}");
    }
}
