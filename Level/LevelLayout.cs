using System.Collections.Generic;
using UnityEngine;

namespace Level
{
    /// <summary>
    /// Output of <see cref="LevelGenerator.Generate"/>.
    /// Contains world-space spawn positions and metadata for one generated level.
    /// </summary>
    public class LevelLayout
    {
        /// <summary>World-space position where the player should spawn.</summary>
        public Vector2 playerSpawn;

        /// <summary>World-space positions for enemy spawns. Count may be less than available enemy slots.</summary>
        public List<Vector2> enemySpawns = new List<Vector2>();

        /// <summary>World-space positions for pickup spawns.</summary>
        public List<Vector2> pickupSpawns = new List<Vector2>();

        /// <summary>World-space position of the level exit trigger.</summary>
        public Vector2 exitPosition;

        /// <summary>World-space position where boss should spawn on level 5.</summary>
        public Vector2 bossSpawnPosition;

        /// <summary>Total width of the generated level in tiles.</summary>
        public int totalWidth;

        /// <summary>Seed used for generation (store for display / reproducibility).</summary>
        public int seed;
    }
}
