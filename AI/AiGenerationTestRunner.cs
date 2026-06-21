using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// Batch test runner for AI generation and artifact export.
///
/// What it does:
/// 1) Runs generation for selected themes.
/// 2) Measures plan time, generation time, and total time.
/// 3) Exports generated textures as PNG files.
/// 4) Saves per-run metadata JSON and summary CSV.
///
/// Export location:
///   <UnityProjectRoot>/GeneratedTestOutputs/
/// </summary>
public class AiGenerationTestRunner : MonoBehaviour
{
    [Header("References")]
    public AiLevelPipeline pipeline;
    public OllamaClient ollamaClient;

    [Header("Test Setup")]
    [Tooltip("When enabled, batch starts automatically on scene start.")]
    public bool autoRunOnStart = false;

    [Tooltip("Use Ollama for plan generation. If disabled/unavailable, FallbackPlan is used.")]
    public bool useOllama = true;

    [Range(1, 5)]
    [Tooltip("Level index used for generated plans (boss is generated only on level 5).")]
    public int testLevelIndex = 1;

    [Min(1)]
    [Tooltip("How many runs per theme.")]
    public int runsPerTheme = 1;

    [Min(10f)]
    [Tooltip("Timeout for one full generation run.")]
    public float generationTimeoutSeconds = 240f;

    [Tooltip("Themes to test.")]
    public string[] themes = new[]
    {
        "Cyberpunk city",
        "Enchanted forest",
        "Ice cave",
        "Desert ruins",
        "Alien planet",
        "Haunted castle",
        "Volcanic dungeon",
        "Underwater temple",
        "Candy world",
        "Robot factory"
    };

    [Header("Export")]
    [Tooltip("Folder in project root where all test outputs will be written.")]
    public string exportRootFolderName = "GeneratedTestOutputs";

    [Tooltip("Write PNG files for every generated texture.")]
    public bool exportPngFiles = true;

    [Tooltip("Write metadata JSON for each run.")]
    public bool exportMetadataJson = true;

    [Tooltip("Write one CSV summary for the whole batch.")]
    public bool exportSummaryCsv = true;

    [Tooltip("Open export folder after test finishes (Editor/Standalone).")]
    public bool openFolderWhenDone = false;

    private bool _isRunning;

    [Serializable]
    private class RunMetrics
    {
        public string batchId;
        public string runId;
        public string theme;
        public string effectiveTheme;
        public int levelIndex;
        public int runSeed;

        public bool usedFallbackPlan;
        public bool generationSucceeded;
        public string generationError;

        public float planSeconds;
        public float generationSeconds;
        public float totalSeconds;

        public bool backgroundOk;
        public bool terrainOk;
        public bool playerOk;
        public bool groundEnemyOk;
        public bool flyingEnemyOk;
        public bool shootingEnemyOk;
        public bool bossEnemyOk;
        public bool projectileOk;
        public bool pickupOk;

        public int okCount;
        public int expectedCount;

        // ── Per-asset timing (from bundle.metrics) ────────────────────────
        public float backgroundSeconds;
        public float terrainSeconds;
        public float playerSeconds;
        public float groundEnemySeconds;
        public float flyingEnemySeconds;
        public float shootingEnemySeconds;
        public float bossSeconds;
        public float projectileSeconds;
        public float pickupSeconds;
        public float totalGenerationSeconds;

        // ── Fallback flags ────────────────────────────────────────────────
        public bool terrainFallbackUsed;
        public bool playerFallbackUsed;
        public bool groundEnemyFallbackUsed;
        public bool flyingEnemyFallbackUsed;
        public bool shootingEnemyFallbackUsed;
        public bool bossFallbackUsed;
        public bool projectileFallbackUsed;
        public bool pickupFallbackUsed;

        // ── Retry counts ──────────────────────────────────────────────────
        public int terrainRetryCount;
        public int playerRetryCount;
        public int spriteRetryCount;
        public int fallbackCount;

        public string outputFolder;
    }

    [Serializable]
    private class RunMetadata
    {
        public RunMetrics metrics;
        public LevelPlan plan;
    }

