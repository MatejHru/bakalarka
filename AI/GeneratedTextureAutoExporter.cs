using System;
using System.IO;
using UnityEngine;

/// <summary>
/// Automatically exports generated textures when AiLevelPipeline finishes a normal run.
///
/// Useful during regular gameplay/manual testing.
/// Export path:
///   <UnityProjectRoot>/GeneratedTestOutputs/manual_exports/
/// </summary>
public class GeneratedTextureAutoExporter : MonoBehaviour
{
    [Header("Export")]
    public bool exportEnabled = true;
    public string exportRootFolderName = "GeneratedTestOutputs";
    public string manualFolderName = "manual_exports";

    private void OnEnable()
    {
        AiLevelPipeline.OnBundleReady += OnBundleReady;
    }

    private void OnDisable()
    {
        AiLevelPipeline.OnBundleReady -= OnBundleReady;
    }

    private void OnBundleReady(LevelBundle bundle)
    {
        if (!exportEnabled || bundle == null) return;

        string root = GetExportRootPath();
        string theme = string.IsNullOrWhiteSpace(Game.GameSessionState.BaseTheme)
            ? "theme"
            : Game.GameSessionState.BaseTheme;

        string folderName = DateTime.Now.ToString("yyyyMMdd_HHmmss")
            + "__L" + Game.GameSessionState.CurrentLevelIndex
            + "__" + Sanitize(theme);

        string outputDir = Path.Combine(root, manualFolderName, folderName);
        Directory.CreateDirectory(outputDir);

        Save(bundle.background, Path.Combine(outputDir, "background.png"));
        Save(bundle.terrainTile, Path.Combine(outputDir, "terrain_tile.png"));
        Save(bundle.playerSkin, Path.Combine(outputDir, "player.png"));
        Save(bundle.groundEnemySkin, Path.Combine(outputDir, "enemy_ground.png"));
        Save(bundle.flyingEnemySkin, Path.Combine(outputDir, "enemy_flying.png"));
        Save(bundle.shootingEnemySkin, Path.Combine(outputDir, "enemy_shooting.png"));
        Save(bundle.bossEnemySkin, Path.Combine(outputDir, "enemy_boss.png"));
        Save(bundle.shootingProjectileSkin, Path.Combine(outputDir, "projectile.png"));
        Save(bundle.pickupSkin, Path.Combine(outputDir, "pickup.png"));

        Debug.Log("[GeneratedTextureAutoExporter] Exported textures: " + outputDir);
    }

    private string GetExportRootPath()
    {
        var projectRootInfo = Directory.GetParent(Application.dataPath);
        string projectRoot = projectRootInfo != null ? projectRootInfo.FullName : Application.dataPath;

        string root = Path.Combine(
            projectRoot,
            string.IsNullOrWhiteSpace(exportRootFolderName)
                ? "GeneratedTestOutputs"
                : exportRootFolderName.Trim());

        Directory.CreateDirectory(root);
        return root;
    }

    private static void Save(Texture2D tex, string path)
    {
        if (tex == null) return;

        try
        {
            byte[] png = tex.EncodeToPNG();
            if (png != null && png.Length > 0)
                File.WriteAllBytes(path, png);
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[GeneratedTextureAutoExporter] Failed to save " + path + ": " + ex.Message);
        }
    }

    private static string Sanitize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "theme";

        char[] invalid = Path.GetInvalidFileNameChars();
        char[] chars = value.ToCharArray();

        for (int i = 0; i < chars.Length; i++)
        {
            bool bad = false;
            for (int j = 0; j < invalid.Length; j++)
            {
                if (chars[i] == invalid[j])
                {
                    bad = true;
                    break;
                }
            }

            if (bad || char.IsWhiteSpace(chars[i]))
                chars[i] = '_';
        }

        return new string(chars);
    }
}
