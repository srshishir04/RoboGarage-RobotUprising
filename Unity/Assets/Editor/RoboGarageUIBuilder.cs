using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// Editor-only automation for the RoboGarage UI redesign (implementation spec, direction 2a).
/// Each phase of the spec gets one menu item under Tools/RoboGarage UI. Run them in order;
/// each is safe to re-run (idempotent) since it sets final values rather than incrementing.
///
/// Phase 1 — Foundation: CanvasScaler setup on both screens (spec §4.3).
/// Font import (Archivo / JetBrains Mono TMP Font Assets) is a one-time manual step via
/// Window > TextMeshPro > Font Asset Creator — see the accompanying chat instructions for
/// exact settings per weight; it cannot be automated headlessly from here.
/// </summary>
public static class RoboGarageUIBuilder
{
    private static readonly string[] ScenePaths =
    {
        "Assets/Scenes/MainMenu.unity",
        "Assets/Scenes/GameScene.unity",
    };

    [MenuItem("Tools/RoboGarage UI/Phase 1 - Setup Canvas Scalers")]
    public static void Phase1_SetupCanvasScalers()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
        {
            Debug.LogWarning("[RoboGarageUIBuilder] Aborted — unsaved scene changes.");
            return;
        }

        string originalScenePath = SceneManager.GetActiveScene().path;

        foreach (string path in ScenePaths)
        {
            Scene scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);

            Canvas canvas = FindCanvas(scene);
            if (canvas == null)
            {
                Debug.LogError($"[RoboGarageUIBuilder] No Canvas found in {path} — skipped.");
                continue;
            }

            CanvasScaler scaler = canvas.GetComponent<CanvasScaler>();
            if (scaler == null) scaler = canvas.gameObject.AddComponent<CanvasScaler>();

            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280f, 720f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 1f; // 1 = match height, per spec §4.3

            EditorUtility.SetDirty(scaler);
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);

            Debug.Log($"[RoboGarageUIBuilder] Phase 1 — {path}: CanvasScaler set to " +
                      $"ScaleWithScreenSize, 1280x720, match=height on '{canvas.gameObject.name}'.");
        }

        if (!string.IsNullOrEmpty(originalScenePath))
            EditorSceneManager.OpenScene(originalScenePath, OpenSceneMode.Single);

        Debug.Log("[RoboGarageUIBuilder] Phase 1 complete.");
    }

    private static Canvas FindCanvas(Scene scene)
    {
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            Canvas c = root.GetComponentInChildren<Canvas>(true);
            if (c != null) return c;
        }
        return null;
    }
}
