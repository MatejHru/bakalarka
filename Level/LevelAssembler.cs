using UnityEngine;
using UnityEngine.Tilemaps;

namespace Level
{
    /// <summary>
    /// Applies a <see cref="LevelBundle"/> to the Unity scene.
    ///
    /// Background: scales to fill screen and applies parallax via <see cref="ParallaxBackground"/>.
    ///
    /// Tiles: one generated terrain block is applied to one logical terrain tile.
    ///   - terrainTilemap contains both the floor and floating platforms.
    ///   - terrainBlockPPU controls how many pixels map to 1 Unity unit.
    ///   - A 256x128 texture at terrainBlockPPU=128 becomes exactly 2x1 Unity units.
    ///
    /// Sprites: player, enemies, pickups — white background removed by TextureUtils.
    ///
    /// Wire all references in the Inspector or via PlatformerSceneSetup.
    /// </summary>
    public class LevelAssembler : MonoBehaviour
    {
        [Header("Background")]
        public SpriteRenderer backgroundRenderer;

        [Header("Tilemaps")]
        public Tilemap terrainTilemap;

        [Header("Sprites")]
        [Tooltip("SpriteRenderer on the Player GameObject.")]
        public SpriteRenderer playerRenderer;

        [Tooltip("SpriteRenderers on every ground Enemy GameObject (share the same skin).")]
        public SpriteRenderer[] enemyRenderers;

        [Tooltip("SpriteRenderers on every FlyingEnemy GameObject (share the same skin).")]
        public SpriteRenderer[] flyingEnemyRenderers;

        [Tooltip("SpriteRenderers on every ShootingEnemy GameObject (share the same skin).")]
        public SpriteRenderer[] shootingEnemyRenderers;

        [Tooltip("Optional boss SpriteRenderer (uses enemy skin by default).")]
        public SpriteRenderer bossRenderer;

        [Tooltip("SpriteRenderers on every Pickup GameObject (share the same skin).")]
        public SpriteRenderer[] pickupRenderers;

        [Header("Pixels Per Unit")]
        [Tooltip("PPU for the background sprite (lower = larger visible area).")]
        public float backgroundPPU = 100f;

        [Tooltip("PPU for terrain block sprites. Usually equals generation resolution (e.g. 512).")]
        public float terrainBlockPPU = 512f;

        [Tooltip("PPU for character/pickup sprites. If character is 512px, PPU=256 makes it 2 units tall.")]
        public float spritePPU = 256f;

        // ── Runtime sprite/texture references ─────────────────────────────────
        private Sprite    _bgSprite;
        private Sprite    _terrainSprite;
        private Sprite    _playerSprite;
        private Sprite    _pickupSprite;
        private Texture2D _bgTex, _terrainTex, _playerTex, _pickupTex;

        private void Start()
        {
            EnsurePhysics(terrainTilemap, "Terrain");
            ApplyPlaceholderBackground();
        }

        /// <summary>Creates a simple sky-to-ground gradient so the screen is not black before AI loads.</summary>
        private void ApplyPlaceholderBackground()
        {
            if (backgroundRenderer == null || backgroundRenderer.sprite != null) return;

            const int W = 64, H = 32;
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
            tex.hideFlags = HideFlags.HideAndDontSave;

            var skyTop    = new Color(0.28f, 0.52f, 0.82f);
            var skyBottom = new Color(0.55f, 0.72f, 0.90f);
            var gndTop    = new Color(0.22f, 0.48f, 0.14f);
            var gndBottom = new Color(0.14f, 0.30f, 0.09f);

            for (int y = 0; y < H; y++)
            {
                float t = (float)y / H;
                Color c = t > 0.25f
                    ? Color.Lerp(skyBottom, skyTop, (t - 0.25f) / 0.75f)
                    : Color.Lerp(gndBottom, gndTop, t / 0.25f);
                for (int x = 0; x < W; x++) tex.SetPixel(x, y, c);
            }
            tex.Apply();

            _bgTex    = tex;
            _bgSprite = ToSprite(tex, 16f);
            backgroundRenderer.sprite = _bgSprite;

            var parallax = backgroundRenderer.GetComponent<ParallaxBackground>();
            if (parallax != null) parallax.FitToScreen();
        }

        // ── Public API ────────────────────────────────────────────────────────

