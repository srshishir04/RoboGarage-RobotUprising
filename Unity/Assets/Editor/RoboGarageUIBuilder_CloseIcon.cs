using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;

/// <summary>
/// One-off: swaps the Setup Guide modal's close button from a "✕" text glyph to the real
/// close icon you sourced. Finds GuideModal/.../CloseButton/Content/Label by name, replaces
/// it with an Image using the new sprite. Only touches that one object — safe against the
/// rest of the polished scene.
/// </summary>
public static class RoboGarageUIBuilder_CloseIcon
{
    private const string IconPath = "Assets/Images/close_32dp_E3E3E3_FILL0_wght400_GRAD0_opsz40.png";

    [MenuItem("Tools/RoboGarage UI/Use Real Close Icon (Guide Modal)")]
    public static void Apply()
    {
        var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(IconPath);
        if (sprite == null)
        {
            Debug.LogError($"[RoboGarageUIBuilder_CloseIcon] No sprite found at {IconPath}.");
            return;
        }

        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
        {
            Debug.LogWarning("[RoboGarageUIBuilder_CloseIcon] Aborted — unsaved scene changes.");
            return;
        }

        Scene scene = EditorSceneManager.OpenScene("Assets/Scenes/MainMenu.unity", OpenSceneMode.Single);

        GameObject closeButton = FindByName(scene, "CloseButton");
        if (closeButton == null)
        {
            Debug.LogError("[RoboGarageUIBuilder_CloseIcon] Aborted — couldn't find CloseButton. " +
                            "Run 'Add Setup Guide (Main Menu)' first if you haven't yet.");
            return;
        }

        Transform content = closeButton.transform.Find("Content");
        Transform oldLabel = content != null ? content.Find("Label") : null;
        if (content == null || oldLabel == null)
        {
            Debug.LogError("[RoboGarageUIBuilder_CloseIcon] Aborted — CloseButton/Content/Label not found.");
            return;
        }

        Object.DestroyImmediate(oldLabel.gameObject);

        var iconGO = new GameObject("Icon", typeof(RectTransform));
        iconGO.layer = LayerMask.NameToLayer("UI");
        var rt = iconGO.GetComponent<RectTransform>();
        rt.SetParent(content, false);
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(14, 14);
        rt.anchoredPosition = Vector2.zero;

        var img = iconGO.AddComponent<Image>();
        img.sprite = sprite;
        img.color = Colors.TextSecondary;
        img.raycastTarget = false;

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log("[RoboGarageUIBuilder_CloseIcon] Close button now uses the real icon.");
    }

    private static GameObject FindByName(Scene scene, string name)
    {
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            var t = FindInChildren(root.transform, name);
            if (t != null) return t.gameObject;
        }
        return null;
    }

    private static Transform FindInChildren(Transform parent, string name)
    {
        if (parent.name == name) return parent;
        for (int i = 0; i < parent.childCount; i++)
        {
            var found = FindInChildren(parent.GetChild(i), name);
            if (found != null) return found;
        }
        return null;
    }
}