    private void Awake()
    {
        if (pipeline == null) pipeline = FindFirstObjectByType<AiLevelPipeline>();
        if (ollamaClient == null) ollamaClient = FindFirstObjectByType<OllamaClient>();
    }

    private void Start()
    {
        if (autoRunOnStart)
            RunBatchFromInspector();
    }

    [ContextMenu("AI Tests/Run Batch")]
    public void RunBatchFromInspector()
    {
        if (_isRunning)
        {
            Debug.LogWarning("[AiGenerationTestRunner] Batch already running.");
            return;
        }

        StartCoroutine(RunBatchCoroutine());
    }

    [ContextMenu("AI Tests/Print Export Folder")]
    public void PrintExportFolder()
    {
        string root = GetExportRootPath();
        Debug.Log("[AiGenerationTestRunner] Export root: " + root);
    }

    private IEnumerator RunBatchCoroutine()
    {
        if (pipeline == null)
        {
            Debug.LogError("[AiGenerationTestRunner] Missing AiLevelPipeline reference.");
            yield break;
        }

        if (themes == null || themes.Length == 0)
        {
            Debug.LogError("[AiGenerationTestRunner] No themes configured.");
            yield break;
        }

        _isRunning = true;

        string batchId = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string exportRoot = GetExportRootPath();
        string batchFolder = Path.Combine(exportRoot, "batch_" + batchId);
        Directory.CreateDirectory(batchFolder);

        Debug.Log("[AiGenerationTestRunner] Starting batch " + batchId);
        Debug.Log("[AiGenerationTestRunner] Output: " + batchFolder);

        var allMetrics = new List<RunMetrics>();

        int totalRuns = themes.Length * Mathf.Max(1, runsPerTheme);
        int currentRun = 0;

        for (int themeIndex = 0; themeIndex < themes.Length; themeIndex++)
        {
            string theme = string.IsNullOrWhiteSpace(themes[themeIndex]) ? "forest" : themes[themeIndex].Trim();

            for (int runIndex = 1; runIndex <= Mathf.Max(1, runsPerTheme); runIndex++)
            {
                currentRun++;
                Debug.Log($"[AiGenerationTestRunner] Run {currentRun}/{totalRuns}: theme=\"{theme}\", attempt={runIndex}");

                RunMetrics metrics = null;
                yield return RunSingleCase(batchId, batchFolder, theme, runIndex, m => metrics = m);
                if (metrics != null)
                    allMetrics.Add(metrics);
            }
        }

        if (exportSummaryCsv)
            WriteSummaryCsv(batchFolder, allMetrics);

        WriteAggregateSummaryCsv(batchFolder, allMetrics);
        WriteFailureCasesCsv(batchFolder, allMetrics);
        WriteBatchTestNotes(batchFolder, allMetrics, themes, runsPerTheme, testLevelIndex);

        int successCount = 0;
        int fallbackPlanCount = 0;
        float totalTime = 0f;
        for (int i = 0; i < allMetrics.Count; i++)
        {
            var r = allMetrics[i];
            if (r.generationSucceeded) successCount++;
            if (r.usedFallbackPlan) fallbackPlanCount++;
            totalTime += r.totalSeconds;
        }

        float avgTotal = allMetrics.Count > 0 ? totalTime / allMetrics.Count : 0f;
        Debug.Log($"[AiGenerationTestRunner] Success: {successCount}/{allMetrics.Count}");
        Debug.Log($"[AiGenerationTestRunner] Fallback plans: {fallbackPlanCount}/{allMetrics.Count}");
        Debug.Log($"[AiGenerationTestRunner] Average total time: {avgTotal:F1}s");

        Debug.Log($"[AiGenerationTestRunner] Batch done. Runs: {allMetrics.Count}");
        Debug.Log("[AiGenerationTestRunner] Export folder: " + batchFolder);

        if (openFolderWhenDone)
            Application.OpenURL("file://" + batchFolder.Replace("\\", "/"));

        _isRunning = false;
    }

