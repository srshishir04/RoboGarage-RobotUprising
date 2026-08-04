using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Shared GameObject-construction helpers for the RoboGarage UI builder scripts.
/// Pure code construction (no scene/prefab hand-authoring) so every phase can build
/// exact, reviewable, re-runnable hierarchies from the spec's pixel values.
/// </summary>
public static class RoboGarageUIFactory
{
    // Archivo/JetBrains Mono didn't render clearly at the HUD's smaller text sizes, so every
    // text role now uses Unity's built-in LiberationSans SDF instead — EXCEPT the Main Menu
    // title, which still looks right at 72px and keeps real Archivo Black (see TitleFont).
    private const string LiberationSDF = "Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF.asset";

    public const string ArchivoRegular = LiberationSDF;
    public const string ArchivoSemiBold = LiberationSDF;
    public const string ArchivoBold = LiberationSDF;
    public const string ArchivoExtraBold = LiberationSDF;
    public const string ArchivoBlack = LiberationSDF;
    public const string JetBrainsRegular = LiberationSDF;
    public const string JetBrainsBold = LiberationSDF;

    /// <summary>The one exception — Main Menu title only (spec §2, Archivo 900 @ 72px).</summary>
    public const string TitleFont = "Assets/Fonts/Archivo/Archivo-Black SDF.asset";

    public static TMP_FontAsset LoadFont(string path)
    {
        var font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(path);
        if (font == null)
            Debug.LogError($"[RoboGarageUIFactory] Missing TMP Font Asset at '{path}'. " +
                            "Generate it via Window > TextMeshPro > Font Asset Creator (see Phase 1 instructions) " +
                            "and save it to this exact path, or update the path constant in RoboGarageUIFactory.cs.");
        return font;
    }

    public static Sprite LoadRoundedRect(int radius)
    {
        string path = RoboGarageSpriteFactory.RoundedRectPath(radius);
        var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
        if (sprite == null)
            Debug.LogError($"[RoboGarageUIFactory] Missing sprite '{path}'. Run Tools > RoboGarage UI > Generate Sprites first.");
        return sprite;
    }

    public static Sprite LoadCircle()
    {
        var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(RoboGarageSpriteFactory.CirclePath);
        if (sprite == null)
            Debug.LogError("[RoboGarageUIFactory] Missing Circle sprite. Run Tools > RoboGarage UI > Generate Sprites first.");
        return sprite;
    }

    public static Sprite LoadGlow()
    {
        var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(RoboGarageSpriteFactory.GlowPath);
        if (sprite == null)
            Debug.LogError("[RoboGarageUIFactory] Missing Glow sprite. Run Tools > RoboGarage UI > Generate Sprites first.");
        return sprite;
    }

    public static RectTransform CreateUIObject(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.layer = LayerMask.NameToLayer("UI");
        var rt = go.GetComponent<RectTransform>();
        rt.SetParent(parent, false);
        rt.localScale = Vector3.one;
        return rt;
    }