        public void ApplyBundle(LevelBundle bundle)
        {
            if (bundle == null)
            {
                Debug.LogWarning("[LevelAssembler] ApplyBundle called with null bundle.");
                return;
            }

            // Background — also triggers parallax rescale
            if (bundle.background != null && backgroundRenderer != null)
            {
                _bgTex = bundle.background;
                _bgSprite = ToSprite(_bgTex, backgroundPPU);
                backgroundRenderer.sprite = _bgSprite;
                // Tell ParallaxBackground to refit the sprite to the current screen size
                var parallax = backgroundRenderer.GetComponent<ParallaxBackground>();
                if (parallax != null) parallax.FitToScreen();
            }

            // Single terrain texture — used for both floor and floating platforms.
            if (bundle.terrainTile != null)
            {
                _terrainTex = bundle.terrainTile;
                ApplyTileTexture(terrainTilemap, _terrainTex, "Terrain");
            }

            // Player skin
            if (bundle.playerSkin != null && playerRenderer != null)
            {
                _playerTex = bundle.playerSkin;
                // Force player to be strictly 2 units tall regardless of texture resolution or inspector PPU
                float computedPpu = _playerTex.height / 2f; 
                _playerSprite = ToSprite(_playerTex, computedPpu);
                playerRenderer.sprite = _playerSprite;
            }

            // Apply type-specific enemy skins
            if (bundle.groundEnemySkin != null && enemyRenderers != null)
                foreach (var er in enemyRenderers)
                    if (er != null) er.sprite = ToSprite(bundle.groundEnemySkin, bundle.groundEnemySkin.height / 1.8f);
            if (bundle.flyingEnemySkin != null && flyingEnemyRenderers != null)
                foreach (var er in flyingEnemyRenderers)
                    if (er != null) er.sprite = ToSprite(bundle.flyingEnemySkin, bundle.flyingEnemySkin.height / 1.8f);
            if (bundle.shootingEnemySkin != null && shootingEnemyRenderers != null)
                foreach (var er in shootingEnemyRenderers)
                    if (er != null) er.sprite = ToSprite(bundle.shootingEnemySkin, bundle.shootingEnemySkin.height / 1.8f);
            if (bossRenderer != null)
            {
                if (bundle.bossEnemySkin != null)
                    bossRenderer.sprite = ToSprite(bundle.bossEnemySkin, bundle.bossEnemySkin.height / 3.2f);
                else if (bundle.groundEnemySkin != null)
                    bossRenderer.sprite = ToSprite(bundle.groundEnemySkin, bundle.groundEnemySkin.height / 2.6f);
            }

            Game.GameSessionState.SetCurrentProjectileSkin(bundle.shootingProjectileSkin);
            // Pickup skins
            if (bundle.pickupSkin != null && pickupRenderers != null)
            {
                _pickupTex = bundle.pickupSkin;
                // Force pickup to be 1 unit tall
                float computedPpu = _pickupTex.height / 1f;
                _pickupSprite = ToSprite(_pickupTex, computedPpu);
                foreach (SpriteRenderer pr in pickupRenderers)
                    if (pr != null) pr.sprite = _pickupSprite;
            }
        }

        // ── Internal helpers ──────────────────────────────────────────────────

        /// <summary>
        /// Replace every logical terrain tile with a block sprite.
        /// The generated terrain texture is 128x128 and with terrainBlockPPU=128 it becomes
        /// exactly 1x1 Unity units, i.e. one block per tilemap cell.
        /// </summary>
        private void ApplyTileTexture(Tilemap tm, Texture2D tex, string label)
        {
            if (tm == null)
            {
                Debug.LogWarning($"[LevelAssembler] {label} tilemap not assigned!");
                return;
            }

            tex = CropTileTexture(tex);
            if (tex == null)
            {
                return;
            }

            if (IsWeakTerrainTexture(tex))
            {
                Debug.LogWarning($"[LevelAssembler] {label} texture looked weak/noisy. Using stable fallback tile.");
                tex = BuildFallbackTerrainTile(tex.width > 0 ? tex.width : 128, tex, Game.GameSessionState.BaseTheme);
            }

            // PPU = texture size so tile is always exactly 1x1 world unit
            float ppu = tex.width;

            var tile = ScriptableObject.CreateInstance<Tile>();
            tile.hideFlags    = HideFlags.HideAndDontSave;
            var tileSprite    = MakeTileSprite(tex, ppu);
            tile.sprite       = tileSprite;
            tile.colliderType = Tile.ColliderType.Sprite;

            _terrainSprite = tileSprite;

            BoundsInt bounds = tm.cellBounds;
            int count = 0;
            foreach (Vector3Int pos in bounds.allPositionsWithin)
            {
                if (tm.HasTile(pos)) { tm.SetTile(pos, tile); count++; }
            }

            tm.RefreshAllTiles();
            EnsurePhysics(tm, label);
        }