    private IEnumerator RunSingleCase(string batchId,
                                      string batchFolder,
                                      string theme,
                                      int runAttempt,
                                      Action<RunMetrics> onDone)
    {
        float totalStart = Time.realtimeSinceStartup;

        Game.GameSessionState.ResetRun();
        Game.GameSessionState.BeginRun(theme);

        while (Game.GameSessionState.CurrentLevelIndex < testLevelIndex)
            Game.GameSessionState.AdvanceLevel();

        string effectiveTheme = theme + ", " + ThemeVariationComposer.ComposeVariantTag(
            theme,
            Game.GameSessionState.CurrentLevelIndex,
            Game.GameSessionState.RunSeed);

        var metrics = new RunMetrics
        {
            batchId = batchId,
            runId = $"{Sanitize(theme)}__L{testLevelIndex}__R{runAttempt}",
            theme = theme,
            effectiveTheme = effectiveTheme,
            levelIndex = Game.GameSessionState.CurrentLevelIndex,
            runSeed = Game.GameSessionState.RunSeed,
            usedFallbackPlan = false,
            generationSucceeded = false,
            generationError = "",
            expectedCount = testLevelIndex >= 5 ? 9 : 8
        };

        string runFolder = Path.Combine(batchFolder, metrics.runId);
        Directory.CreateDirectory(runFolder);
        metrics.outputFolder = runFolder;

        // 1) Build plan
        float planStart = Time.realtimeSinceStartup;
        LevelPlan plan = null;

        if (useOllama && ollamaClient != null)
        {
            bool planDone = false;
            string planError = null;

            yield return ollamaClient.GeneratePlan(
                effectiveTheme,
                p =>
                {
                    plan = p;
                    planDone = true;
                },
                e =>
                {
                    planError = e;
                    planDone = true;
                });

            if (!planDone || plan == null || !plan.IsValid)
            {
                metrics.usedFallbackPlan = true;
                if (!string.IsNullOrWhiteSpace(planError))
                    metrics.generationError = "Plan fallback: " + planError;

                plan = FallbackPlan.For(theme, Game.GameSessionState.CurrentLevelIndex, Game.GameSessionState.RunSeed);
            }
        }
        else
        {
            metrics.usedFallbackPlan = true;
            plan = FallbackPlan.For(theme, Game.GameSessionState.CurrentLevelIndex, Game.GameSessionState.RunSeed);
        }

        metrics.planSeconds = Time.realtimeSinceStartup - planStart;

        // 2) Generate textures
        float genStart = Time.realtimeSinceStartup;
        bool done = false;
        string fail = null;
        LevelBundle bundle = null;

        const float pipelineBusyWaitSeconds = 20f;
        float busyWaitUntil = Time.realtimeSinceStartup + pipelineBusyWaitSeconds;
        while (AiLevelPipeline.IsGenerating && Time.realtimeSinceStartup < busyWaitUntil)
            yield return null;

        if (AiLevelPipeline.IsGenerating)
        {
            metrics.generationError = "Pipeline busy for too long before run start.";
            metrics.generationSucceeded = false;
            metrics.generationSeconds = 0f;
            metrics.totalSeconds = Time.realtimeSinceStartup - totalStart;
            onDone?.Invoke(metrics);
            yield break;
        }

        bool started = false;
        const int startAttempts = 3;
        for (int startTry = 1; startTry <= startAttempts; startTry++)
        {
            started = pipeline.GenerateFromPlan(
                plan,
                b =>
                {
                    bundle = b;
                    done = true;
                },
                err =>
                {
                    fail = err;
                    done = true;
                });

            if (started)
                break;

            if (startTry < startAttempts)
            {
                Debug.LogWarning($"[AiGenerationTestRunner] Pipeline busy for {metrics.runId}, retrying start ({startTry + 1}/{startAttempts})...");
                float retryWaitUntil = Time.realtimeSinceStartup + 1.5f;
                while (AiLevelPipeline.IsGenerating && Time.realtimeSinceStartup < retryWaitUntil)
                    yield return null;
                yield return null;
            }
        }

        if (!started)
        {
            metrics.generationError = "Pipeline busy or invalid plan.";
            metrics.generationSucceeded = false;
            metrics.generationSeconds = 0f;
            metrics.totalSeconds = Time.realtimeSinceStartup - totalStart;
            onDone?.Invoke(metrics);
            yield break;
        }

        float timeoutAt = Time.realtimeSinceStartup + Mathf.Max(10f, generationTimeoutSeconds);
        while (!done && Time.realtimeSinceStartup < timeoutAt)
            yield return null;

        if (!done)
        {
            fail = "Generation timeout.";
            AiLevelPipeline.IsGeneratingPublicReset();
        }

        metrics.generationSeconds = Time.realtimeSinceStartup - genStart;
        metrics.totalSeconds = Time.realtimeSinceStartup - totalStart;

        metrics.generationSucceeded = string.IsNullOrWhiteSpace(fail) && bundle != null;
        if (!string.IsNullOrWhiteSpace(fail))
            metrics.generationError = string.IsNullOrWhiteSpace(metrics.generationError)
                ? fail
                : metrics.generationError + " | " + fail;

        if (bundle != null)
            EvaluateBundle(bundle, metrics);

        // 3) Export artifacts
        if (bundle != null && exportPngFiles)
            ExportBundlePng(bundle, runFolder);

        if (exportMetadataJson)
        {
            var metadata = new RunMetadata
            {
                metrics = metrics,
                plan = plan
            };
            string json = JsonUtility.ToJson(metadata, true);
            File.WriteAllText(Path.Combine(runFolder, "metadata.json"), json, Encoding.UTF8);
        }

        WriteQualityRatingsTemplate(runFolder, metrics.levelIndex);

        onDone?.Invoke(metrics);
    }