    public static void StretchFill(RectTransform rt, float left = 0, float right = 0, float top = 0, float bottom = 0)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(left, bottom);
        rt.offsetMax = new Vector2(-right, -top);
    }

    public static Image CreateImage(string name, Transform parent, Color color, Sprite sprite = null, Image.Type type = Image.Type.Simple)
    {
        var rt = CreateUIObject(name, parent);
        var img = rt.gameObject.AddComponent<Image>();
        img.sprite = sprite;
        img.type = type;
        img.color = color;
        if (sprite != null) img.pixelsPerUnitMultiplier = 1f;
        return img;
    }

    /// <summary>
    /// A filled rounded rect at the given radius, with an optional 1px border rendered as a
    /// second, inset copy of the same corner sprite underneath the fill (cheap, crisp, no
    /// custom shader needed at these small border widths).
    /// </summary>
    public static (RectTransform root, Image fill, GameObject border) CreateRoundedPanel(
        string name, Transform parent, Vector2 size, Color fillColor, int radius,
        Color? borderColor = null, float borderWidth = 1f)
    {
        var root = CreateUIObject(name, parent);
        root.sizeDelta = size;
        Sprite sprite = LoadRoundedRect(radius);

        GameObject borderGO = null;
        if (borderColor.HasValue)
        {
            var borderImg = CreateImage(name + "_Border", root, borderColor.Value, sprite, Image.Type.Sliced);
            StretchFill(borderImg.rectTransform);
            borderImg.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
            borderGO = borderImg.gameObject;
        }

        float inset = borderColor.HasValue ? borderWidth : 0f;
        var fillImg = CreateImage(name + "_Fill", root, fillColor, sprite, Image.Type.Sliced);
        StretchFill(fillImg.rectTransform, inset, inset, inset, inset);
        // Ignored by any HorizontalLayoutGroup a caller adds to `root` afterward (e.g. ScorePod,
        // DifficultyChip) — otherwise the layout group would try to arrange this backdrop as a
        // row item instead of leaving it as a stretched background.
        fillImg.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;

        return (root, fillImg, borderGO);
    }

    public static TextMeshProUGUI CreateText(
        string name, Transform parent, string text, TMP_FontAsset font, float fontSize, Color color,
        float letterSpacingEm = 0f, TextAlignmentOptions align = TextAlignmentOptions.Center)
    {
        var rt = CreateUIObject(name, parent);
        StretchFill(rt);
        var tmp = rt.gameObject.AddComponent<TextMeshProUGUI>();
        tmp.font = font;
        tmp.fontSize = fontSize;
        tmp.color = color;
        tmp.text = text;
        tmp.alignment = align;
        tmp.characterSpacing = letterSpacingEm * 100f;
        tmp.raycastTarget = false;
        tmp.textWrappingMode = TextWrappingModes.NoWrap;
        tmp.overflowMode = TextOverflowModes.Overflow;
        return tmp;
    }

    /// <summary>Assigns a private [SerializeField] reference via SerializedObject — avoids
    /// making every component field public just so builder scripts can wire them up.</summary>
    public static void SetRef(Object target, string fieldName, Object value)
    {
        var so = new SerializedObject(target);
        var prop = so.FindProperty(fieldName);
        if (prop == null)
        {
            Debug.LogError($"[RoboGarageUIFactory] Field '{fieldName}' not found on {target.GetType().Name}.");
            return;
        }
        prop.objectReferenceValue = value;
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    public static void SetFloat(Object target, string fieldName, float value)
    {
        var so = new SerializedObject(target);
        var prop = so.FindProperty(fieldName);
        if (prop == null)
        {
            Debug.LogError($"[RoboGarageUIFactory] Field '{fieldName}' not found on {target.GetType().Name}.");
            return;
        }
        prop.floatValue = value;
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    public static TextMeshProUGUI CreateLayoutText(
        string name, Transform parent, string text, TMP_FontAsset font, float fontSize, Color color,
        float letterSpacingEm = 0f, TextAlignmentOptions align = TextAlignmentOptions.Left)
    {
        var rt = CreateUIObject(name, parent);
        var tmp = rt.gameObject.AddComponent<TextMeshProUGUI>();
        tmp.font = font;
        tmp.fontSize = fontSize;
        tmp.color = color;
        tmp.text = text;
        tmp.alignment = align;
        tmp.characterSpacing = letterSpacingEm * 100f;
        tmp.raycastTarget = false;
        tmp.textWrappingMode = TextWrappingModes.NoWrap;
        tmp.overflowMode = TextOverflowModes.Overflow;
        var fitter = rt.gameObject.AddComponent<ContentSizeFitter>();
        fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        return tmp;
    }

    public static T SavePrefab<T>(GameObject go, string folder, string fileName) where T : Component
    {
        System.IO.Directory.CreateDirectory(folder);
        string path = $"{folder}/{fileName}.prefab";
        var prefab = PrefabUtility.SaveAsPrefabAsset(go, path, out bool success);
        Object.DestroyImmediate(go);
        if (!success)
        {
            Debug.LogError($"[RoboGarageUIFactory] Failed to save prefab at '{path}'.");
            return null;
        }
        // GetComponentInChildren, not GetComponent — some prefabs (e.g. ToggleRow) have their
        // key component on a child (the Track), not the root.
        return prefab.GetComponentInChildren<T>(true);
    }
}