        private void EnsurePhysics(Tilemap tm, string label)
        {
            if (tm == null) return;

            var rb = tm.GetComponent<Rigidbody2D>();
            if (rb == null) { rb = tm.gameObject.AddComponent<Rigidbody2D>(); }
            rb.bodyType = RigidbodyType2D.Static;

            var tmCol = tm.GetComponent<TilemapCollider2D>();
            if (tmCol == null) { tmCol = tm.gameObject.AddComponent<TilemapCollider2D>(); }

            var compCol = tm.GetComponent<CompositeCollider2D>();
            if (compCol == null) { compCol = tm.gameObject.AddComponent<CompositeCollider2D>(); }

            tmCol.isTrigger          = false;
            tmCol.compositeOperation = Collider2D.CompositeOperation.Merge;
            compCol.isTrigger        = false;

            // Zero friction so player never sticks to tile edges or walls
            if (compCol.sharedMaterial == null)
            {
                var noFric = new PhysicsMaterial2D { friction = 0f, bounciness = 0f };
                noFric.hideFlags = HideFlags.HideAndDontSave;
                compCol.sharedMaterial = noFric;
            }

            // Fix tiles that have ColliderType.None (fall-through bug)
            BoundsInt bounds = tm.cellBounds;
            foreach (Vector3Int pos in bounds.allPositionsWithin)
            {
                if (!tm.HasTile(pos)) continue;
                var existing = tm.GetTile(pos) as Tile;
                if (existing != null && existing.colliderType != Tile.ColliderType.Grid)
                {
                    var fixed2 = ScriptableObject.CreateInstance<Tile>();
                    fixed2.hideFlags    = HideFlags.HideAndDontSave;
                    fixed2.sprite       = tm.GetSprite(pos);
                    fixed2.color        = tm.GetColor(pos);
                    fixed2.colliderType = Tile.ColliderType.Sprite;
                    tm.SetTile(pos, fixed2);
                }
            }

            tm.RefreshAllTiles();
            tmCol.enabled = false;
            tmCol.enabled = true;
            compCol.GenerateGeometry();
            Physics2D.SyncTransforms();
        }

        // ── Texture helpers ───────────────────────────────────────────────────

        /// <summary>
        /// Crops the centre-bottom region of the AI terrain image and returns a square
        /// Texture2D suitable for side-view block tiling.
        /// Uses a RenderTexture GPU blit so it works even when the source is non-readable.
        /// </summary>
        private static Texture2D CropTileTexture(Texture2D src)
        {
            if (src == null) return null;

            int outSize = Mathf.Clamp(Mathf.Min(src.width, src.height), 128, 512);

            // Use the full source area (or a centered square if non-square).
            // Previous aggressive zoom crop produced repeated artifacts from tiny source regions.
            float uScale = (float)outSize / Mathf.Max(1, src.width);
            float vScale = (float)outSize / Mathf.Max(1, src.height);
            float uOff = (1f - uScale) * 0.5f;
            float vOff = (1f - vScale) * 0.5f;

            var rt     = RenderTexture.GetTemporary(outSize, outSize, 0, RenderTextureFormat.ARGB32);
            var prevRt = RenderTexture.active;
            RenderTexture.active = rt;

            Graphics.Blit(src, rt, new Vector2(uScale, vScale), new Vector2(uOff, vOff));

            var result = new Texture2D(outSize, outSize, TextureFormat.RGBA32, false);
            result.hideFlags  = HideFlags.HideAndDontSave;
            result.ReadPixels(new Rect(0, 0, outSize, outSize), 0, 0);
            result.Apply();

            RenderTexture.active = prevRt;
            RenderTexture.ReleaseTemporary(rt);

            result = BuildReadableBlockTile(result);

            // Smooth out seams: blend 1px borders against opposite sides.
            SoftenTileSeams(result);

            result.filterMode = FilterMode.Point;
            result.wrapMode   = TextureWrapMode.Repeat;
            return result;
        }

        private static Texture2D BuildReadableBlockTile(Texture2D source)
        {
            if (source == null || !source.isReadable) return source;

            int s = source.width;
            Color[] px = source.GetPixels();

            // Ensure all pixels are fully opaque — terrain tiles must not have alpha gaps.
            // Do NOT add artificial cap/seam colour shifts: they produce ugly banding when
            // the SD texture already has its own surface detail.
            for (int i = 0; i < px.Length; i++)
            {
                Color c = px[i];
                px[i] = new Color(c.r, c.g, c.b, 1f);
            }

            source.SetPixels(px);
            source.Apply(false, false);
            source.filterMode = FilterMode.Bilinear;
            source.wrapMode   = TextureWrapMode.Repeat;
            return source;
        }

