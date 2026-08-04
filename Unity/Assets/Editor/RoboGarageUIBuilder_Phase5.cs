using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using TMPro;
using static RoboGarageUIFactory;

/// <summary>
/// Phase 5 — resize behaviour (spec §4.3). Unlike Phases 3/4, this is ADDITIVE ONLY: it finds
/// existing objects by name in each scene and adds/wires one new component, but never destroys
/// or rebuilds anything. Safe to run against manually polished scenes.
/// </summary>
public static class RoboGarageUIBuilder_Phase5
{
    [MenuItem("Tools/RoboGarage UI/Phase 5 - Wire Resize Behavior (Main Menu)")]
    public static void WireMainMenu()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
        {
            Debug.LogWarning("[RoboGarageUIBuilder_Phase5] Aborted — unsaved scene changes.");
            return;
        }

        Scene scene = EditorSceneManager.OpenScene("Assets/Scenes/MainMenu.unity", OpenSceneMode.Single);

        GameObject canvasGO = FindByName(scene, "Canvas");
        GameObject titleGO = FindByName(scene, "Title");
        GameObject contentBlockGO = FindByName(scene, "ContentBlock");

        if (canvasGO == null || titleGO == null || contentBlockGO == null)
        {
            Debug.LogError("[RoboGarageUIBuilder_Phase5] Aborted — couldn't find Canvas/Title/ContentBlock by name. " +
                            "If you renamed these during polishing, tell me the new names and I'll adjust the script.");
            return;
        }

        var title = titleGO.GetComponent<TextMeshProUGUI>();
        var contentLayout = contentBlockGO.GetComponent<VerticalLayoutGroup>();
        if (title == null || contentLayout == null)
        {
            Debug.LogError("[RoboGarageUIBuilder_Phase5] Aborted — Title has no TextMeshProUGUI or ContentBlock has no VerticalLayoutGroup.");
            return;
        }

        var controller = canvasGO.GetComponent<MainMenuResizeController>();
        if (controller == null) controller = canvasGO.AddComponent<MainMenuResizeController>();
        SetRef(controller, "title", title);
        SetRef(controller, "contentLayout", contentLayout);

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log("[RoboGarageUIBuilder_Phase5] Main Menu resize behaviour wired.");
    }

    [MenuItem("Tools/RoboGarage UI/Phase 5 - Wire Resize Behavior (HUD)")]
    public static void WireHud()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
        {
            Debug.LogWarning("[RoboGarageUIBuilder_Phase5] Aborted — unsaved scene changes.");
            return;
        }

        Scene scene = EditorSceneManager.OpenScene("Assets/Scenes/GameScene.unity", OpenSceneMode.Single);

        GameObject canvasGO = FindByName(scene, "Canvas");
        GameObject topBarGO = FindByName(scene, "TopBar");
        GameObject bottomBarGO = FindByName(scene, "BottomBar");
        GameObject modeLabelGO = FindByName(scene, "ModeLabel");

        if (canvasGO == null || topBarGO == null || bottomBarGO == null || modeLabelGO == null)
        {
            Debug.LogError("[RoboGarageUIBuilder_Phase5] Aborted — couldn't find Canvas/TopBar/BottomBar/ModeLabel by name. " +
                            "If you renamed these during polishing, tell me the new names and I'll adjust the script.");
            return;
        }

        var canvasScaler = canvasGO.GetComponent<CanvasScaler>();
        if (canvasScaler == null)
        {
            Debug.LogError("[RoboGarageUIBuilder_Phase5] Aborted — Canvas has no CanvasScaler.");
            return;
        }

        var controller = canvasGO.GetComponent<HudResizeController>();
        if (controller == null) controller = canvasGO.AddComponent<HudResizeController>();
        SetRef(controller, "canvasScaler", canvasScaler);
        SetRef(controller, "topBar", topBarGO.GetComponent<RectTransform>());
        SetRef(controller, "bottomBar", bottomBarGO.GetComponent<RectTransform>());
        SetRef(controller, "modeLabel", modeLabelGO);

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log("[RoboGarageUIBuilder_Phase5] HUD resize behaviour wired.");
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
