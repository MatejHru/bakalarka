using System;
using UnityEngine;

namespace Metrics
{
    /// <summary>
    /// Per-run generation metrics collected by <see cref="AiLevelPipeline"/>.
    /// Stored in <see cref="LevelBundle.metrics"/> and consumed by
    /// <see cref="AiGenerationTestRunner"/> for CSV export.
    /// </summary>
    [Serializable]
    public class GenerationMetrics
    {
        public string theme;
        public int levelIndex;
        public int runSeed;

        public bool usedFallbackPlan;
        public bool generationSucceeded;
        public string errorMessage;

        public float planSeconds;
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

        public bool backgroundOk;
        public bool terrainOk;
        public bool playerOk;
        public bool groundEnemyOk;
        public bool flyingEnemyOk;
        public bool shootingEnemyOk;
        public bool bossOk;
        public bool projectileOk;
        public bool pickupOk;

        public bool terrainFallbackUsed;
        public bool playerFallbackUsed;
        public bool groundEnemyFallbackUsed;
        public bool flyingEnemyFallbackUsed;
        public bool shootingEnemyFallbackUsed;
        public bool bossFallbackUsed;
        public bool projectileFallbackUsed;
        public bool pickupFallbackUsed;

        public int terrainRetryCount;
        public int playerRetryCount;
        public int spriteRetryCount;
        public int fallbackCount;
    }
}
