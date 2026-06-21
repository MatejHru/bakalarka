using System.Collections.Generic;
using UnityEngine;

namespace AI
{
    /// <summary>
    /// Sprite / texture post-processing helpers ported from the old AiArtPipeline.
    /// Static utilities used by LevelGenerationOrchestrator.
    /// </summary>
    public static class TextureUtils
    {
        // ── Sprite creation ────────────────────────────────────────────────
        public static Sprite ToSprite(Texture2D tex, float pixelsPerUnit = 100f,
                                      TextureWrapMode wrap = TextureWrapMode.Clamp)
        {
            if (tex == null) return null;
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode   = wrap;
            return Sprite.Create(tex,
                new Rect(0, 0, tex.width, tex.height),
                new Vector2(0.5f, 0.5f),
                pixelsPerUnit);
        }

        public static Sprite ToTileSprite(Texture2D tex, float pixelsPerUnit = 64f)
            => ToSprite(tex, pixelsPerUnit, TextureWrapMode.Repeat);

        // ── Validate Texture ────────────────────────────────────────────────
        public static bool IsValidTexture(Texture2D tex)
        {
            if (tex == null || !tex.isReadable) return false;
            Color32[] pixels = tex.GetPixels32();
            if (pixels.Length == 0) return false;
            
            Color32 first = pixels[0];
            bool   allSame = true;
            for (int i = 1; i < pixels.Length; i++)
            {
                if (pixels[i].r != first.r || pixels[i].g != first.g || 
                    pixels[i].b != first.b || pixels[i].a != first.a)
                {
                    allSame = false;
                    break;
                }
            }
            if (allSame)
            {
                Debug.LogError($"[TextureUtils] Generated texture is a SOLID COLOR (r:{first.r} g:{first.g} b:{first.b} a:{first.a}). " +
                               "This usually means Stable Diffusion ran out of VRAM (--no-half-vae needed) or the NSFW checker triggered! Rejecting texture.");
                return false;
            }
            return true;
        }

        // ── Transparent background removal (flood-fill from border) ────────
        /// <summary>
        /// Removes the solid-color background from an SD-generated sprite texture.
        ///
        /// Two-pass approach:
        ///   1. Tight flood-fill (threshold 20) from the image border removes only pixels
        ///      that are very close to the estimated background color.  The tight threshold
        ///      prevents the flood from passing through thin body parts or light-coloured fur.
        ///   2. Edge erosion (2 passes, threshold 28) cleans up the thin fringe the tight
        ///      flood leaves near subject edges without entering character interiors.
        ///
        /// A fail-safe reverts the texture if more than 97 % became transparent.
        /// </summary>
        public static void RemoveBackground(Texture2D texture)
        {
            if (texture == null || !texture.isReadable) return;
            int w = texture.width;
            int h = texture.height;
            if (w < 8 || h < 8) return;

            Color32[] pixels = texture.GetPixels32();
            var original = new Color32[pixels.Length];
            System.Array.Copy(pixels, original, pixels.Length);

            // Sample a small corner region for a robust background estimate.
            Color32 bg = EstimateBackgroundColor(pixels, w, h);
            int brightness = (bg.r + bg.g + bg.b) / 3;

            // floodThreshSq  — controls which pixels the border flood can pass through.
            //   Must be TIGHT: prevents flood from entering body parts that are near-white.
            //   20 units from white means R,G,B must all be ≥ 243 to be flooded.
            // edgeThreshSq   — controls edge-erosion and the corner sanity check.
            //   More lenient so fringe anti-aliased pixels are cleaned up.
            int floodThreshSq = brightness > 200 ? 20 * 20 : 14 * 14;
            int edgeThreshSq  = brightness > 200 ? 36 * 36 : 26 * 26;

            // Abort if the image corners do not confirm a uniform removable background.
            if (!LooksLikeRemovableBackground(pixels, w, h, bg, edgeThreshSq))
                return;

            // ── Pass 1: tight border flood-fill ───────────────────────────────
            bool[] visited = new bool[pixels.Length];
            var    queue   = new Queue<int>(w * 2 + h * 2);

            EnqueueBorder(queue, visited, pixels, w, h, bg, floodThreshSq);

            while (queue.Count > 0)
            {
                int idx = queue.Dequeue();
                pixels[idx].a = 0;
                int x = idx % w;
                int y = idx / w;
                TryVisit(x - 1, y, w, h, pixels, visited, queue, bg, floodThreshSq);
                TryVisit(x + 1, y, w, h, pixels, visited, queue, bg, floodThreshSq);
                TryVisit(x, y - 1, w, h, pixels, visited, queue, bg, floodThreshSq);
                TryVisit(x, y + 1, w, h, pixels, visited, queue, bg, floodThreshSq);
            }

            // ── Pass 2: edge erosion (2×) — remove near-bg fringe ─────────────
            // Only erodes pixels that (a) border a transparent pixel AND
            // (b) are within edgeThreshSq of the background.
            // Interior character pixels are never adjacent to transparent, so they survive.
            for (int pass = 0; pass < 2; pass++)
            {
                for (int i = 0; i < pixels.Length; i++)
                {
                    if (pixels[i].a == 0) continue;                      // already transparent
                    if (ColorDistSq(pixels[i], bg) > edgeThreshSq) continue; // clearly subject-coloured
                    int xi = i % w, yi = i / w;
                    if (HasTransparentNeighbour4(pixels, xi, yi, w, h))
                        pixels[i].a = 0;
                }
            }

            // ── Fail-safe ─────────────────────────────────────────────────────
            if (!HasSufficientOpaqueArea(pixels, w, h))
            {
                texture.SetPixels32(original);
                texture.Apply(false, false);
                return;
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, false);
        }

