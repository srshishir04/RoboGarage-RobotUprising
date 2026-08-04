using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Generates the rounded-rect / circle / glow sprites the UI needs (spec §6 — "rounded-rect
/// 9-slice for radius 8/9/10/12/14" etc). Unity's Image component can't draw a corner radius
/// on its own, and there's no way to source hand-authored vector assets from here, so these
/// are produced procedurally: one flat-white sprite per radius, alpha-masked to a rounded
/// rect, imported with a matching Border so Image.Type.Sliced renders the exact design radius
/// at any control size. Safe to re-run — overwrites in place.
/// </summary>
public static class RoboGarageSpriteFactory
{
    private const string OutputFolder = "Assets/Textures/UI";
    public static readonly int[] RoundedRectRadii = { 3, 8, 9, 10, 12, 13, 14, 17 };

    public static string RoundedRectPath(int radius) => $"{OutputFolder}/RoundedRect_r{radius}.png";
    public static string CirclePath => $"{OutputFolder}/Circle.png";
    public static string GlowPath => $"{OutputFolder}/Glow.png";
    public static string HazardStripePath => $"{OutputFolder}/HazardStripe.png";

    [MenuItem("Tools/RoboGarage UI/Generate Sprites")]
    public static void GenerateAll()
    {
        Directory.CreateDirectory(OutputFolder);

        foreach (int r in RoundedRectRadii)
            WriteRoundedRect(r);

        WriteCircle();
        WriteGlow();
        WriteHazardStripe();

        AssetDatabase.Refresh();

        foreach (int r in RoundedRectRadii)
            ConfigureSpriteImport(RoundedRectPath(r), new Vector4(r, r, r, r));
        ConfigureSpriteImport(CirclePath, Vector4.zero);
        ConfigureSpriteImport(GlowPath, Vector4.zero);
        ConfigureSpriteImport(HazardStripePath, Vector4.zero);

        Debug.Log($"[RoboGarageSpriteFactory] Generated {RoundedRectRadii.Length} rounded-rect sprites + Circle + Glow + HazardStripe in {OutputFolder}.");
    }

    private static void WriteRoundedRect(int radius)
    {
        int size = radius * 2 + 4; // small stretchable middle strip beyond the two corners
        var pixels = new Color32[size * size];

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float alpha = RoundedRectAlpha(x, y, size, size, radius);
                pixels[y * size + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(alpha * 255f));
            }
        }

        WritePng(RoundedRectPath(radius), size, size, pixels);
    }

    private static float RoundedRectAlpha(int x, int y, int w, int h, float radius)
    {
        // Distance from pixel centre to the nearest edge of the rect, in each axis.
        float dx = Mathf.Max(radius - 0.5f - x, x - (w - radius - 0.5f), 0f);
        float dy = Mathf.Max(radius - 0.5f - y, y - (h - radius - 0.5f), 0f);

        if (dx <= 0f || dy <= 0f) return 1f; // straight edge or interior — always inside

        float dist = Mathf.Sqrt(dx * dx + dy * dy);
        return Mathf.Clamp01(radius - dist + 0.5f);
    }

    private static void WriteCircle()
    {
        const int size = 128;
        float r = size * 0.5f;
        var pixels = new Color32[size * size];

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dist = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(r, r));
                float alpha = Mathf.Clamp01(r - dist);
                pixels[y * size + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(alpha * 255f));
            }
        }

        WritePng(CirclePath, size, size, pixels);
    }

    private static void WriteGlow()
    {
        const int size = 128;
        float r = size * 0.5f;
        var pixels = new Color32[size * size];

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dist = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(r, r)) / r;
                float alpha = Mathf.Clamp01(1f - dist);
                alpha *= alpha; // soften falloff
                pixels[y * size + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(alpha * 255f));
            }
        }

        WritePng(GlowPath, size, size, pixels);
    }

    private static void WriteHazardStripe()
    {
        // 16px bands, baked-in two-tone diagonal (not a tintable mask — Image.color must stay
        // white or it'd wash out the contrast between the two stripe colours). (x+y) % 32 gives
        // a diagonal that tiles seamlessly in both directions since the tile size is 2x the band.
        const int band = 16;
        const int size = band * 2;
        Color32 orange = new Color32(0xFF, 0x4E, 0x00, 0xFF);
        Color32 dark = new Color32(0x19, 0x17, 0x15, 0xFF);
        var pixels = new Color32[size * size];

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int diagonal = ((x + y) % size + size) % size;
                pixels[y * size + x] = diagonal < band ? orange : dark;
            }
        }

        WritePng(HazardStripePath, size, size, pixels);
    }

    private static void WritePng(string path, int width, int height, Color32[] pixels)
    {
        var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
        tex.SetPixels32(pixels);
        tex.Apply();
        File.WriteAllBytes(path, ImageConversion.EncodeToPNG(tex));
        Object.DestroyImmediate(tex);
    }

    private static void ConfigureSpriteImport(string path, Vector4 border)
    {
        var importer = (TextureImporter)AssetImporter.GetAtPath(path);
        if (importer == null) return;

        importer.textureType = TextureImporterType.Sprite;
        importer.spriteImportMode = SpriteImportMode.Single;
        importer.spriteBorder = border;
        importer.filterMode = FilterMode.Bilinear;
        importer.mipmapEnabled = false;
        importer.textureCompression = TextureImporterCompression.Uncompressed;
        importer.alphaIsTransparency = true;
        importer.SaveAndReimport();
    }
}
