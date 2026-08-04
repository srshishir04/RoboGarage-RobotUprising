using UnityEditor;
using UnityEngine;

/// <summary>
/// One-off fix for Assets/Images/image.png (the uploaded mascot art) — it was imported as a
/// generic Default texture, so it can't be assigned to a UI Image's Sprite field yet. This
/// only touches the asset's import settings, never the scene — safe to run regardless of
/// any unsaved scene changes.
/// </summary>
public static class RoboGarageFixMascotImport
{
    private const string Path = "Assets/Images/image.png";

    [MenuItem("Tools/RoboGarage UI/Fix Mascot Import Settings")]
    public static void Fix()
    {
        var importer = (TextureImporter)AssetImporter.GetAtPath(Path);
        if (importer == null)
        {
            Debug.LogError($"[RoboGarageFixMascotImport] No texture found at {Path}.");
            return;
        }

        importer.textureType = TextureImporterType.Sprite;
        importer.spriteImportMode = SpriteImportMode.Single;
        importer.alphaIsTransparency = true;
        importer.mipmapEnabled = false;
        importer.filterMode = FilterMode.Bilinear;
        importer.SaveAndReimport();

        Debug.Log($"[RoboGarageFixMascotImport] {Path} is now a UI-ready Sprite.");
    }
}