        public static Texture2D CropToOpaqueBounds(Texture2D texture, int alphaThreshold = 8, int padding = 10)
        {
            if (texture == null || !texture.isReadable) return texture;

            Color32[] pixels = texture.GetPixels32();
            int w = texture.width;
            int h = texture.height;
            int minX = w, minY = h, maxX = -1, maxY = -1;

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (pixels[y * w + x].a <= alphaThreshold) continue;
                    if (x < minX) minX = x;
                    if (y < minY) minY = y;
                    if (x > maxX) maxX = x;
                    if (y > maxY) maxY = y;
                }
            }

            if (maxX < minX || maxY < minY) return texture;

            minX = Mathf.Max(0, minX - padding);
            minY = Mathf.Max(0, minY - padding);
            maxX = Mathf.Min(w - 1, maxX + padding);
            maxY = Mathf.Min(h - 1, maxY + padding);

            int outW = maxX - minX + 1;
            int outH = maxY - minY + 1;
            if (outW == w && outH == h) return texture;

            var cropped = new Texture2D(outW, outH, TextureFormat.RGBA32, false);
            cropped.hideFlags = HideFlags.HideAndDontSave;
            cropped.filterMode = texture.filterMode;
            cropped.wrapMode = TextureWrapMode.Clamp;
            var croppedPixels = new Color32[outW * outH];
            for (int y = 0; y < outH; y++)
            {
                int srcRow = (minY + y) * w + minX;
                int dstRow = y * outW;
                for (int x = 0; x < outW; x++)
                    croppedPixels[dstRow + x] = pixels[srcRow + x];
            }
            cropped.SetPixels32(croppedPixels);
            cropped.Apply(false, false);
            return cropped;
        }

        /// <summary>
        /// Keeps only one connected opaque component and clears all others.
        /// This removes duplicate characters/items when SD returns collage-like outputs.
        /// </summary>
        public static Texture2D KeepDominantOpaqueComponent(Texture2D texture, int alphaThreshold = 8)
        {
            if (texture == null || !texture.isReadable) return texture;

            Color32[] pixels = texture.GetPixels32();
            int w = texture.width;
            int h = texture.height;
            int n = pixels.Length;
            if (n == 0) return texture;

            bool[] opaque = new bool[n];
            bool anyOpaque = false;
            for (int i = 0; i < n; i++)
            {
                bool isOpaque = pixels[i].a > alphaThreshold;
                opaque[i] = isOpaque;
                if (isOpaque) anyOpaque = true;
            }

            if (!anyOpaque) return texture;

            bool[] visited = new bool[n];
            int[] componentOf = new int[n];
            for (int i = 0; i < n; i++) componentOf[i] = -1;

            int cx = w / 2;
            int cy = h / 2;
            float maxDistSq = Mathf.Max(1f, cx * cx + cy * cy);

            int bestComponent = -1;
            float bestScore = float.MinValue;
            int componentIndex = 0;
            var queue = new Queue<int>(Mathf.Min(4096, n));

            for (int i = 0; i < n; i++)
            {
                if (!opaque[i] || visited[i]) continue;

                int count = 0;
                long sumX = 0;
                long sumY = 0;

                visited[i] = true;
                queue.Enqueue(i);

                while (queue.Count > 0)
                {
                    int idx = queue.Dequeue();
                    componentOf[idx] = componentIndex;
                    count++;

                    int x = idx % w;
                    int y = idx / w;
                    sumX += x;
                    sumY += y;

                    EnqueueOpaqueNeighbour(x - 1, y,     w, h, opaque, visited, queue);
                    EnqueueOpaqueNeighbour(x + 1, y,     w, h, opaque, visited, queue);
                    EnqueueOpaqueNeighbour(x,     y - 1, w, h, opaque, visited, queue);
                    EnqueueOpaqueNeighbour(x,     y + 1, w, h, opaque, visited, queue);
                    // Diagonal neighbours (8-connectivity) keep diagonally-touching
                    // body parts (curved arms, hair, accessories) in the same component.
                    EnqueueOpaqueNeighbour(x - 1, y - 1, w, h, opaque, visited, queue);
                    EnqueueOpaqueNeighbour(x + 1, y - 1, w, h, opaque, visited, queue);
                    EnqueueOpaqueNeighbour(x - 1, y + 1, w, h, opaque, visited, queue);
                    EnqueueOpaqueNeighbour(x + 1, y + 1, w, h, opaque, visited, queue);
                }

                float centroidX = (float)sumX / Mathf.Max(1, count);
                float centroidY = (float)sumY / Mathf.Max(1, count);
                float dx = centroidX - cx;
                float dy = centroidY - cy;
                float centerBias = 1f - Mathf.Clamp01((dx * dx + dy * dy) / maxDistSq);

                // Area dominates, center proximity breaks ties for multi-subject renders.
                float score = count * (1.0f + 0.45f * centerBias);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestComponent = componentIndex;
                }

                componentIndex++;
            }

            if (bestComponent < 0) return texture;

            for (int i = 0; i < n; i++)
            {
                if (opaque[i] && componentOf[i] != bestComponent)
                    pixels[i].a = 0;
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            return texture;
        }

        /// <summary>
        /// Smooths alpha-channel edges by averaging each pixel's alpha with its neighbors.
        /// Reduces the jagged, staircase edges left after color-key removal.
        /// Only pixels near the alpha boundary (not fully opaque and not fully transparent) are affected.
        /// </summary>
        public static void FeatherAlphaEdges(Texture2D texture, int radius = 1)
        {
            if (texture == null || !texture.isReadable) return;

            Color32[] pixels = texture.GetPixels32();
            int w = texture.width;
            int h = texture.height;
            byte[] alphaIn = new byte[pixels.Length];
            for (int i = 0; i < pixels.Length; i++) alphaIn[i] = pixels[i].a;

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int idx = y * w + x;
                    byte a = alphaIn[idx];
                    // Only process boundary pixels (not fully transparent or fully opaque)
                    if (a == 0 || a == 255) continue;

                    int sum = 0;
                    int count = 0;
                    for (int dy = -radius; dy <= radius; dy++)
                    {
                        int ny = y + dy;
                        if (ny < 0 || ny >= h) continue;
                        for (int dx = -radius; dx <= radius; dx++)
                        {
                            int nx = x + dx;
                            if (nx < 0 || nx >= w) continue;
                            sum += alphaIn[ny * w + nx];
                            count++;
                        }
                    }
                    pixels[idx].a = (byte)(sum / Mathf.Max(1, count));
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, false);
        }

        /// <summary>
        /// Removes border-connected pixels close to a known solid background color.
        /// Useful when prompts force a uniform white/black/chroma background.
        /// </summary>
        public static void RemoveBackgroundByColorKey(Texture2D texture,
                                                      Color32 keyColor,
                                                      int threshold = 24,
                                                      int edgeThreshold = 32,
                                                      int edgePasses = 1)
        {
            if (texture == null || !texture.isReadable) return;

            int w = texture.width;
            int h = texture.height;
            if (w < 4 || h < 4) return;

            Color32[] pixels = texture.GetPixels32();
            bool[] visited = new bool[pixels.Length];
            var queue = new Queue<int>(w * 2 + h * 2);

            int thrSq = threshold * threshold;
            int edgeSq = edgeThreshold * edgeThreshold;

            for (int x = 0; x < w; x++)
            {
                TryVisitColorKey(x, 0, w, h, pixels, visited, queue, keyColor, thrSq);
                TryVisitColorKey(x, h - 1, w, h, pixels, visited, queue, keyColor, thrSq);
            }
            for (int y = 1; y < h - 1; y++)
            {
                TryVisitColorKey(0, y, w, h, pixels, visited, queue, keyColor, thrSq);
                TryVisitColorKey(w - 1, y, w, h, pixels, visited, queue, keyColor, thrSq);
            }

            while (queue.Count > 0)
            {
                int idx = queue.Dequeue();
                pixels[idx].a = 0;
                int x = idx % w;
                int y = idx / w;

                TryVisitColorKey(x - 1, y, w, h, pixels, visited, queue, keyColor, thrSq);
                TryVisitColorKey(x + 1, y, w, h, pixels, visited, queue, keyColor, thrSq);
                TryVisitColorKey(x, y - 1, w, h, pixels, visited, queue, keyColor, thrSq);
                TryVisitColorKey(x, y + 1, w, h, pixels, visited, queue, keyColor, thrSq);
            }

            for (int pass = 0; pass < edgePasses; pass++)
            {
                for (int i = 0; i < pixels.Length; i++)
                {
                    if (pixels[i].a == 0) continue;
                    if (ColorDistSq(pixels[i], keyColor) > edgeSq) continue;
                    int xi = i % w;
                    int yi = i / w;
                    if (HasTransparentNeighbour4(pixels, xi, yi, w, h))
                        pixels[i].a = 0;
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, false);
        }

        public static bool HasOpaqueCoverage(Texture2D texture,
                                             int alphaThreshold = 12,
                                             float minRatio = 0.03f,
                                             int minPixels = 64)
        {
            if (texture == null || !texture.isReadable) return false;

            Color32[] pixels = texture.GetPixels32();
            if (pixels == null || pixels.Length == 0) return false;

            int opaque = 0;
            for (int i = 0; i < pixels.Length; i++)
            {
                if (pixels[i].a > alphaThreshold) opaque++;
            }

            float ratio = (float)opaque / pixels.Length;
            return opaque >= minPixels && ratio >= minRatio;
        }

        public static bool IsMostlyNearColor(Texture2D texture,
                                             Color32 color,
                                             int threshold = 24,
                                             float ratio = 0.55f)
        {
            if (texture == null || !texture.isReadable) return false;

            Color32[] pixels = texture.GetPixels32();
            if (pixels == null || pixels.Length == 0) return false;

            int thrSq = threshold * threshold;
            int matches = 0;
            for (int i = 0; i < pixels.Length; i++)
            {
                if (ColorDistSq(pixels[i], color) <= thrSq)
                    matches++;
            }

            return (float)matches / pixels.Length >= ratio;
        }

        public static Color32 EstimateCornerColor(Texture2D texture)
        {
            if (texture == null || !texture.isReadable)
                return new Color32(255, 255, 255, 255);

            Color32[] pixels = texture.GetPixels32();
            int w = texture.width;
            int h = texture.height;
            int s = Mathf.Clamp(Mathf.Min(w, h) / 10, 3, 24);

            long r = 0, g = 0, b = 0, count = 0;
            for (int cy = 0; cy < s; cy++)
            {
                for (int cx = 0; cx < s; cx++)
                {
                    Color32 c = pixels[cy * w + cx];
                    r += c.r; g += c.g; b += c.b; count++;

                    c = pixels[cy * w + (w - 1 - cx)];
                    r += c.r; g += c.g; b += c.b; count++;

                    c = pixels[(h - 1 - cy) * w + cx];
                    r += c.r; g += c.g; b += c.b; count++;

                    c = pixels[(h - 1 - cy) * w + (w - 1 - cx)];
                    r += c.r; g += c.g; b += c.b; count++;
                }
            }

            if (count <= 0) return new Color32(255, 255, 255, 255);
            return new Color32((byte)(r / count), (byte)(g / count), (byte)(b / count), 255);
        }

        public static bool CornersMostlyNearColor(Texture2D texture,
                                                  Color32 color,
                                                  int threshold = 30,
                                                  float ratio = 0.70f)
        {
            if (texture == null || !texture.isReadable) return false;

            Color32[] pixels = texture.GetPixels32();
            int w = texture.width;
            int h = texture.height;
            int s = Mathf.Clamp(Mathf.Min(w, h) / 10, 3, 24);
            int thrSq = threshold * threshold;

            int matches = 0;
            int total = 0;

            for (int cy = 0; cy < s; cy++)
            {
                for (int cx = 0; cx < s; cx++)
                {
                    if (ColorDistSq(pixels[cy * w + cx], color) <= thrSq) matches++;
                    if (ColorDistSq(pixels[cy * w + (w - 1 - cx)], color) <= thrSq) matches++;
                    if (ColorDistSq(pixels[(h - 1 - cy) * w + cx], color) <= thrSq) matches++;
                    if (ColorDistSq(pixels[(h - 1 - cy) * w + (w - 1 - cx)], color) <= thrSq) matches++;
                    total += 4;
                }
            }

            return total > 0 && (float)matches / total >= ratio;
        }

        public static bool HasOpaqueCorners(Texture2D texture,
                                            int alphaThreshold = 12,
                                            int cornerSize = 10,
                                            float opaqueRatio = 0.08f)
        {
            if (texture == null || !texture.isReadable) return true;

            Color32[] pixels = texture.GetPixels32();
            int w = texture.width;
            int h = texture.height;
            int s = Mathf.Clamp(cornerSize, 2, Mathf.Max(2, Mathf.Min(w, h) / 4));

            int opaque = 0;
            int total = 0;
            for (int cy = 0; cy < s; cy++)
            {
                for (int cx = 0; cx < s; cx++)
                {
                    if (pixels[cy * w + cx].a > alphaThreshold) opaque++;
                    if (pixels[cy * w + (w - 1 - cx)].a > alphaThreshold) opaque++;
                    if (pixels[(h - 1 - cy) * w + cx].a > alphaThreshold) opaque++;
                    if (pixels[(h - 1 - cy) * w + (w - 1 - cx)].a > alphaThreshold) opaque++;
                    total += 4;
                }
            }

            return total > 0 && (float)opaque / total >= opaqueRatio;
        }

        /// <summary>
        /// Additional cleanup pass for sprites where chroma key did not fully remove
        /// near-uniform corner background (e.g., pale gradients or tinted cards).
        /// </summary>
        public static void RemoveResidualCornerBackground(Texture2D texture,
                                                          int threshold = 40,
                                                          int edgeThreshold = 56,
                                                          int maxPasses = 2)
        {
            if (texture == null || !texture.isReadable) return;

            for (int i = 0; i < maxPasses; i++)
            {
                if (!HasOpaqueCorners(texture, alphaThreshold: 12, cornerSize: 10, opaqueRatio: 0.015f))
                    return;

                Color32 key = EstimateCornerColor(texture);
                RemoveBackgroundByColorKey(texture, key, threshold, edgeThreshold, edgePasses: 2);
            }
        }

        private static bool LooksLikeRemovableBackground(Color32[] pixels, int w, int h, Color32 bg, int thresholdSq)
        {
            // SD subjects almost never appear in the image corners.
            // Checking only corner patches is far more robust than checking the full border,
            // because a character that touches the side edges would break a full-border ratio check.
            int cornerSize = Mathf.Max(3, Mathf.Min(w, h) / 10);  // ~10% of image edge

            int matches = 0, total = 0;
            for (int cy = 0; cy < cornerSize; cy++)
            {
                for (int cx = 0; cx < cornerSize; cx++)
                {
                    // top-left
                    if (ColorDistSq(pixels[cy * w + cx], bg) <= thresholdSq) matches++;
                    // top-right
                    if (ColorDistSq(pixels[cy * w + (w - 1 - cx)], bg) <= thresholdSq) matches++;
                    // bottom-left
                    if (ColorDistSq(pixels[(h - 1 - cy) * w + cx], bg) <= thresholdSq) matches++;
                    // bottom-right
                    if (ColorDistSq(pixels[(h - 1 - cy) * w + (w - 1 - cx)], bg) <= thresholdSq) matches++;
                    total += 4;
                }
            }

            // Require 70% of corner pixels to match the estimated background color.
            return total > 0 && (float)matches / total >= 0.70f;
        }

        private static bool HasSufficientOpaqueArea(Color32[] pixels, int w, int h)
        {
            int opaque = 0;
            int total = pixels.Length;
            for (int i = 0; i < total; i++)
                if (pixels[i].a > 12) opaque++;

            float ratio = total > 0 ? (float)opaque / total : 0f;

            // Keep at least 3% opaque pixels and at least 64 pixels total.
            // (Pickup sprites can be small and legitimately occupy only 3-5% of the image.)
            return opaque >= 64 && ratio >= 0.03f;
        }

        // ──────────────────────────────────────────────────────────────────
        /// <summary>
        /// Estimates the background colour by averaging a small region in each corner.
        /// Using a region (not single pixels) is far more robust against JPEG noise.
        /// </summary>
        private static Color32 EstimateBackgroundColor(Color32[] pixels, int w, int h)
        {
            // Sample a patch of ~5 % of the smaller image dimension in each corner.
            int s = Mathf.Max(3, Mathf.Min(w, h) / 20);
            long r = 0, g = 0, b = 0, count = 0;

            for (int cy = 0; cy < s; cy++)
            {
                for (int cx = 0; cx < s; cx++)
                {
                    // top-left
                    Color32 c = pixels[cy * w + cx];
                    r += c.r; g += c.g; b += c.b; count++;
                    // top-right
                    c = pixels[cy * w + (w - 1 - cx)];
                    r += c.r; g += c.g; b += c.b; count++;
                    // bottom-left
                    c = pixels[(h - 1 - cy) * w + cx];
                    r += c.r; g += c.g; b += c.b; count++;
                    // bottom-right
                    c = pixels[(h - 1 - cy) * w + (w - 1 - cx)];
                    r += c.r; g += c.g; b += c.b; count++;
                }
            }

            return count > 0
                ? new Color32((byte)(r / count), (byte)(g / count), (byte)(b / count), 255)
                : new Color32(255, 255, 255, 255);
        }

        private static void EnqueueBorder(Queue<int> q, bool[] vis, Color32[] p,
                                          int w, int h, Color32 bg, int thr)
        {
            for (int x = 0; x < w; x++)
            {
                TryVisit(x, 0,     w, h, p, vis, q, bg, thr);
                TryVisit(x, h - 1, w, h, p, vis, q, bg, thr);
            }
            for (int y = 1; y < h - 1; y++)
            {
                TryVisit(0,     y, w, h, p, vis, q, bg, thr);
                TryVisit(w - 1, y, w, h, p, vis, q, bg, thr);
            }
        }

        private static void TryVisit(int x, int y, int w, int h,
                                     Color32[] p, bool[] vis,
                                     Queue<int> q, Color32 bg, int thr)
        {
            if (x < 0 || y < 0 || x >= w || y >= h) return;
            int i = y * w + x;
            if (vis[i]) return;
            vis[i] = true;
            if (ColorDistSq(p[i], bg) <= thr)
                q.Enqueue(i);
        }

        private static int ColorDistSq(Color32 a, Color32 b)
        {
            int dr = a.r - b.r, dg = a.g - b.g, db = a.b - b.b;
            return dr * dr + dg * dg + db * db;
        }

        /// <summary>Returns true if any 4-connected neighbour pixel is fully transparent (a == 0).</summary>
        private static bool HasTransparentNeighbour4(Color32[] pixels, int x, int y, int w, int h)
        {
            if (x > 0   && pixels[ y      * w + (x - 1)].a == 0) return true;
            if (x < w-1 && pixels[ y      * w + (x + 1)].a == 0) return true;
            if (y > 0   && pixels[(y - 1) * w +  x     ].a == 0) return true;
            if (y < h-1 && pixels[(y + 1) * w +  x     ].a == 0) return true;
            return false;
        }

        private static void EnqueueOpaqueNeighbour(int x, int y, int w, int h,
                                                   bool[] opaque, bool[] visited,
                                                   Queue<int> queue)
        {
            if (x < 0 || y < 0 || x >= w || y >= h) return;
            int idx = y * w + x;
            if (visited[idx] || !opaque[idx]) return;
            visited[idx] = true;
            queue.Enqueue(idx);
        }

        private static void TryVisitColorKey(int x, int y, int w, int h,
                                             Color32[] pixels,
                                             bool[] visited,
                                             Queue<int> queue,
                                             Color32 keyColor,
                                             int thresholdSq)
        {
            if (x < 0 || y < 0 || x >= w || y >= h) return;
            int idx = y * w + x;
            if (visited[idx]) return;
            visited[idx] = true;
            if (ColorDistSq(pixels[idx], keyColor) <= thresholdSq)
                queue.Enqueue(idx);
        }
    }
}