    private static void EvaluateBundle(LevelBundle bundle, RunMetrics metrics)
    {
        metrics.backgroundOk = bundle.background != null;
        metrics.terrainOk = bundle.terrainTile != null;
        metrics.playerOk = bundle.playerSkin != null;
        metrics.groundEnemyOk = bundle.groundEnemySkin != null;
        metrics.flyingEnemyOk = bundle.flyingEnemySkin != null;
        metrics.shootingEnemyOk = bundle.shootingEnemySkin != null;
        metrics.bossEnemyOk = bundle.bossEnemySkin != null;
        metrics.projectileOk = bundle.shootingProjectileSkin != null;
        metrics.pickupOk = bundle.pickupSkin != null;

        int count = 0;
        if (metrics.backgroundOk) count++;
        if (metrics.terrainOk) count++;
        if (metrics.playerOk) count++;
        if (metrics.groundEnemyOk) count++;
        if (metrics.flyingEnemyOk) count++;
        if (metrics.shootingEnemyOk) count++;
        if (metrics.projectileOk) count++;
        if (metrics.pickupOk) count++;
        if (metrics.levelIndex >= 5 && metrics.bossEnemyOk) count++;

        metrics.okCount = count;

        // Copy per-asset timing and fallback data from bundle.metrics if available
        var bm = bundle.metrics;
        if (bm != null)
        {
            metrics.backgroundSeconds     = bm.backgroundSeconds;
            metrics.terrainSeconds        = bm.terrainSeconds;
            metrics.playerSeconds         = bm.playerSeconds;
            metrics.groundEnemySeconds    = bm.groundEnemySeconds;
            metrics.flyingEnemySeconds    = bm.flyingEnemySeconds;
            metrics.shootingEnemySeconds  = bm.shootingEnemySeconds;
            metrics.bossSeconds           = bm.bossSeconds;
            metrics.projectileSeconds     = bm.projectileSeconds;
            metrics.pickupSeconds         = bm.pickupSeconds;
            metrics.totalGenerationSeconds = bm.totalGenerationSeconds;

            metrics.terrainFallbackUsed       = bm.terrainFallbackUsed;
            metrics.playerFallbackUsed        = bm.playerFallbackUsed;
            metrics.groundEnemyFallbackUsed   = bm.groundEnemyFallbackUsed;
            metrics.flyingEnemyFallbackUsed   = bm.flyingEnemyFallbackUsed;
            metrics.shootingEnemyFallbackUsed = bm.shootingEnemyFallbackUsed;
            metrics.bossFallbackUsed          = bm.bossFallbackUsed;
            metrics.projectileFallbackUsed    = bm.projectileFallbackUsed;
            metrics.pickupFallbackUsed        = bm.pickupFallbackUsed;

            metrics.terrainRetryCount = bm.terrainRetryCount;
            metrics.playerRetryCount  = bm.playerRetryCount;
            metrics.spriteRetryCount  = bm.spriteRetryCount;
            metrics.fallbackCount     = bm.fallbackCount;
        }
    }

