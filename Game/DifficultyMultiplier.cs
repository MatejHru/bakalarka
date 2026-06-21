using UnityEngine;

namespace Game
{
    /// <summary>
    /// Applies a difficulty multiplier to an already-generated <see cref="LevelPlan"/>.
    /// Call <see cref="Apply"/> once after the plan is loaded, before it is passed to
    /// <see cref="Level.LevelAssembler"/> or <see cref="Gameplay.EnemyController"/>.
    /// </summary>
    public static class DifficultyMultiplier
    {
        public static void Apply(LevelPlan plan, Difficulty difficulty)
        {
            if (plan == null) return;

            switch (difficulty)
            {
                case Difficulty.Easy:
                    plan.enemySpeed       = Mathf.Max(0.5f, plan.enemySpeed * 0.70f);
                    plan.enemyPatrolRange = Mathf.Max(1.0f, plan.enemyPatrolRange * 0.80f);
                    plan.enemyDamage      = 1; // always 1 on easy
                    // Guarantee at least 1 heal per pickup on easy
                    if (plan.pickupHealAmount <= 0) plan.pickupHealAmount = 1;
                    plan.pickupScoreValue = Mathf.RoundToInt(plan.pickupScoreValue * 0.80f);
                    break;

                case Difficulty.Hard:
                    plan.enemySpeed       = plan.enemySpeed * 1.50f;
                    plan.enemyPatrolRange = plan.enemyPatrolRange * 1.40f;
                    plan.enemyDamage      = Mathf.Max(plan.enemyDamage + 1, 2);
                    plan.pickupHealAmount = 0; // no healing on hard
                    plan.pickupScoreValue = Mathf.RoundToInt(plan.pickupScoreValue * 1.50f);
                    break;

                // Normal: no modifications
            }

            // Clamp final values to sane ranges regardless of difficulty
            plan.enemySpeed       = Mathf.Clamp(plan.enemySpeed,       0.5f, 6.0f);
            plan.enemyPatrolRange = Mathf.Clamp(plan.enemyPatrolRange, 1.0f, 8.0f);
            plan.enemyDamage      = Mathf.Clamp(plan.enemyDamage,      1,    3);
            plan.pickupScoreValue = Mathf.Clamp(plan.pickupScoreValue, 10,   300);
            plan.pickupHealAmount = Mathf.Clamp(plan.pickupHealAmount, 0,    2);
        }

        /// <summary>
        /// Scales <see cref="Level.LevelGenerator"/> generation parameters to match difficulty.
        /// Must be called before <see cref="Level.LevelGenerator.Generate"/>.
        /// </summary>
        public static void ApplyToGenerator(Level.LevelGenerator gen, Difficulty difficulty)
        {
            if (gen == null) return;

            switch (difficulty)
            {
                case Difficulty.Easy:
                    gen.enemySpawnChance  = Mathf.Max(0.10f, gen.enemySpawnChance  * 0.60f);
                    gen.gapFrequency      = Mathf.Max(0.10f, gen.gapFrequency      * 0.65f);
                    gen.maxGapWidth       = Mathf.Max(1,     gen.maxGapWidth  - 1);
                    gen.pickupSpawnChance = Mathf.Min(1.00f, gen.pickupSpawnChance * 1.50f);
                    break;

                case Difficulty.Hard:
                    gen.enemySpawnChance  = Mathf.Min(1.00f, gen.enemySpawnChance  * 1.60f);
                    gen.gapFrequency      = Mathf.Min(0.80f, gen.gapFrequency      * 1.35f);
                    gen.maxGapWidth       = Mathf.Min(4,     gen.maxGapWidth  + 1);
                    gen.pickupSpawnChance = Mathf.Max(0.05f, gen.pickupSpawnChance * 0.50f);
                    break;

                // Normal: inspector values unchanged
            }
        }
    }
}
