using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace Level
{
    /// <summary>
    /// Procedural Mario-style platformer level generator.
    ///
    /// Generates a side-view level by placing ground segments and floating platforms
    /// into a Unity <see cref="Tilemap"/>. Also repositions the player, enemies,
    /// pickups, and exit to valid spawn positions.
    ///
    /// Physics constraints (PlayerMovement2D defaults):
    ///   moveSpeed = 6 u/s, jumpForce = 14, gravity scale ≈ 3
    ///   → max jump height ≈ 3.3 tiles, max horizontal distance ≈ 5.7 tiles
    ///   → maxGapWidth is capped at 4 tiles for guaranteed passability.
    ///
    /// Preset system:
    ///   Assign <see cref="PlatformPreset"/> assets (1×1, 2×1, 3×1) to
    ///   <see cref="platformPresets"/>. If left empty the generator falls back
    ///   to built-in 1–3 tile platforms automatically.
    ///
    /// Call <see cref="Generate"/> from <see cref="Game.GameController"/> before
    /// the AI texture pipeline starts so <see cref="LevelAssembler"/> can re-skin
    /// the generated tiles later.
    /// </summary>
    public class LevelGenerator : MonoBehaviour
    {
        // ── Inspector ─────────────────────────────────────────────────────────

        [Header("Tilemap")]
        [Tooltip("The terrain Tilemap to write tiles into. Same one used by LevelAssembler.")]
        public Tilemap terrainTilemap;

        [Header("Platform Presets")]
        [Tooltip("Optional floating-platform presets (1×1, 2×1, 3×1…).\n" +
                 "Leave empty to use built-in 1–3 tile platforms.\n" +
                 "Create via: right-click → Create → Platformer → Platform Preset")]
        public PlatformPreset[] platformPresets;

        [Header("Level Dimensions")]
        [Tooltip("Total level width in tiles. At moveSpeed=6 and average pace ~3.5 u/s, " +
                 "100 tiles ≈ 28 seconds.")]
        [Min(30)] public int levelWidth = 150;

        [Tooltip("Y cell coordinate of the top of the ground floor (0 = world origin row).")]
        public int groundY = 0;

        [Tooltip("How many tile rows deep the ground floor is. 1 = single tile like platforms, no doubled look.")]
        [Min(1)] public int groundDepth = 1;

        [Header("Generation Parameters")]
        [Tooltip("Use a random seed each time. Disable to reproduce the same level.")]
        public bool randomSeed = true;

        [Tooltip("Seed used when randomSeed is off. Also logged for reproducibility.")]
        public int seed = 0;

        [Range(0f, 0.8f)]
        [Tooltip("Probability per decision step that a gap appears instead of a flat segment.\n" +
                 "0 = no gaps (flat level), 0.5 = gaps roughly half the time.")]
        public float gapFrequency = 0.55f;

        [Range(1, 4)]
        [Tooltip("Maximum gap width in tiles. Keep ≤4 for guaranteed direct-jump passability.")]
        public int maxGapWidth = 3;

        [Range(0f, 1f)]
        [Tooltip("Probability that an enemy spawns on a ground segment (after the safe start zone).")]
        public float enemySpawnChance = 0.5f;

        [Range(0f, 1f)]
        [Tooltip("Probability that a pickup spawns on a ground segment.")]
        public float pickupSpawnChance = 0.35f;

        [Header("Spawn Object References")]
        [Tooltip("Player transform to reposition on generation.")]
        public Transform playerTransform;

        [Tooltip("Ground patrol enemy pool. Active when enemyType == \"ground\".")]
        public Transform[] enemyTransforms;

        [Tooltip("Flying enemy pool (FlyingEnemyController). Active when enemyType == \"flying\".")]
        public Transform[] flyingEnemyTransforms;

        [Tooltip("Shooting enemy pool (ShootingEnemyController). Active when enemyType == \"shooting\".")]
        public Transform[] shootingEnemyTransforms;

        [Tooltip("Active enemy type for this level. Set by GameController before Generate() is called.")]
        public string enemyType = "ground";

        [Tooltip("Pickup transforms to reposition. Extras beyond spawn count are deactivated.")]
        public Transform[] pickupTransforms;

        [Tooltip("Level exit trigger transform to reposition.")]
        public Transform exitTransform;

        [Tooltip("Optional boss transform. Activated and positioned only on level 5+.")]
        public Transform bossTransform;

        // ── Internal ──────────────────────────────────────────────────────────

        private Tile _placeholderTile;
        private System.Random _rng;
        private HashSet<Vector3Int> _occupiedCells;

        // ── Constants ─────────────────────────────────────────────────────────

        private const int StartZoneWidth = 6;
        private const int EndZoneWidth   = 8;
        private const int BossArenaMinWidth = 18;

        // Floor Y range relative to groundY.
        // The main path can rise up to +5 tiles above groundY.
        // Floating bonus platforms can be up to +3 above the current floor.
        private const int MaxFloorRise  =  7;
        private const int MaxFloorDrop  = -2; // allow slight pits (relative to groundY)
        private const int MaxJumpHeight =  3; // max tiles a player can safely ascend in one jump

        // Guaranteed non-linear section that forces: right -> climb -> back left on upper route -> continue.
        private const int BacktrackSectionMinWidth = 18;

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Generate a new level. Clears the tilemap, places ground and platforms,
        /// then repositions all spawn objects. Returns the resulting <see cref="LevelLayout"/>.
        /// </summary>
        public LevelLayout Generate()
        {
            if (randomSeed)
                seed = Random.Range(0, int.MaxValue);

            _rng = new System.Random(seed);

            // Always 1 tile thick — the inspector serialized value may still be 2 from
            // old scenes. Enforce it here so the floor never looks doubled.
            groundDepth = 1;

            EnsurePlaceholderTile();
            terrainTilemap.ClearAllTiles();
            _occupiedCells = new HashSet<Vector3Int>();

            var layout = new LevelLayout { seed = seed, totalWidth = levelWidth };

            BuildLayout(layout);
            PlaceObjects(layout);

            terrainTilemap.RefreshAllTiles();

            Debug.Log($"[LevelGenerator] Level generated  seed={seed}  width={levelWidth}" +
                      $"  enemies={layout.enemySpawns.Count}  pickups={layout.pickupSpawns.Count}");

            return layout;
        }

        // ── Layout building ───────────────────────────────────────────────────

        private void BuildLayout(LevelLayout layout)
        {
            if (Game.GameSessionState.CurrentLevelIndex >= 5)
            {
                BuildBossArenaLevel(layout);
                return;
            }

            // Safe flat start — player spawns here, no hazards
            FillSegment(0, StartZoneWidth, groundY);
            layout.playerSpawn = new Vector2(1.5f, groundY + 2f);

            int x        = StartZoneWidth;
            int end      = levelWidth - EndZoneWidth;
            int currentY = groundY;   // Y of the main floor the player is walking on
            bool placedBacktrack = false;

            while (x < end)
            {
                int remaining = end - x;
                if (remaining <= 0) break;

                bool safeZone = x < StartZoneWidth + 12;

                // Inject one forced backtracking challenge in the middle third.
                bool inMiddleThird = x > levelWidth / 3 && x < (levelWidth * 2) / 3;
                if (!safeZone && !placedBacktrack && inMiddleThird && remaining > BacktrackSectionMinWidth + 6)
                {
                    x = PlaceBacktrackTowerSection(x, currentY, layout);
                    currentY = Mathf.Clamp(currentY + 3, groundY + MaxFloorDrop, groundY + MaxFloorRise);
                    placedBacktrack = true;
                    continue;
                }

                // ── Pick a section type ────────────────────────────────────────
                double roll = _rng.NextDouble();

                if (!safeZone && roll < 0.22 && remaining > 14)
                {
                    // ── Height transition: rise or fall ────────────────────────
                    int delta  = _rng.Next(-2, MaxJumpHeight + 1); // -2 … +3
                    int targetY = Mathf.Clamp(currentY + delta,
                                              groundY + MaxFloorDrop,
                                              groundY + MaxFloorRise);
                    delta = targetY - currentY;

                    if (delta > 0)
                    {
                        // Rising: place step platforms so it's actually climbable
                        x = PlaceRisingSteps(x, currentY, targetY, layout);
                    }
                    else if (delta < 0)
                    {
                        // Falling: just place ground lower — player can drop down
                        int segW = _rng.Next(3, 6);
                        segW     = Mathf.Min(segW, remaining - 4);
                        if (segW < 2) segW = 2;
                        FillSegment(x, segW, targetY);
                        x += segW;
                    }
                    currentY = targetY;
                }
                else if (!safeZone && roll < 0.50 && remaining > maxGapWidth + 5)
                {
                    // ── Gap with platform bridge ───────────────────────────────
                    int gapW = RandInt(2, maxGapWidth);
                    gapW     = Mathf.Min(gapW, remaining - 4);
                    if (gapW < 1) gapW = 1;
                    PlaceGapBridge(x, gapW, currentY, layout);
                    x += gapW;
                }
                else if (!safeZone && roll < 0.65 && remaining > 8)
                {
                    // ── Floating platform chain (optional shortcut / bonus path)
                    int segW = RandInt(3, 6);
                    segW     = Mathf.Min(segW, remaining - 2);
                    if (segW < 2) segW = 2;
                    FillSegment(x, segW, currentY);
                    PlacePlatformChain(x, segW, currentY, layout);
                    AddSpawns(x, segW, currentY, safeZone, layout);
                    x += segW;
                }
                else if (!safeZone && roll < 0.78 && remaining > 10)
                {
                    // ── Plateau: ground + elevated platform ────────────────────
                    int segW = RandInt(5, 9);
                    segW = Mathf.Min(segW, remaining - 1);
                    if (segW < 3) segW = 3;

                    FillSegment(x, segW, currentY);
                    int plateauY = Mathf.Clamp(currentY + RandInt(2, MaxJumpHeight + 1),
                                               groundY + MaxFloorDrop,
                                               groundY + MaxFloorRise);
                    int platformW = Mathf.Max(2, segW - RandInt(2, 3));
                    int platformX = x + 1;
                    PlacePlatformTiles(platformX, plateauY, platformW);

                    if (_rng.NextDouble() < 0.7)
                        layout.pickupSpawns.Add(new Vector2(platformX + platformW * 0.5f, plateauY + 2.0f));
                    if (_rng.NextDouble() < 0.5)
                        layout.enemySpawns.Add(new Vector2(platformX + 1.5f, plateauY + 1.0f));

                    AddGroundBumps(x, segW, currentY);
                    x += segW;
                }
                else if (!safeZone && roll < 0.90 && remaining > 12)
                {
                    // ── Split-route: two floating upper platforms over a shared floor
                    x = PlaceSplitRouteSection(x, currentY, layout);
                }
                else
                {
                    // ── Regular ground segment ─────────────────────────────────
                    int segW = RandInt(3, 8);
                    segW     = Mathf.Min(segW, remaining);
                    if (segW < 1) segW = 1;
                    FillSegment(x, segW, currentY);
                    AddSpawns(x, segW, currentY, safeZone, layout);
                    AddGroundBumps(x, segW, currentY);
                    x += segW;
                }
            }

            // Safe end zone — always back at groundY so exit is reachable
            if (currentY != groundY)
            {
                // One last small segment at current height then step/drop to groundY
                FillSegment(x, 2, currentY);
                x += 2;
            }
            FillSegment(x, EndZoneWidth, groundY);
            layout.exitPosition = new Vector2(x + EndZoneWidth - 2f, groundY + 1f);
        }

        private void BuildBossArenaLevel(LevelLayout layout)
        {
            // Entire level is a dedicated boss arena — full flat floor for boss to chase/dash.
            FillSegment(0, levelWidth, groundY);

            int leftMargin  = 6;
            int rightMargin = 8;
            int arenaStart  = leftMargin;
            int arenaEnd    = Mathf.Max(arenaStart + 20, levelWidth - rightMargin);
            int arenaWidth  = arenaEnd - arenaStart;

            layout.playerSpawn = new Vector2(arenaStart + 1.5f, groundY + 2f);

            // Three wide upper platforms with clear separation so the boss has long
            // horizontal floor runs and doesn't get wedged between narrow blocks.
            // Left upper platform
            int wLeft   = Mathf.Max(7, 8 + _rng.Next(-1, 2));
            int yLeft   = Mathf.Clamp(groundY + 3 + _rng.Next(0, 2), groundY + 3, groundY + 4);
            int xLeft   = arenaStart + Mathf.Clamp(5 + _rng.Next(-1, 2), 3, arenaWidth / 4);

            // Center high platform
            int wCenter = Mathf.Max(6, 7 + _rng.Next(-1, 2));
            int yCenter = Mathf.Clamp(groundY + 5 + _rng.Next(-1, 1), groundY + 4, groundY + 6);
            int xCenter = arenaStart + arenaWidth / 2 - wCenter / 2 + _rng.Next(-1, 2);

            // Right upper platform
            int wRight  = Mathf.Max(7, 8 + _rng.Next(-1, 2));
            int yRight  = Mathf.Clamp(groundY + 3 + _rng.Next(0, 2), groundY + 3, groundY + 4);
            int xRight  = arenaStart + Mathf.Clamp(arenaWidth - wRight - 5 + _rng.Next(-1, 2),
                              arenaWidth * 3 / 4, arenaWidth - wRight - 3);

            PlacePlatformTiles(xLeft,   yLeft,   wLeft);
            PlacePlatformTiles(xCenter, yCenter, wCenter);
            PlacePlatformTiles(xRight,  yRight,  wRight);

            // Two small helper ledges near the left and right walls to help player reposition;
            // kept low (groundY+2) so they don't create a maze for the boss.
            int xLedge1 = arenaStart + _rng.Next(1, 3);
            int xLedge2 = arenaEnd   - _rng.Next(5, 7);
            PlacePlatformTiles(xLedge1, groundY + 2, 3);
            PlacePlatformTiles(xLedge2, groundY + 2, 3);

            // Two pickups — one on each upper side platform.
            layout.pickupSpawns.Clear();
            layout.pickupSpawns.Add(new Vector2(xLeft  + wLeft  * 0.5f, yLeft  + 1.8f));
            layout.pickupSpawns.Add(new Vector2(xRight + wRight * 0.5f, yRight + 1.8f));

            layout.enemySpawns.Clear();
            layout.bossSpawnPosition = new Vector2(arenaStart + arenaWidth * 0.5f, groundY + 1f);
            layout.exitPosition = new Vector2(arenaEnd - 1.8f, groundY + 1f);
        }

        // ── Section builders ──────────────────────────────────────────────────

        /// <summary>
        /// Place step platforms to let the player climb from <paramref name="fromY"/>
        /// up to <paramref name="toY"/>. Returns the new X position after the steps.
        /// Each step is 1 tile high and 2 tiles wide, so it's always jumpable.
        /// </summary>
        private int PlaceRisingSteps(int startX, int fromY, int toY, LevelLayout layout)
        {
            int steps = toY - fromY; // always positive
            int x     = startX;

            for (int s = 1; s <= steps; s++)
            {
                int stepY = fromY + s;
                int stepW = RandInt(2, 4);
                FillSegment(x, stepW, stepY);
                // Pickup reward on top of step sometimes
                if (_rng.NextDouble() < 0.4)
                    layout.pickupSpawns.Add(new Vector2(x + stepW * 0.5f, stepY + 2.0f));
                x += stepW;
            }
            return x;
        }

        /// <summary>
        /// Forced non-linear section:
        /// 1) lower path goes right and is blocked by a tall wall,
        /// 2) player must continue right to stairs,
        /// 3) climb up,
        /// 4) traverse upper path back to the left over the wall,
        /// 5) then continue right on the high lane.
        /// </summary>
        private int PlaceBacktrackTowerSection(int startX, int floorY, LevelLayout layout)
        {
            int sectionW = RandInt(BacktrackSectionMinWidth, BacktrackSectionMinWidth + 4);

            int wallX       = startX + 6;
            int stairStartX = startX + 10;
            int upperY      = Mathf.Clamp(floorY + 3, groundY + MaxFloorDrop, groundY + MaxFloorRise);

            // Base floor across the whole section
            FillSegment(startX, sectionW, floorY);

            // Tall blocker wall (too high for direct jump)
            for (int tx = wallX; tx < wallX + 2; tx++)
            {
                for (int ty = floorY + 1; ty <= floorY + 5; ty++)
                    terrainTilemap.SetTile(new Vector3Int(tx, ty, 0), _placeholderTile);
            }

            // Stairs on the right side: must go right first
            int sx = stairStartX;
            for (int s = 1; s <= 3; s++)
            {
                int stepY = floorY + s;
                FillSegment(sx, 2, stepY);
                sx += 2;
            }

            // Upper path starts LEFT of the wall, forcing brief backtrack after climb.
            // Then it extends to the section end so player can continue right.
            int upperStartX = wallX - 3;
            int upperWidth  = (startX + sectionW) - upperStartX;
            PlacePlatformTiles(upperStartX, upperY, upperWidth);

            // Reward items on upper route
            if (_rng.NextDouble() < 0.8)
                layout.pickupSpawns.Add(new Vector2(upperStartX + 1.5f, upperY + 2.0f));
            if (_rng.NextDouble() < 0.7)
                layout.pickupSpawns.Add(new Vector2(upperStartX + upperWidth - 2f, upperY + 2.0f));

            // Enemy patrol near the top route entrance
            if (_rng.NextDouble() < 0.75)
                layout.enemySpawns.Add(new Vector2(upperStartX + 3f, upperY + 1.0f));

            return startX + sectionW;
        }

        /// <summary>
        /// Place a gap (void) and a floating bridge platform so it's always passable.
        /// May add a second platform at a different height for variety.
        /// </summary>
        private void PlaceGapBridge(int gapStartX, int gapWidth, int floorY, LevelLayout layout)
        {
            // Primary bridge — always present, at a random height above floorY
            PlatformPreset bridgePreset = PickPreset();
            int bridgeW = GetPresetWidthOrFallback(bridgePreset, 2, 4);
            int bridgeX = gapStartX - 1; // overlap left edge by 1
            int bridgeH = FindAvailablePlatformY(bridgeX, floorY, bridgeW, 1, MaxJumpHeight + 1);

            PlacePlatformPreset(bridgeX, bridgeH, bridgePreset);

            if (_rng.NextDouble() < 0.5)
                layout.pickupSpawns.Add(new Vector2(bridgeX + bridgeW * 0.5f, bridgeH + 2.0f));

            // Optional second platform at a different height (chain jump)
            if (gapWidth >= 3 && _rng.NextDouble() < 0.6)
            {
                PlatformPreset p2Preset = PickPreset();
                int w2 = GetPresetWidthOrFallback(p2Preset, 1, 3);
                int x2 = gapStartX + gapWidth / 2;
                int h2 = FindAvailablePlatformY(x2, floorY, w2, 1, MaxJumpHeight + 2);
                if (Mathf.Abs(h2 - bridgeH) < 1)
                    h2 = bridgeH + 1;
                PlacePlatformPreset(x2, h2, p2Preset);
            }
        }

        /// <summary>
        /// Place a cluster of bonus floating platforms above a ground segment.
        /// These are optional (the ground is already there) but reward exploration.
        /// </summary>
        private void PlacePlatformChain(int segStartX, int segWidth, int floorY, LevelLayout layout)
        {
            int count   = RandInt(1, 3); // 1–2 platforms
            int cursor  = segStartX + 1;

            for (int i = 0; i < count; i++)
            {
                if (cursor >= segStartX + segWidth - 1) break;

                PlatformPreset chainPreset = PickPreset();
                int w = GetPresetWidthOrFallback(chainPreset, 1, 4);
                w = Mathf.Min(w, segStartX + segWidth - cursor - 1);
                if (w < 1) break;

                int h = FindAvailablePlatformY(cursor, floorY, w, 2, MaxJumpHeight + 2);

                PlacePlatformPreset(cursor, h, chainPreset);

                if (_rng.NextDouble() < 0.55)
                    layout.pickupSpawns.Add(new Vector2(cursor + w * 0.5f, h + 2.0f));

                cursor += w + RandInt(1, 3);
            }
        }

        /// <summary>
        /// A split-route section: two floating platforms at different heights above a shared floor.
        /// Both routes converge at the end — always passable from ground level.
        /// </summary>
        private int PlaceSplitRouteSection(int startX, int floorY, LevelLayout layout)
        {
            int sectionW = RandInt(10, 16);
            int upperY   = Mathf.Clamp(floorY + RandInt(2, MaxJumpHeight + 1),
                                       groundY + MaxFloorDrop,
                                       groundY + MaxFloorRise);

            FillSegment(startX, sectionW, floorY);

            int p1W = RandInt(3, 5);
            int p2W = RandInt(3, 5);
            int p1X = startX + 2;
            int p2X = startX + sectionW - p2W - 2;

            PlacePlatformTiles(p1X, upperY, p1W);
            PlacePlatformTiles(p2X, upperY + RandInt(0, 1), p2W);

            layout.pickupSpawns.Add(new Vector2(p1X + p1W * 0.5f, upperY + 2.0f));

            if (_rng.NextDouble() < 0.45)
                layout.enemySpawns.Add(new Vector2(p2X + 1.5f, upperY + 1.0f));

            if (_rng.NextDouble() < 0.55)
            {
                int midX = startX + sectionW / 2;
                int midY = Mathf.Clamp(floorY + 2, groundY + MaxFloorDrop, groundY + MaxFloorRise);
                PlacePlatformTiles(midX, midY, RandInt(2, 3));
            }

            AddSpawns(startX, sectionW, floorY, safeZone: false, layout);
            return startX + sectionW;
        }

        private void AddSpawns(int segX, int segW, int floorY, bool safeZone, LevelLayout layout)
        {
            if (safeZone) return;

            // Pass tile-top Y (floorY + 1 in world space) — ComputeEnemySpawnY adds halfHeight on top.
            if (_rng.NextDouble() < enemySpawnChance)
                layout.enemySpawns.Add(new Vector2(segX + segW * 0.5f, floorY + 1.0f));

            if (_rng.NextDouble() < pickupSpawnChance && segW >= 2)
                layout.pickupSpawns.Add(new Vector2(segX + RandInt(1, segW), floorY + 2.5f));
        }

        // ── Tile helpers ──────────────────────────────────────────────────────

        /// <summary>Fill a ground segment: <paramref name="depth"/> rows from topY downward.</summary>
        private void FillSegment(int startX, int width, int topY)
        {
            // Keep elevated floors one-tile thick so they don't look like chunky 2-high blocks.
            int depth = topY == groundY ? groundDepth : 1;
            for (int tx = startX; tx < startX + width; tx++)
                for (int ty = topY; ty > topY - depth; ty--)
                    PlaceTile(tx, ty);
        }

        /// <summary>Fill a single-row platform (no depth).</summary>
        private void PlacePlatformTiles(int startX, int y, int width)
        {
            for (int tx = startX; tx < startX + width; tx++)
                PlaceTile(tx, y);
        }

        /// <summary>
        /// Place a platform using a <see cref="PlatformPreset"/>'s tile offsets.
        /// Falls back to a straight row when the preset has no tile data.
        /// </summary>
        private void PlacePlatformPreset(int startX, int y, PlatformPreset preset)
        {
            if (preset == null || preset.tiles == null || preset.tiles.Length == 0)
            {
                int fallbackW = preset != null ? Mathf.Max(1, preset.width) : RandInt(1, 4);
                PlacePlatformTiles(startX, y, fallbackW);
                return;
            }

            foreach (Vector2Int cell in preset.tiles)
                PlaceTile(startX + cell.x, y + cell.y);
        }

        /// <summary>Returns the preset width when a preset is available, otherwise a random int in [min, max].</summary>
        private int GetPresetWidthOrFallback(PlatformPreset preset, int min, int max)
        {
            return preset != null ? Mathf.Max(1, preset.width) : RandInt(min, max);
        }

        /// <summary>
        /// Optionally add 1–2 single-tile bumps on a ground segment to break up flat stretches.
        /// Only fires 35 % of the time on segments at least 6 tiles wide.
        /// </summary>
        private void AddGroundBumps(int segX, int segW, int floorY)
        {
            if (segW < 6) return;
            if (_rng.NextDouble() > 0.35) return;

            int bumpCount = RandInt(1, 2);
            for (int i = 0; i < bumpCount; i++)
            {
                int bx = segX + RandInt(2, Mathf.Max(2, segW - 3));
                int bw = RandInt(1, 2);

                for (int bxi = bx; bxi < bx + bw && bxi < segX + segW - 1; bxi++)
                    PlaceTile(bxi, floorY + 1);
            }
        }

        private void PlaceTile(int x, int y)
        {
            Vector3Int pos = new Vector3Int(x, y, 0);
            if (_occupiedCells != null && _occupiedCells.Contains(pos)) return;
            terrainTilemap.SetTile(pos, _placeholderTile);
            _occupiedCells?.Add(pos);
        }

        private bool IsPlatformAreaFree(int startX, int y, int width)
        {
            if (_occupiedCells == null) return true;
            for (int tx = startX; tx < startX + width; tx++)
                if (_occupiedCells.Contains(new Vector3Int(tx, y, 0)))
                    return false;
            return true;
        }

        private int FindAvailablePlatformY(int startX, int floorY, int width, int minOffset, int maxOffset)
        {
            int candidate = floorY + RandInt(minOffset, maxOffset);
            for (int i = 0; i < 6; i++)
            {
                int y = floorY + RandInt(minOffset, maxOffset);
                if (IsPlatformAreaFree(startX, y, width)) return y;
            }
            return candidate;
        }

        private PlatformPreset PickPreset()
        {
            if (platformPresets == null || platformPresets.Length == 0) return null;
            return platformPresets[_rng.Next(0, platformPresets.Length)];
        }

        /// <summary>Random int in [min, max] inclusive, safe even when min == max.</summary>
        private int RandInt(int min, int max)
        {
            if (min >= max) return min;
            return _rng.Next(min, max + 1);
        }

        // ── Object placement ──────────────────────────────────────────────────

        private void PlaceObjects(LevelLayout layout)
        {
            if (playerTransform != null)
                playerTransform.position = new Vector3(layout.playerSpawn.x, layout.playerSpawn.y, 0f);

            PlaceMixedEnemyPools(layout.enemySpawns);

            if (pickupTransforms != null)
            {
                for (int i = 0; i < pickupTransforms.Length; i++)
                {
                    if (pickupTransforms[i] == null) continue;

                    if (i < layout.pickupSpawns.Count)
                    {
                        pickupTransforms[i].gameObject.SetActive(true);
                        pickupTransforms[i].position = new Vector3(layout.pickupSpawns[i].x,
                                                                    layout.pickupSpawns[i].y, 0f);
                    }
                    else
                    {
                        pickupTransforms[i].gameObject.SetActive(false);
                    }
                }
            }

            if (exitTransform != null)
                exitTransform.position = new Vector3(layout.exitPosition.x, layout.exitPosition.y, 0f);

            PlaceBossIfNeeded(layout);
        }

        // ── Enemy pool helpers ────────────────────────────────────────────────

        private void DeactivatePool(Transform[] pool)
        {
            if (pool == null) return;
            foreach (var t in pool)
                if (t != null) t.gameObject.SetActive(false);
        }

        private void PlaceMixedEnemyPools(System.Collections.Generic.List<Vector2> spawns)
        {
            DeactivatePool(enemyTransforms);
            DeactivatePool(flyingEnemyTransforms);
            DeactivatePool(shootingEnemyTransforms);

            if (Game.GameSessionState.CurrentLevelIndex >= 5)
                return; // Boss level: regular enemies stay disabled.

            var groundAvailable   = CollectAvailable(enemyTransforms);
            var flyingAvailable   = CollectAvailable(flyingEnemyTransforms);
            var shootingAvailable = CollectAvailable(shootingEnemyTransforms);

            string mode = string.IsNullOrWhiteSpace(enemyType) ? "ground" : enemyType.Trim().ToLowerInvariant();
            var primary = mode == "flying" ? flyingAvailable : mode == "shooting" ? shootingAvailable : groundAvailable;

            int spawnCount = spawns == null ? 0 : spawns.Count;
            for (int i = 0; i < spawnCount; i++)
            {
                Transform enemy = null;

                // Keep a real mix in every level, but mildly bias toward selected mode.
                int roll = _rng.Next(0, 100);
                if (roll < 45 && primary.Count > 0)
                    enemy = PopRandom(primary);
                else
                {
                    if (groundAvailable.Count > 0 && enemy == null)
                        enemy = PopRandom(groundAvailable);
                    if (flyingAvailable.Count > 0 && enemy == null)
                        enemy = PopRandom(flyingAvailable);
                    if (shootingAvailable.Count > 0 && enemy == null)
                        enemy = PopRandom(shootingAvailable);
                }

                if (enemy == null && primary.Count > 0) enemy = PopRandom(primary);
                if (enemy == null && groundAvailable.Count > 0) enemy = PopRandom(groundAvailable);
                if (enemy == null && flyingAvailable.Count > 0) enemy = PopRandom(flyingAvailable);
                if (enemy == null && shootingAvailable.Count > 0) enemy = PopRandom(shootingAvailable);

                if (enemy == null)
                    break; // no slots left in any pool

                enemy.gameObject.SetActive(true);
                PlaceSingleEnemy(enemy, spawns[i]);
            }
        }

        private static System.Collections.Generic.List<Transform> CollectAvailable(Transform[] pool)
        {
            var list = new System.Collections.Generic.List<Transform>();
            if (pool == null) return list;
            foreach (var t in pool)
                if (t != null) list.Add(t);
            return list;
        }

        private Transform PopRandom(System.Collections.Generic.List<Transform> list)
        {
            if (list == null || list.Count == 0) return null;
            int idx = _rng.Next(0, list.Count);
            Transform t = list[idx];
            list.RemoveAt(idx);
            return t;
        }

        private void PlaceSingleEnemy(Transform enemy, Vector2 floorTopSpawn)
        {
            if (enemy == null) return;

            // FlyingEnemyController — keep it above terrain and out of solids.
            var flying = enemy.GetComponent<Gameplay.FlyingEnemyController>();
            if (flying != null)
            {
                Vector3 desired = new Vector3(floorTopSpawn.x, floorTopSpawn.y + 1.7f, 0f);
                Vector3 safe = ResolveAirEnemySpawnPosition(enemy, desired);
                flying.SetSpawnOrigin(safe);
                return;
            }

            // ShootingEnemyController — stands on the floor, no patrol.
            var shooting = enemy.GetComponent<Gameplay.ShootingEnemyController>();
            if (shooting != null)
            {
                float sy = ComputeEnemySpawnY(enemy, floorTopSpawn.y);
                Vector3 safe = ResolveEnemySpawnPosition(enemy, new Vector3(floorTopSpawn.x, sy, 0f));
                shooting.SetSpawnOrigin(safe);
                return;
            }

            // Default: EnemyController ground patrol.
            float spawnY = ComputeEnemySpawnY(enemy, floorTopSpawn.y);
            Vector3 spawnPos = ResolveEnemySpawnPosition(enemy, new Vector3(floorTopSpawn.x, spawnY, 0f));
            var ec = enemy.GetComponent<Gameplay.EnemyController>();
            if (ec != null)
                ec.SetPatrolOrigin(spawnPos);
            else
                enemy.position = spawnPos;
        }

        private void PlaceBossIfNeeded(LevelLayout layout)
        {
            if (bossTransform == null) return;

            bool bossLevel = Game.GameSessionState.CurrentLevelIndex >= 5;
            bossTransform.gameObject.SetActive(bossLevel);
            if (!bossLevel) return;

            Vector2 bossSpawn = layout != null && layout.bossSpawnPosition != Vector2.zero
                ? layout.bossSpawnPosition
                : new Vector2((layout != null ? layout.exitPosition.x : levelWidth - 8f) - 7f, groundY + 1f);

            float halfH = GetEnemyHalfExtents(bossTransform).y;
            float spawnY = bossSpawn.y + halfH + 0.06f;
            Vector3 nearCenter = new Vector3(bossSpawn.x, spawnY, 0f);
            Vector3 safe = ResolveEnemySpawnPosition(bossTransform, nearCenter);

            // Ensure visibly larger boss silhouette.
            Vector3 ls = bossTransform.localScale;
            float sx = ls.x >= 0f ? 2f : -2f;
            bossTransform.localScale = new Vector3(sx, 2f, 1f);

            var boss = bossTransform.GetComponent<Gameplay.BossController>();
            if (boss == null)
                boss = bossTransform.gameObject.AddComponent<Gameplay.BossController>();

            string bossName = Game.GameSessionState.CurrentLore != null &&
                              !string.IsNullOrWhiteSpace(Game.GameSessionState.CurrentLore.bossName)
                ? Game.GameSessionState.CurrentLore.bossName
                : "Boss";

            float speed = 3.0f;
            int damage = 2;
            float patrol = 9f;
            if (Game.GameSessionState.CurrentLevelPlan != null)
            {
                speed = Mathf.Max(2.2f, Game.GameSessionState.CurrentLevelPlan.enemySpeed * 1.25f);
                damage = Mathf.Max(2, Game.GameSessionState.CurrentLevelPlan.enemyDamage + 1);
                patrol = Mathf.Max(6f, Game.GameSessionState.CurrentLevelPlan.enemyPatrolRange * 1.7f);
            }

            boss.SetPatrolOrigin(safe);
            boss.Configure(bossName, hp: 5, speed: speed, patrol: patrol, damage: damage);

            var ec = bossTransform.GetComponent<Gameplay.EnemyController>();
            if (ec != null) ec.enabled = false;
        }

        private void PlaceBossArena(int arenaStartX, LevelLayout layout)
        {
            int remainingWidth = Mathf.Max(12, levelWidth - arenaStartX);
            int arenaWidth = Mathf.Max(BossArenaMinWidth, Mathf.Min(34, remainingWidth));

            // Thick, flat floor to keep the fight stable.
            FillSegment(arenaStartX, arenaWidth, groundY);

            int leftWallX = arenaStartX + 1;
            int rightWallX = arenaStartX + arenaWidth - 2;
            for (int y = groundY + 1; y <= groundY + 6; y++)
            {
                PlaceTile(leftWallX, y);
                PlaceTile(rightWallX, y);
            }

            // Main combat platforms (left / center / right) for boss jump/chase dynamics.
            int pY1 = groundY + 3;
            int pY2 = groundY + 5;
            PlacePlatformTiles(arenaStartX + 4, pY1, 6);
            PlacePlatformTiles(arenaStartX + arenaWidth - 10, pY1, 6);
            PlacePlatformTiles(arenaStartX + arenaWidth / 2 - 3, pY2, 6);

            // Small helper ledges to keep traversal smooth for player and boss.
            PlacePlatformTiles(arenaStartX + 11, groundY + 2, 3);
            PlacePlatformTiles(arenaStartX + arenaWidth - 14, groundY + 2, 3);

            layout.bossSpawnPosition = new Vector2(arenaStartX + arenaWidth * 0.5f, groundY + 1f);
            layout.exitPosition = new Vector2(arenaStartX + arenaWidth - 2.5f, groundY + 1f);
        }

        // ── Placeholder tile ──────────────────────────────────────────────────

        private void EnsurePlaceholderTile()
        {
            if (_placeholderTile != null) return;

            // Simple solid-color texture. LevelAssembler will replace this with
            // the AI-generated terrain texture when the bundle arrives.
            var tex = new Texture2D(16, 16, TextureFormat.RGBA32, false);
            var px  = new Color32[256];
            for (int i = 0; i < 256; i++) px[i] = new Color32(110, 95, 75, 255); // earthy brown
            tex.SetPixels32(px);
            tex.Apply();
            tex.filterMode = FilterMode.Point;
            tex.hideFlags   = HideFlags.HideAndDontSave;

            var sprite = Sprite.Create(tex,
                                        new Rect(0, 0, 16, 16),
                                        new Vector2(0.5f, 0.5f),
                                        16f);
            sprite.hideFlags = HideFlags.HideAndDontSave;

            _placeholderTile                = ScriptableObject.CreateInstance<Tile>();
            _placeholderTile.hideFlags      = HideFlags.HideAndDontSave;
            _placeholderTile.sprite         = sprite;
            _placeholderTile.colliderType   = Tile.ColliderType.Grid;
        }

        private static float ComputeEnemySpawnY(Transform _, float floorTopY)
        {
            // The auto-added CapsuleCollider2D in EnemyController has height 1.8 → halfHeight 0.9.
            // We cannot reliably read col.bounds here because EnemyController.Awake() may not have
            // run yet (Awake order is not guaranteed). Use the known design constant instead.
            const float halfH = 0.9f;
            const float skin  = 0.05f;
            return floorTopY + halfH + skin;
        }

        private Vector3 ResolveEnemySpawnPosition(Transform enemy, Vector3 desiredCenter)
        {
            if (terrainTilemap == null || enemy == null)
                return desiredCenter;

            Vector2 halfExtents = GetEnemyHalfExtents(enemy);

            float[] xOffsets = { 0f, 0.5f, -0.5f, 1f, -1f, 1.5f, -1.5f };
            const int verticalChecks = 8;
            const float yStep = 0.45f;

            for (int vy = 0; vy < verticalChecks; vy++)
            {
                float y = desiredCenter.y + vy * yStep;
                for (int xi = 0; xi < xOffsets.Length; xi++)
                {
                    float x = desiredCenter.x + xOffsets[xi];
                    var candidate = new Vector3(x, y, desiredCenter.z);

                    if (IntersectsTerrainTiles(candidate, halfExtents)) continue;
                    if (!HasGroundSupport(candidate, halfExtents)) continue;
                    return candidate;
                }
            }

            return desiredCenter + new Vector3(0f, 1.5f, 0f);
        }

        private Vector3 ResolveAirEnemySpawnPosition(Transform enemy, Vector3 desiredCenter)
        {
            if (terrainTilemap == null || enemy == null)
                return desiredCenter;

            Vector2 halfExtents = GetEnemyHalfExtents(enemy);

            float[] xOffsets = { 0f, 0.75f, -0.75f, 1.5f, -1.5f, 2.25f, -2.25f };
            const int verticalChecks = 10;
            const float yStep = 0.6f;

            for (int vy = 0; vy < verticalChecks; vy++)
            {
                float y = desiredCenter.y + vy * yStep;
                for (int xi = 0; xi < xOffsets.Length; xi++)
                {
                    float x = desiredCenter.x + xOffsets[xi];
                    var candidate = new Vector3(x, y, desiredCenter.z);
                    if (IntersectsTerrainTiles(candidate, halfExtents)) continue;
                    return candidate;
                }
            }

            return desiredCenter + new Vector3(0f, 3f, 0f);
        }

        private static Vector2 GetEnemyHalfExtents(Transform enemy)
        {
            const float fallbackHalfW = 0.4f;
            const float fallbackHalfH = 0.9f;

            if (enemy == null) return new Vector2(fallbackHalfW, fallbackHalfH);

            Vector3 scale = enemy.lossyScale;
            scale.x = Mathf.Abs(scale.x);
            scale.y = Mathf.Abs(scale.y);

            var cap = enemy.GetComponent<CapsuleCollider2D>();
            if (cap != null)
            {
                return new Vector2(
                    Mathf.Max(0.1f, cap.size.x * 0.5f * scale.x),
                    Mathf.Max(0.2f, cap.size.y * 0.5f * scale.y));
            }

            var box = enemy.GetComponent<BoxCollider2D>();
            if (box != null)
            {
                return new Vector2(
                    Mathf.Max(0.1f, box.size.x * 0.5f * scale.x),
                    Mathf.Max(0.2f, box.size.y * 0.5f * scale.y));
            }

            return new Vector2(fallbackHalfW, fallbackHalfH);
        }

        private bool IntersectsTerrainTiles(Vector3 center, Vector2 halfExtents)
        {
            Vector2 min = (Vector2)center - halfExtents * 0.92f;
            Vector2 max = (Vector2)center + halfExtents * 0.92f;

            Vector3Int minCell = terrainTilemap.WorldToCell(min);
            Vector3Int maxCell = terrainTilemap.WorldToCell(max);

            for (int x = minCell.x; x <= maxCell.x; x++)
            {
                for (int y = minCell.y; y <= maxCell.y; y++)
                {
                    if (terrainTilemap.HasTile(new Vector3Int(x, y, 0)))
                        return true;
                }
            }

            return false;
        }

        private bool HasGroundSupport(Vector3 center, Vector2 halfExtents)
        {
            float footY = center.y - halfExtents.y - 0.08f;

            float x0 = center.x;
            float x1 = center.x - halfExtents.x * 0.6f;
            float x2 = center.x + halfExtents.x * 0.6f;

            return HasTileAtWorld(x0, footY) || HasTileAtWorld(x1, footY) || HasTileAtWorld(x2, footY);
        }

        private bool HasTileAtWorld(float x, float y)
        {
            if (terrainTilemap == null) return false;
            Vector3Int cell = terrainTilemap.WorldToCell(new Vector3(x, y, 0f));
            return terrainTilemap.HasTile(cell);
        }
    }
}