    private static void ExportBundlePng(LevelBundle bundle, string folder)
    {
        SaveTexture(bundle.background, Path.Combine(folder, "background.png"));
        SaveTexture(bundle.terrainTile, Path.Combine(folder, "terrain_tile.png"));
        SaveTexture(bundle.playerSkin, Path.Combine(folder, "player.png"));
        SaveTexture(bundle.groundEnemySkin, Path.Combine(folder, "enemy_ground.png"));
        SaveTexture(bundle.flyingEnemySkin, Path.Combine(folder, "enemy_flying.png"));
        SaveTexture(bundle.shootingEnemySkin, Path.Combine(folder, "enemy_shooting.png"));
        SaveTexture(bundle.bossEnemySkin, Path.Combine(folder, "enemy_boss.png"));
        SaveTexture(bundle.shootingProjectileSkin, Path.Combine(folder, "projectile.png"));
        SaveTexture(bundle.pickupSkin, Path.Combine(folder, "pickup.png"));
    }

    private static void SaveTexture(Texture2D texture, string filePath)
    {
        if (texture == null) return;

        byte[] png;
        try
        {
            png = texture.EncodeToPNG();
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[AiGenerationTestRunner] Failed to encode PNG: " + ex.Message);
            return;
        }

        if (png == null || png.Length == 0) return;

        File.WriteAllBytes(filePath, png);
    }

