using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using static RoboGarageUIFactory;

/// <summary>
/// One-off: wraps the HUD bottom bar's StatusText in the same chip treatment as the
/// Difficulty chip (rounded panel, border, padding) instead of floating bare on the bar.
/// Reparents the EXISTING StatusText object (GameSceneUI's field reference stays intact,
/// since it's the same GameObject instance, just moved one level deeper) — no script
/// wiring changes needed.
/// </summary>
public static class RoboGarageUIBuilder_StatusChip
{
    [MenuItem("Tools/RoboGarage UI/Add Border To Status Text (HUD)")]
    public static void Apply()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
        {
            Debug.LogWarning("[RoboGarageUIBuilder_StatusChip] Aborted — unsaved scene changes.");
            return;
        }

        Scene scene = EditorSceneManager.OpenScene("Assets/Scenes/GameScene.unity", OpenSceneMode.Single);

        GameObject statusTextGO = FindByName(scene, "StatusText");
        if (statusTextGO == null)
        {
            Debug.LogError("[RoboGarageUIBuilder_StatusChip] Aborted — couldn't find StatusText.");
            return;
        }

        Transform leftCluster = statusTextGO.transform.parent;
        if (leftCluster == null)
        {
            Debug.LogError("[RoboGarageUIBuilder_StatusChip] Aborted — StatusText has no parent.");
            return;
        }

        int siblingIndex = statusTextGO.transform.GetSiblingIndex();

        var (chipRoot, chipFill, chipBorder) = CreateRoundedPanel("StatusTextChip", leftCluster,
            new Vector2(100, 34), Colors.ControlSurface, 8, Colors.ControlBorder, 1);
        chipRoot.SetSiblingIndex(siblingIndex);

        var layout = chipRoot.gameObject.AddComponent<HorizontalLayoutGroup>();
        layout.padding = new RectOffset(13, 13, 7, 7);
        layout.childAlignment = TextAnchor.MiddleCenter;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        chipRoot.gameObject.AddComponent<ContentSizeFitter>().horizontalFit = ContentSizeFitter.FitMode.PreferredSize;

        statusTextGO.transform.SetParent(chipRoot, false);

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log("[RoboGarageUIBuilder_StatusChip] StatusText now has the chip border treatment.");
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
