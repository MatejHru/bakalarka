using UnityEngine;

namespace Level
{
    /// <summary>
    /// Defines the shape of a floating platform as a set of relative tile offsets.
    /// Anchor (0,0) is the top-left corner.
    ///
    /// Create assets: right-click in Project → Create → Platformer → Platform Preset
    ///
    /// Included presets to create:
    ///   Platform_1x1  width=1  tiles={(0,0)}
    ///   Platform_2x1  width=2  tiles={(0,0),(1,0)}
    ///   Platform_3x1  width=3  tiles={(0,0),(1,0),(2,0)}
    /// </summary>
    [CreateAssetMenu(fileName = "PlatformPreset", menuName = "Platformer/Platform Preset")]
    public class PlatformPreset : ScriptableObject
    {
        [Tooltip("Display name shown in debug logs.")]
        public string presetName = "Platform";

        [Tooltip("Width in tiles — used by the generator for gap and spacing calculations.")]
        [Min(1)] public int width = 1;

        [Tooltip("Relative tile positions to fill.\n" +
                 "(0,0) is the top-left anchor of this preset.\n" +
                 "Use positive X for width, negative Y for tiles below anchor.")]
        public Vector2Int[] tiles = { Vector2Int.zero };
    }
}