    private static void WriteSummaryCsv(string batchFolder, List<RunMetrics> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("runId,theme,effectiveTheme,levelIndex,runSeed,usedFallbackPlan,generationSucceeded," +
                      "planSeconds,generationSeconds,totalSeconds," +
                      "backgroundSeconds,terrainSeconds,playerSeconds,groundEnemySeconds,flyingEnemySeconds," +
                      "shootingEnemySeconds,bossSeconds,projectileSeconds,pickupSeconds,totalGenerationSeconds," +
                      "okCount,expectedCount," +
                      "backgroundOk,terrainOk,playerOk,groundEnemyOk,flyingEnemyOk,shootingEnemyOk,bossEnemyOk,projectileOk,pickupOk," +
                      "terrainFallbackUsed,playerFallbackUsed,groundEnemyFallbackUsed,flyingEnemyFallbackUsed," +
                      "shootingEnemyFallbackUsed,bossFallbackUsed,projectileFallbackUsed,pickupFallbackUsed," +
                      "terrainRetryCount,playerRetryCount,spriteRetryCount,fallbackCount," +
                      "generationError,outputFolder");

        foreach (var r in rows)
        {
            sb.Append(EscapeCsv(r.runId)).Append(',')
              .Append(EscapeCsv(r.theme)).Append(',')
              .Append(EscapeCsv(r.effectiveTheme)).Append(',')
              .Append(r.levelIndex).Append(',')
              .Append(r.runSeed).Append(',')
              .Append(r.usedFallbackPlan ? "1" : "0").Append(',')
              .Append(r.generationSucceeded ? "1" : "0").Append(',')
              .Append(r.planSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
              .Append(r.generationSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
              .Append(r.totalSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
              .Append(r.backgroundSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
              .Append(r.terrainSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
              .Append(r.playerSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
              .Append(r.groundEnemySeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
              .Append(r.flyingEnemySeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
              .Append(r.shootingEnemySeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
              .Append(r.bossSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
              .Append(r.projectileSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
              .Append(r.pickupSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
              .Append(r.totalGenerationSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
              .Append(r.okCount).Append(',')
              .Append(r.expectedCount).Append(',')
              .Append(r.backgroundOk ? "1" : "0").Append(',')
              .Append(r.terrainOk ? "1" : "0").Append(',')
              .Append(r.playerOk ? "1" : "0").Append(',')
              .Append(r.groundEnemyOk ? "1" : "0").Append(',')
              .Append(r.flyingEnemyOk ? "1" : "0").Append(',')
              .Append(r.shootingEnemyOk ? "1" : "0").Append(',')
              .Append(r.bossEnemyOk ? "1" : "0").Append(',')
              .Append(r.projectileOk ? "1" : "0").Append(',')
              .Append(r.pickupOk ? "1" : "0").Append(',')
              .Append(r.terrainFallbackUsed ? "1" : "0").Append(',')
              .Append(r.playerFallbackUsed ? "1" : "0").Append(',')
              .Append(r.groundEnemyFallbackUsed ? "1" : "0").Append(',')
              .Append(r.flyingEnemyFallbackUsed ? "1" : "0").Append(',')
              .Append(r.shootingEnemyFallbackUsed ? "1" : "0").Append(',')
              .Append(r.bossFallbackUsed ? "1" : "0").Append(',')
              .Append(r.projectileFallbackUsed ? "1" : "0").Append(',')
              .Append(r.pickupFallbackUsed ? "1" : "0").Append(',')
              .Append(r.terrainRetryCount).Append(',')
              .Append(r.playerRetryCount).Append(',')
              .Append(r.spriteRetryCount).Append(',')
              .Append(r.fallbackCount).Append(',')
              .Append(EscapeCsv(r.generationError)).Append(',')
              .Append(EscapeCsv(r.outputFolder))
              .AppendLine();
        }

        File.WriteAllText(Path.Combine(batchFolder, "summary.csv"), sb.ToString(), Encoding.UTF8);
    }

    private static void WriteAggregateSummaryCsv(string batchFolder, List<RunMetrics> rows)
    {
        var sb = new StringBuilder();

        sb.AppendLine("assetType,runs,successful,fallbacks,successRatePercent,minSeconds,avgSeconds,maxSeconds,retryCountTotal");

        List<RunMetrics> bossRows = new List<RunMetrics>();
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].levelIndex >= 5)
                bossRows.Add(rows[i]);
        }

        AppendAggregateRow(sb, "LLM plan", rows,
            r => !r.usedFallbackPlan,
            r => r.usedFallbackPlan,
            r => r.planSeconds,
            r => 0);

        AppendAggregateRow(sb, "Background", rows,
            r => r.backgroundOk,
            r => false,
            r => r.backgroundSeconds,
            r => 0,
            timeIncludeSelector: r => r.backgroundOk);

        AppendAggregateRow(sb, "Terrain", rows,
            r => r.terrainOk,
            r => r.terrainFallbackUsed,
            r => r.terrainSeconds,
            r => r.terrainRetryCount,
            timeIncludeSelector: r => r.terrainOk);

        AppendAggregateRow(sb, "Player", rows,
            r => r.playerOk,
            r => r.playerFallbackUsed,
            r => r.playerSeconds,
            r => r.playerRetryCount,
            timeIncludeSelector: r => r.playerOk);

        AppendAggregateRow(sb, "Ground enemy", rows,
            r => r.groundEnemyOk,
            r => r.groundEnemyFallbackUsed,
            r => r.groundEnemySeconds,
            r => 0,
            timeIncludeSelector: r => r.groundEnemyOk);

        AppendAggregateRow(sb, "Flying enemy", rows,
            r => r.flyingEnemyOk,
            r => r.flyingEnemyFallbackUsed,
            r => r.flyingEnemySeconds,
            r => 0,
            timeIncludeSelector: r => r.flyingEnemyOk);

        AppendAggregateRow(sb, "Shooting enemy", rows,
            r => r.shootingEnemyOk,
            r => r.shootingEnemyFallbackUsed,
            r => r.shootingEnemySeconds,
            r => 0,
            timeIncludeSelector: r => r.shootingEnemyOk);

        AppendAggregateRow(sb, "Boss", bossRows,
            r => r.bossEnemyOk,
            r => r.bossFallbackUsed,
            r => r.bossSeconds,
            r => 0,
            timeIncludeSelector: r => r.bossEnemyOk);

        AppendAggregateRow(sb, "Projectile", rows,
            r => r.projectileOk,
            r => r.projectileFallbackUsed,
            r => r.projectileSeconds,
            r => 0,
            timeIncludeSelector: r => r.projectileOk);

        AppendAggregateRow(sb, "Pickup", rows,
            r => r.pickupOk,
            r => r.pickupFallbackUsed,
            r => r.pickupSeconds,
            r => 0,
            timeIncludeSelector: r => r.pickupOk);

        AppendAggregateRow(sb, "Whole pipeline", rows,
            r => r.generationSucceeded,
            r => r.fallbackCount > 0,
            r => r.totalSeconds > 0f ? r.totalSeconds : r.totalGenerationSeconds,
            r => r.spriteRetryCount + r.terrainRetryCount,
            useFallbackSum: true,
            fallbackSumSelector: r => r.fallbackCount,
            timeIncludeSelector: r => r.generationSucceeded);

        File.WriteAllText(Path.Combine(batchFolder, "aggregate_summary.csv"), sb.ToString(), Encoding.UTF8);

        static void AppendAggregateRow(
            StringBuilder target,
            string assetType,
            List<RunMetrics> source,
            Func<RunMetrics, bool> successSelector,
            Func<RunMetrics, bool> fallbackSelector,
            Func<RunMetrics, float> timeSelector,
            Func<RunMetrics, int> retrySelector,
            bool useFallbackSum = false,
            Func<RunMetrics, int> fallbackSumSelector = null,
            Func<RunMetrics, bool> timeIncludeSelector = null)
        {
            var invLocal = System.Globalization.CultureInfo.InvariantCulture;
            int runs = source.Count;
            int successful = 0;
            int fallbacks = 0;
            int retryTotal = 0;
            int timeCount = 0;

            float min = 0f;
            float max = 0f;
            float sum = 0f;

            min = float.MaxValue;
            max = float.MinValue;

            for (int i = 0; i < source.Count; i++)
            {
                RunMetrics r = source[i];
                if (successSelector(r)) successful++;

                if (useFallbackSum)
                {
                    if (fallbackSumSelector != null)
                        fallbacks += fallbackSumSelector(r);
                }
                else
                {
                    if (fallbackSelector(r)) fallbacks++;
                }

                retryTotal += retrySelector(r);

                bool includeTime = timeIncludeSelector == null || timeIncludeSelector(r);
                if (includeTime)
                {
                    float t = Mathf.Max(0f, timeSelector(r));
                    sum += t;
                    timeCount++;
                    if (t < min) min = t;
                    if (t > max) max = t;
                }
            }

            if (timeCount == 0)
            {
                min = 0f;
                max = 0f;
            }

            float avg = timeCount > 0 ? sum / timeCount : 0f;
            float successRatePercent = runs > 0 ? (successful * 100f) / runs : 0f;

            target.Append(EscapeCsv(assetType)).Append(',')
                  .Append(runs).Append(',')
                  .Append(successful).Append(',')
                  .Append(fallbacks).Append(',')
                  .Append(successRatePercent.ToString("F2", invLocal)).Append(',')
                  .Append(min.ToString("F3", invLocal)).Append(',')
                  .Append(avg.ToString("F3", invLocal)).Append(',')
                  .Append(max.ToString("F3", invLocal)).Append(',')
                  .Append(retryTotal)
                  .AppendLine();
        }
    }

    private static void WriteQualityRatingsTemplate(string runFolder, int levelIndex)
    {
        var sb = new StringBuilder();
        sb.AppendLine("assetType,fileName,rating_1_to_5,mainProblem,note");
        sb.AppendLine("Background,background.png,,,");
        sb.AppendLine("Terrain,terrain_tile.png,,,");
        sb.AppendLine("Player,player.png,,,");
        sb.AppendLine("Ground enemy,enemy_ground.png,,,");
        sb.AppendLine("Flying enemy,enemy_flying.png,,,");
        sb.AppendLine("Shooting enemy,enemy_shooting.png,,,");
        sb.AppendLine("Projectile,projectile.png,,,");
        sb.AppendLine("Pickup,pickup.png,,,");
        if (levelIndex >= 5)
            sb.AppendLine("Boss,enemy_boss.png,,,");

        File.WriteAllText(Path.Combine(runFolder, "quality_ratings_template.csv"), sb.ToString(), Encoding.UTF8);
    }

    private static void WriteBatchTestNotes(string batchFolder,
                                            List<RunMetrics> rows,
                                            string[] themes,
                                            int runsPerTheme,
                                            int testLevelIndex)
    {
        int themeCount = themes != null ? themes.Length : 0;
        int totalRuns = rows != null ? rows.Count : 0;

        var sb = new StringBuilder();
        sb.AppendLine("Batch test notes");
        sb.AppendLine("================");
        sb.AppendLine("Batch folder: " + batchFolder);
        sb.AppendLine("Themes count: " + themeCount);
        sb.AppendLine("Runs per theme: " + Mathf.Max(1, runsPerTheme));
        sb.AppendLine("Total runs: " + totalRuns);
        sb.AppendLine("Tested level index: " + testLevelIndex);
        sb.AppendLine();
        sb.AppendLine("Qualitative asset ratings must be filled manually from exported PNG files using quality_ratings_template.csv in each run folder.");
        sb.AppendLine();
        sb.AppendLine("Error-state tests to perform manually:");
        sb.AppendLine("1) Ollama off / useOllama false");
        sb.AppendLine("2) Invalid JSON response from Ollama");
        sb.AppendLine("3) A1111 off");
        sb.AppendLine("4) Unusable sprite output");
        sb.AppendLine("5) Unusable terrain texture");
        sb.AppendLine();
        sb.AppendLine("Key CSV files:");
        sb.AppendLine("- summary.csv = per-run raw data");
        sb.AppendLine("- aggregate_summary.csv = thesis table data");
        sb.AppendLine("- quality_ratings_template.csv = manual visual assessment template");

        File.WriteAllText(Path.Combine(batchFolder, "batch_test_notes.txt"), sb.ToString(), Encoding.UTF8);
    }

    private static void WriteFailureCasesCsv(string batchFolder, List<RunMetrics> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("runId,theme,levelIndex,generationSucceeded,okCount,expectedCount,usedFallbackPlan,fallbackCount,generationError,outputFolder");

        for (int i = 0; i < rows.Count; i++)
        {
            RunMetrics r = rows[i];
            bool include =
                !r.generationSucceeded ||
                r.okCount < r.expectedCount ||
                r.fallbackCount > 0 ||
                r.usedFallbackPlan ||
                !string.IsNullOrWhiteSpace(r.generationError);

            if (!include) continue;

            sb.Append(EscapeCsv(r.runId)).Append(',')
              .Append(EscapeCsv(r.theme)).Append(',')
              .Append(r.levelIndex).Append(',')
              .Append(r.generationSucceeded ? "1" : "0").Append(',')
              .Append(r.okCount).Append(',')
              .Append(r.expectedCount).Append(',')
              .Append(r.usedFallbackPlan ? "1" : "0").Append(',')
              .Append(r.fallbackCount).Append(',')
              .Append(EscapeCsv(r.generationError)).Append(',')
              .Append(EscapeCsv(r.outputFolder))
              .AppendLine();
        }

        File.WriteAllText(Path.Combine(batchFolder, "failure_cases.csv"), sb.ToString(), Encoding.UTF8);
    }

    private string GetExportRootPath()
    {
        string projectRoot = Directory.GetParent(Application.dataPath)!.FullName;
        string root = Path.Combine(projectRoot, string.IsNullOrWhiteSpace(exportRootFolderName)
            ? "GeneratedTestOutputs"
            : exportRootFolderName.Trim());
        Directory.CreateDirectory(root);
        return root;
    }

    private static string EscapeCsv(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        bool mustQuote = value.Contains(",") || value.Contains("\"") || value.Contains("\n") || value.Contains("\r");
        if (!mustQuote) return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static string Sanitize(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "theme";
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(input.Length);
        foreach (char c in input)
        {
            bool bad = false;
            for (int i = 0; i < invalid.Length; i++)
            {
                if (c == invalid[i])
                {
                    bad = true;
                    break;
                }
            }

            if (bad)
                sb.Append('_');
            else if (char.IsWhiteSpace(c))
                sb.Append('_');
            else
                sb.Append(c);
        }

        return sb.ToString();
    }
}