        private static Color AverageRegion(Texture2D tex, float u0, float v0, float u1, float v1)
        {
            int w = tex.width;
            int h = tex.height;
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
                    r += c.r; g += c.g; b += c.b;
                    count++;
                }
            }

            if (count <= 0) return new Color(0.4f, 0.35f, 0.3f, 1f);
            return new Color(r / count, g / count, b / count, 1f);
        }

        private static bool IsWeakTerrainTexture(Texture2D tex)
        {
            if (tex == null || !tex.isReadable) return true;

            Color32[] px = tex.GetPixels32();
            if (px == null || px.Length == 0) return true;

            Color32 first = px[0];
            bool allSame = true;
            int minR = 255, minG = 255, minB = 255;
            int maxR = 0, maxG = 0, maxB = 0;
            int nearWhite = 0;

            for (int i = 0; i < px.Length; i++)
            {
                Color32 c = px[i];
                if (c.r != first.r || c.g != first.g || c.b != first.b) allSame = false;
                if (c.r < minR) minR = c.r; if (c.r > maxR) maxR = c.r;
                if (c.g < minG) minG = c.g; if (c.g > maxG) maxG = c.g;
                if (c.b < minB) minB = c.b; if (c.b > maxB) maxB = c.b;
                if (c.r >= 242 && c.g >= 242 && c.b >= 242) nearWhite++;
            }

            if (allSame) return true;

            int channelRange = (maxR - minR) + (maxG - minG) + (maxB - minB);
            if (channelRange < 30) return true;

            // If a terrain tile is mostly white, SD likely returned an isolated object on white bg.
            // Threshold raised to 0.45 so light-coloured themes (sand, snow, stone, marble)
            // are not incorrectly rejected and replaced with the fallback tile.
            float whiteRatio = (float)nearWhite / px.Length;
            if (whiteRatio > 0.45f) return true;

            // Reject only when both checks agree. A single symmetry signal alone was too strict
            // and discarded many valid themed textures, making results look samey.
            bool decorative = HasDecorativeSymmetry(tex);
            bool centerObj  = HasStrongCenterContrast(tex);
            if (decorative && centerObj) return true;

            // Reject photo-like terrain scenes (horizon bands / strong top-vs-bottom split).
            if (LooksLikeSceneTexture(tex)) return true;

            return false;
        }

        private static bool HasStrongCenterContrast(Texture2D tex)
        {
            if (tex == null || !tex.isReadable) return false;

            Color center = AverageRegion(tex, 0.35f, 0.35f, 0.65f, 0.65f);
            Color edgeA  = AverageRegion(tex, 0.00f, 0.00f, 0.18f, 0.18f);
            Color edgeB  = AverageRegion(tex, 0.82f, 0.00f, 1.00f, 0.18f);
            Color edgeC  = AverageRegion(tex, 0.00f, 0.82f, 0.18f, 1.00f);
            Color edgeD  = AverageRegion(tex, 0.82f, 0.82f, 1.00f, 1.00f);
            Color edge   = (edgeA + edgeB + edgeC + edgeD) * 0.25f;

            float colorDist = Mathf.Abs(center.r - edge.r) + Mathf.Abs(center.g - edge.g) + Mathf.Abs(center.b - edge.b);
            float lumCenter = center.r * 0.2126f + center.g * 0.7152f + center.b * 0.0722f;
            float lumEdge   = edge.r * 0.2126f + edge.g * 0.7152f + edge.b * 0.0722f;
            float lumDist   = Mathf.Abs(lumCenter - lumEdge);

            return colorDist > 0.38f && lumDist > 0.12f;
        }

        private static bool HasDecorativeSymmetry(Texture2D tex)
        {
            if (tex == null || !tex.isReadable) return false;

            Color32[] px = tex.GetPixels32();
            int w = tex.width;
            int h = tex.height;
            int step = Mathf.Clamp(Mathf.Min(w, h) / 64, 2, 8);

            float horiz = AverageMirrorColorDistance(px, w, h, step, mode: 0);
            float vert  = AverageMirrorColorDistance(px, w, h, step, mode: 1);
            float rot90 = AverageMirrorColorDistance(px, w, h, step, mode: 2);

            // Lower sensitivity so natural repeating grain does not get treated as decorative motif.
            return horiz < 56f || vert < 56f || rot90 < 52f;
        }

        private static bool LooksLikeSceneTexture(Texture2D tex)
        {
            if (tex == null || !tex.isReadable) return false;

            Color top = AverageRegion(tex, 0f, 0f, 1f, 0.22f);
            Color mid = AverageRegion(tex, 0f, 0.39f, 1f, 0.61f);
            Color bot = AverageRegion(tex, 0f, 0.78f, 1f, 1f);

            float topMid = Mathf.Abs(top.r - mid.r) + Mathf.Abs(top.g - mid.g) + Mathf.Abs(top.b - mid.b);
            float midBot = Mathf.Abs(mid.r - bot.r) + Mathf.Abs(mid.g - bot.g) + Mathf.Abs(mid.b - bot.b);
            float topBot = Mathf.Abs(top.r - bot.r) + Mathf.Abs(top.g - bot.g) + Mathf.Abs(top.b - bot.b);

            // Broad horizontal scene split (sky/terrain or foreground/background).
            if (topBot > 0.52f && (topMid > 0.20f || midBot > 0.20f))
                return true;

            // Horizon-like low-variance row with strong vertical jump.
            return HasHorizonLikeRow(tex);
        }

        private static bool HasHorizonLikeRow(Texture2D tex)
        {
            if (tex == null || !tex.isReadable) return false;

            Color32[] px = tex.GetPixels32();
            int w = tex.width;
            int h = tex.height;
            if (w < 32 || h < 16) return false;

            float[] lum = new float[h];
            float[] var = new float[h];

            for (int y = 0; y < h; y++)
            {
                float sum = 0f;
                float sum2 = 0f;
                for (int x = 0; x < w; x++)
                {
                    Color32 c = px[y * w + x];
                    float l = (0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b) / 255f;
                    sum += l;
                    sum2 += l * l;
                }
                float mean = sum / w;
                lum[y] = mean;
                var[y] = Mathf.Max(0f, (sum2 / w) - (mean * mean));
            }

            int start = Mathf.FloorToInt(h * 0.18f);
            int end = Mathf.CeilToInt(h * 0.82f);
            for (int y = start; y < end - 1; y++)
            {
                float jump = Mathf.Abs(lum[y + 1] - lum[y]);
                if (jump > 0.08f && var[y] < 0.0035f)
                    return true;
            }

            return false;
        }

        private static float AverageMirrorColorDistance(Color32[] px, int w, int h, int step, int mode)
        {
            int total = 0;
            long sum = 0;

            for (int y = 0; y < h; y += step)
            {
                for (int x = 0; x < w; x += step)
                {
                    int mx = x;
                    int my = y;

                    if (mode == 0)
                    {
                        mx = w - 1 - x;
                    }
                    else if (mode == 1)
                    {
                        my = h - 1 - y;
                    }
                    else
                    {
                        // 90-degree rotational match around center.
                        mx = w - 1 - y;
                        my = x;
                        if (mx < 0 || mx >= w || my < 0 || my >= h) continue;
                    }

                    int i0 = y * w + x;
                    int i1 = my * w + mx;
                    Color32 a = px[i0];
                    Color32 b = px[i1];
                    sum += Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b);
                    total++;
                }
            }

            if (total <= 0) return 255f;
            return (float)sum / total;
        }

        private static void SoftenTileSeams(Texture2D tex)
        {
            if (tex == null || !tex.isReadable) return;
            int w = tex.width;
            int h = tex.height;
            if (w < 4 || h < 4) return;

            Color32[] px = tex.GetPixels32();

            for (int y = 0; y < h; y++)
            {
                int left = y * w;
                int right = y * w + (w - 1);
                Color32 blended = Color32.Lerp(px[left], px[right], 0.5f);
                px[left] = blended;
                px[right] = blended;
            }

            for (int x = 0; x < w; x++)
            {
                int bottom = x;
                int top = (h - 1) * w + x;
                Color32 blended = Color32.Lerp(px[bottom], px[top], 0.5f);
                px[bottom] = blended;
                px[top] = blended;
            }

            tex.SetPixels32(px);
            tex.Apply(false, false);
        }

        private static Texture2D BuildFallbackTerrainTile(int size, Texture2D source, string theme)
        {
            int s = Mathf.Clamp(size, 64, 512);
            var tex = new Texture2D(s, s, TextureFormat.RGBA32, false);
            tex.hideFlags = HideFlags.HideAndDontSave;
            tex.wrapMode = TextureWrapMode.Repeat;
            tex.filterMode = FilterMode.Point;

            Color themeA = ThemeColorFromString(theme, 0.45f, 0.40f, 0);
            Color themeB = ThemeColorFromString(theme, 0.35f, 0.58f, 1);
            Color themeAccent = ThemeColorFromString(theme, 0.62f, 0.78f, 2);

            Color baseA = themeA;
            Color baseB = themeB;
            Color accent = themeAccent;

            if (source != null && source.isReadable)
            {
                Color avg = AverageRegion(source, 0f, 0f, 1f, 1f);
                Color center = AverageRegion(source, 0.30f, 0.30f, 0.70f, 0.70f);
                Color.RGBToHSV(avg, out _, out float sat, out _);

                if (sat < 0.12f)
                {
                    // Source is too gray/washed out — keep strong theme palette.
                    baseA = themeA;
                    baseB = themeB;
                    accent = themeAccent;
                }
                else
                {
                    baseA = Color.Lerp(themeA, Color.Lerp(avg, Color.black, 0.30f), 0.70f);
                    baseB = Color.Lerp(themeB, Color.Lerp(avg, Color.white, 0.18f), 0.70f);
                    accent = Color.Lerp(themeAccent, Color.Lerp(center, avg, 0.45f), 0.65f);
                }

                baseA.a = baseB.a = accent.a = 1f;
            }

            for (int y = 0; y < s; y++)
            {
                float v = (float)y / (s - 1);
                for (int x = 0; x < s; x++)
                {
                    float u = (float)x / (s - 1);
                    float macro = Mathf.PerlinNoise(u * 4.5f + 9.1f, v * 4.5f + 2.7f);
                    float micro = Mathf.PerlinNoise(u * 19.3f + 3.2f, v * 19.3f + 8.6f);
                    float t = Mathf.Clamp01((macro * 0.8f) + ((micro - 0.5f) * 0.25f));
                    Color c = Color.Lerp(baseA, baseB, t);
                    float accentNoise = Mathf.PerlinNoise(u * 11.7f + 1.4f, v * 11.7f + 6.9f);
                    if (accentNoise > 0.79f) c = Color.Lerp(c, accent, 0.35f);
                    c.a = 1f;
                    tex.SetPixel(x, y, c);
                }
            }

            tex.Apply(false, false);
            SoftenTileSeams(tex);
            return tex;
        }

        private static Color ThemeColorFromString(string theme, float saturation, float value, int salt)
        {
            string t = string.IsNullOrWhiteSpace(theme) ? "fantasy" : theme.Trim().ToLowerInvariant();
            int hash = Mathf.Abs((t + "|" + salt).GetHashCode());
            float hue = (hash % 360) / 360f;
            return Color.HSVToRGB(hue, Mathf.Clamp01(saturation), Mathf.Clamp01(value));
        }

        // ── Sprite helpers ────────────────────────────────────────────────────

        /// <summary>Generic sprite — bilinear, clamped (for background and character sprites).</summary>
        private static Sprite ToSprite(Texture2D tex, float ppu)
        {
            if (tex == null) return null;
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode   = TextureWrapMode.Clamp;
            var sp = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height),
                                   new Vector2(0.5f, 0.5f), ppu);
            // Prevent Unity's asset GC from silently destroying runtime sprites
            // (root cause of the "background disappears then tiles vanish" glitch).
            sp.hideFlags = HideFlags.HideAndDontSave;
            return sp;
        }

        /// <summary>
        /// Tile sprite — point filter, repeat wrap.
        /// Setting ppu = texture pixel width makes 1 tile cell = 1 full texture image.
        /// </summary>
        private static Sprite MakeTileSprite(Texture2D tex, float ppu)
        {
            if (tex == null) return null;
            tex.filterMode = FilterMode.Point;
            tex.wrapMode   = TextureWrapMode.Repeat;
            var sp = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height),
                                   new Vector2(0.5f, 0.5f), ppu);
            sp.hideFlags = HideFlags.HideAndDontSave;
            return sp;
        }

        /// <summary>
        /// Rebuild the terrain tilemap's physics colliders after the level generator
        /// has placed new tiles. Called by <see cref="Game.GameController"/> immediately
        /// after <see cref="Level.LevelGenerator.Generate"/>.
        /// </summary>
        public void RebuildColliders() => EnsurePhysics(terrainTilemap, "Terrain");
    }
}