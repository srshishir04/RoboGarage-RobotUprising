using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using TMPro;
using static RoboGarageUIFactory;

/// <summary>
/// Phase 4 — HUD rebuild + camera settings popover (spec §4.2 + §5). Clears the existing
/// Canvas hierarchy and rebuilds it from the Phase 2 prefabs, rewires GameSceneUI.cs and
/// CameraControl.cs's fields, and wires the STOP button's click listener. Neither script's
/// actual logic changed — only which components their fields point at.
///
/// The old MatchEndPanel/HalfTimeBanner/WinnerText subtree (dead UI from before the match
/// model was simplified to "no periods, no half-time, no full-time whistle" — GameSceneUI.cs
/// hasn't referenced any of it for a while) is not recreated; the new spec's HUD has no
/// equivalent element either.
/// </summary>
public static class RoboGarageUIBuilder_Phase4
{
    private const string ScenePath = "Assets/Scenes/GameScene.unity";
    private const string PrefabFolder = "Assets/Prefabs/UI";

    private static TMP_FontAsset _archivoRegular, _archivoBold, _archivoExtraBold, _archivoBlack;
    private static TMP_FontAsset _jbRegular, _jbBold;

    [MenuItem("Tools/RoboGarage UI/Phase 4 - Build HUD")]
    public static void Build()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
        {
            Debug.LogWarning("[RoboGarageUIBuilder_Phase4] Aborted — unsaved scene changes.");
            return;
        }

        // Open the scene BEFORE loading any asset references (lesson from Phase 3 — holding
        // an Object reference across EditorSceneManager.OpenScene can leave it pointing at a
        // destroyed managed wrapper).
        Scene scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

        _archivoRegular = LoadFont(ArchivoRegular);
        _archivoBold = LoadFont(ArchivoBold);
        _archivoExtraBold = LoadFont(ArchivoExtraBold);
        _archivoBlack = LoadFont(ArchivoBlack);
        _jbRegular = LoadFont(JetBrainsRegular);
        _jbBold = LoadFont(JetBrainsBold);

        if (_archivoRegular == null || _archivoBold == null || _archivoExtraBold == null ||
            _archivoBlack == null || _jbRegular == null || _jbBold == null)
        {
            Debug.LogError("[RoboGarageUIBuilder_Phase4] Aborted — one or more TMP Font Assets are missing.");
            return;
        }

        var iconButtonPrefab = Load<IconToggleButton>("IconButton");
        var stopButtonPrefab = Load<ArcadeButton>("StopButton");
        var scorePodPrefab = Load<ScorePodUI>("ScorePod");
        var timerPrefab = Load<TimerDisplayUI>("Timer");
        var chipPrefab = Load<ChipLabelUI>("DifficultyChip");
        var statusDotPrefab = Load<StatusDot>("StatusDot");
        var sliderRowPrefab = Load<SliderRowUI>("SliderRow");
        // ToggleRow's Toggle component lives on its "Track" child, not the prefab root, so this
        // needs the root GameObject itself (for PrefabUtility.InstantiatePrefab) rather than a
        // component reference whose .gameObject would resolve to that child instead.
        var toggleRowPrefabGO = AssetDatabase.LoadAssetAtPath<GameObject>($"{PrefabFolder}/ToggleRow.prefab");
        var saveButtonPrefab = Load<SaveButtonFeedback>("SaveButton");

        if (iconButtonPrefab == null || stopButtonPrefab == null || scorePodPrefab == null ||
            timerPrefab == null || chipPrefab == null || statusDotPrefab == null ||
            sliderRowPrefab == null || toggleRowPrefabGO == null || saveButtonPrefab == null)
        {
            Debug.LogError("[RoboGarageUIBuilder_Phase4] Aborted — run Phase 2 first.");
            return;
        }

        Canvas canvas = FindCanvas(scene);
        if (canvas == null)
        {
            Debug.LogError($"[RoboGarageUIBuilder_Phase4] No Canvas found in {ScenePath}.");
            return;
        }

        GameSceneUI gameUI = FindComponent<GameSceneUI>(scene);
        CameraControl camControl = FindComponent<CameraControl>(scene);
        if (gameUI == null || camControl == null)
        {
            Debug.LogError("[RoboGarageUIBuilder_Phase4] GameSceneUI/CameraControl not found on the Manager object — aborted.");
            return;
        }

        var canvasTransform = canvas.transform;
        for (int i = canvasTransform.childCount - 1; i >= 0; i--)
            Object.DestroyImmediate(canvasTransform.GetChild(i).gameObject);

        BuildBackground(canvasTransform);
        var cameraView = BuildFieldArea(canvasTransform);
        var catcher = BuildCatcher(canvasTransform);

        var (difficultyChip, modeLabel, scorePod) = BuildTopBarLeftAndCenter(canvasTransform, chipPrefab, scorePodPrefab);
        var (timer, gearButton) = BuildTopBarRight(canvasTransform, timerPrefab, iconButtonPrefab);

        var (statusDot, statusText, stopButton) = BuildBottomBar(canvasTransform, statusDotPrefab, stopButtonPrefab);

        var (respawnText, flashPanel, flashText) = BuildFieldOverlays(canvasTransform);

        var (popoverPanel, popoverCanvasGroup, exposureRow, gainRow, autoToggle, saveFeedback) =
            BuildPopover(canvasTransform, gearButton, sliderRowPrefab, toggleRowPrefabGO, saveButtonPrefab);

        var popoverController = gearButton.gameObject.AddComponent<CameraPopoverUI>();
        SetRef(popoverController, "panel", popoverPanel);
        SetRef(popoverController, "canvasGroup", popoverCanvasGroup);
        SetRef(popoverController, "catcher", catcher);
        SetRef(popoverController, "gearButton", gearButton);

        WireGameSceneUI(gameUI, difficultyChip, modeLabel, scorePod, timer, cameraView,
            statusDot, statusText, respawnText, flashPanel, flashText);
        var saveButtonComponent = saveFeedback.GetComponent<Button>();
        WireCameraControl(camControl, cameraView, exposureRow, gainRow, autoToggle, saveButtonComponent, saveFeedback);

        UnityEventTools.AddPersistentListener(stopButton.onClick, gameUI.OnStopClicked);

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log("[RoboGarageUIBuilder_Phase4] HUD rebuilt, GameSceneUI + CameraControl rewired.");
    }

    // ── Background / field ────────────────────────────────────────────────────────

    private static void BuildBackground(Transform canvas)
    {
        var img = CreateImage("Background", canvas, Colors.FieldBackground);
        img.raycastTarget = false;
        StretchFill(img.rectTransform);
    }

    private static RawImage BuildFieldArea(Transform canvas)
    {
        var fieldArea = CreateUIObject("FieldArea", canvas);
        StretchFill(fieldArea, 0, 0, 64, 60); // between the 64px top bar and 60px bottom bar

        var rawImageGO = CreateUIObject("CameraView", fieldArea);
        StretchFill(rawImageGO);
        var rawImage = rawImageGO.gameObject.AddComponent<RawImage>();
        rawImage.color = Color.white;
        // Stretches to fully cover the field area (no aspect-preserving letterbox) — the
        // camera feed should fill the whole middle screen between the top and bottom bars.

        return rawImage;
    }

    private static Button BuildCatcher(Transform canvas)
    {
        var img = CreateImage("PopoverCatcher", canvas, new Color(0f, 0f, 0f, 0f));
        StretchFill(img.rectTransform);
        var btn = img.gameObject.AddComponent<Button>();
        btn.transition = Selectable.Transition.None;
        return btn;
    }

    private static (TextMeshProUGUI respawnText, CanvasGroup flashPanel, TextMeshProUGUI flashText) BuildFieldOverlays(Transform canvas)
    {
        var respawnText = CreateText("RespawnText", canvas, "", _archivoBold, 20, Colors.TextPrimary);
        respawnText.rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
        respawnText.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
        respawnText.rectTransform.pivot = new Vector2(0.5f, 0.5f);
        respawnText.rectTransform.sizeDelta = new Vector2(400, 40);
        respawnText.rectTransform.anchoredPosition = new Vector2(0, 40);
        // No spec coverage for kickoff-countdown text placement — centered above the field's
        // middle is a reasonable judgment call, not a literal spec value.

        var flashRoot = CreateUIObject("FlashPanel", canvas);
        StretchFill(flashRoot, 0, 0, 64, 60);
        var flashPanel = flashRoot.gameObject.AddComponent<CanvasGroup>();
        flashPanel.blocksRaycasts = false;
        flashPanel.interactable = false;
        var flashText = CreateText("FlashText", flashRoot, "", _archivoBlack, 36, Colors.TextPrimary);
        // Same note — flash feedback (goals/pickups/etc.) has no spec entry either; kept as a
        // simple centered overlay matching its pre-existing behaviour.

        return (respawnText, flashPanel, flashText);
    }

    // ── Top bar ────────────────────────────────────────────────────────────────────

    private static (ChipLabelUI chip, TextMeshProUGUI modeLabel, ScorePodUI scorePod) BuildTopBarLeftAndCenter(
        Transform canvas, ChipLabelUI chipPrefab, ScorePodUI scorePodPrefab)
    {
        var topBar = GetOrCreateTopBar(canvas);

        var leftCluster = CreateUIObject("LeftCluster", topBar);
        leftCluster.anchorMin = new Vector2(0, 0.5f);
        leftCluster.anchorMax = new Vector2(0, 0.5f);
        leftCluster.pivot = new Vector2(0, 0.5f);
        leftCluster.anchoredPosition = new Vector2(18, 0);
        var layout = leftCluster.gameObject.AddComponent<HorizontalLayoutGroup>();
        layout.spacing = 12;
        layout.childAlignment = TextAnchor.MiddleLeft;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        leftCluster.gameObject.AddComponent<ContentSizeFitter>().horizontalFit = ContentSizeFitter.FitMode.PreferredSize;

        var mascotSprite = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/Images/image.png");
        var mascot = CreateImage("Mascot", leftCluster, Color.white, mascotSprite);
        var mascotLE = mascot.gameObject.AddComponent<LayoutElement>();
        mascotLE.preferredWidth = 28;
        mascotLE.preferredHeight = 28;
        mascot.rectTransform.sizeDelta = new Vector2(28, 28);

        var chipInstance = (GameObject)PrefabUtility.InstantiatePrefab(chipPrefab.gameObject, leftCluster);
        var chipLE = chipInstance.gameObject.AddComponent<LayoutElement>();
        chipLE.preferredHeight = 34;

        var modeLabel = CreateLayoutText("ModeLabel", leftCluster, "1V1", _jbRegular, 12, Colors.TextMuted);

        var scorePodInstance = (GameObject)PrefabUtility.InstantiatePrefab(scorePodPrefab.gameObject, topBar);
        var podRect = scorePodInstance.GetComponent<RectTransform>();
        podRect.anchorMin = new Vector2(0.5f, 0.5f);
        podRect.anchorMax = new Vector2(0.5f, 0.5f);
        podRect.anchoredPosition = Vector2.zero;

        return (chipInstance.GetComponent<ChipLabelUI>(), modeLabel, scorePodInstance.GetComponent<ScorePodUI>());
    }

    private static (TimerDisplayUI timer, IconToggleButton gear) BuildTopBarRight(
        Transform canvas, TimerDisplayUI timerPrefab, IconToggleButton iconPrefab)
    {
        var topBar = GetOrCreateTopBar(canvas);

        var rightCluster = CreateUIObject("RightCluster", topBar);
        rightCluster.anchorMin = new Vector2(1, 0.5f);
        rightCluster.anchorMax = new Vector2(1, 0.5f);
        rightCluster.pivot = new Vector2(1, 0.5f);
        rightCluster.anchoredPosition = new Vector2(-18, 0);
        var layout = rightCluster.gameObject.AddComponent<HorizontalLayoutGroup>();
        layout.spacing = 14;
        layout.childAlignment = TextAnchor.MiddleRight;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        rightCluster.gameObject.AddComponent<ContentSizeFitter>().horizontalFit = ContentSizeFitter.FitMode.PreferredSize;

        var timerInstance = (GameObject)PrefabUtility.InstantiatePrefab(timerPrefab.gameObject, rightCluster);
        var timerLE = timerInstance.gameObject.AddComponent<LayoutElement>();
        timerLE.preferredWidth = 86;
        timerLE.preferredHeight = 34;

        var gearInstance = (GameObject)PrefabUtility.InstantiatePrefab(iconPrefab.gameObject, rightCluster);
        var gearLE = gearInstance.gameObject.AddComponent<LayoutElement>();
        gearLE.preferredWidth = 42;
        gearLE.preferredHeight = 42;

        return (timerInstance.GetComponent<TimerDisplayUI>(), gearInstance.GetComponent<IconToggleButton>());
    }

    private static RectTransform _topBarCache;

    private static RectTransform GetOrCreateTopBar(Transform canvas)
    {
        if (_topBarCache != null) return _topBarCache;

        var topBar = CreateImage("TopBar", canvas, Colors.BarSurface).rectTransform;
        topBar.anchorMin = new Vector2(0, 1);
        topBar.anchorMax = new Vector2(1, 1);
        topBar.pivot = new Vector2(0.5f, 1f);
        topBar.sizeDelta = new Vector2(0, 64);
        topBar.anchoredPosition = Vector2.zero;

        var border = CreateImage("BottomBorder", topBar, Colors.GarageOrange);
        border.rectTransform.anchorMin = new Vector2(0, 0);
        border.rectTransform.anchorMax = new Vector2(1, 0);
        border.rectTransform.pivot = new Vector2(0.5f, 0f);
        border.rectTransform.sizeDelta = new Vector2(0, 2);
        border.rectTransform.anchoredPosition = Vector2.zero;

        _topBarCache = topBar;
        return topBar;
    }

    // ── Bottom bar ─────────────────────────────────────────────────────────────────

    private static (StatusDot dot, TextMeshProUGUI statusText, Button stop) BuildBottomBar(
        Transform canvas, StatusDot dotPrefab, ArcadeButton stopPrefab)
    {
        var bottomBar = CreateImage("BottomBar", canvas, Colors.BarSurface).rectTransform;
        bottomBar.anchorMin = new Vector2(0, 0);
        bottomBar.anchorMax = new Vector2(1, 0);
        bottomBar.pivot = new Vector2(0.5f, 0f);
        bottomBar.sizeDelta = new Vector2(0, 60);
        bottomBar.anchoredPosition = Vector2.zero;

        var border = CreateImage("TopBorder", bottomBar, Colors.Divider);
        border.rectTransform.anchorMin = new Vector2(0, 1);
        border.rectTransform.anchorMax = new Vector2(1, 1);
        border.rectTransform.pivot = new Vector2(0.5f, 1f);
        border.rectTransform.sizeDelta = new Vector2(0, 1);
        border.rectTransform.anchoredPosition = Vector2.zero;

        var leftCluster = CreateUIObject("LeftCluster", bottomBar);
        leftCluster.anchorMin = new Vector2(0, 0.5f);
        leftCluster.anchorMax = new Vector2(0, 0.5f);
        leftCluster.pivot = new Vector2(0, 0.5f);
        leftCluster.anchoredPosition = new Vector2(18, 0);
        var layout = leftCluster.gameObject.AddComponent<HorizontalLayoutGroup>();
        layout.spacing = 11;
        layout.childAlignment = TextAnchor.MiddleLeft;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        leftCluster.gameObject.AddComponent<ContentSizeFitter>().horizontalFit = ContentSizeFitter.FitMode.PreferredSize;

        var dotInstance = (GameObject)PrefabUtility.InstantiatePrefab(dotPrefab.gameObject, leftCluster);
        var dotLE = dotInstance.gameObject.AddComponent<LayoutElement>();
        dotLE.preferredWidth = 9;
        dotLE.preferredHeight = 9;

        var statusText = CreateLayoutText("StatusText", leftCluster, "Waiting for brain...", _archivoRegular, 15, Colors.TextBright);

        var stopInstance = (GameObject)PrefabUtility.InstantiatePrefab(stopPrefab.gameObject, bottomBar);
        var stopRect = stopInstance.GetComponent<RectTransform>();
        stopRect.anchorMin = new Vector2(1, 0.5f);
        stopRect.anchorMax = new Vector2(1, 0.5f);
        stopRect.pivot = new Vector2(1, 0.5f);
        stopRect.anchoredPosition = new Vector2(-18, 0);

        return (dotInstance.GetComponent<StatusDot>(), statusText, stopInstance.GetComponent<Button>());
    }

    // ── Camera popover (spec §5) ─────────────────────────────────────────────────────

    private static (RectTransform panel, CanvasGroup canvasGroup, SliderRowUI exposureRow, SliderRowUI gainRow, Toggle autoToggle, SaveButtonFeedback saveFeedback)
        BuildPopover(Transform canvas, IconToggleButton gearButton, SliderRowUI sliderRowPrefab, GameObject toggleRowPrefabGO, SaveButtonFeedback savePrefab)
    {
        var popoverRoot = CreateUIObject("CameraPopover", canvas);
        popoverRoot.anchorMin = new Vector2(1, 1);
        popoverRoot.anchorMax = new Vector2(1, 1);
        popoverRoot.pivot = new Vector2(1, 1);
        popoverRoot.anchoredPosition = new Vector2(-18, -74);
        popoverRoot.sizeDelta = new Vector2(320, 0);
        var canvasGroup = popoverRoot.gameObject.AddComponent<CanvasGroup>();

        // Caret — 16x16 square rotated 45°, drawn above the panel body, centred under the gear.
        var caretSprite = LoadRoundedRect(3);
        var caret = CreateImage("Caret", popoverRoot, Colors.PopoverSurface, caretSprite, Image.Type.Sliced);
        caret.rectTransform.anchorMin = new Vector2(1, 1);
        caret.rectTransform.anchorMax = new Vector2(1, 1);
        caret.rectTransform.pivot = new Vector2(0.5f, 0.5f);
        caret.rectTransform.sizeDelta = new Vector2(16, 16);
        // Spec measures the caret at (right:26, top:56) from the WINDOW edge, but this anchor
        // point is relative to popoverRoot's own origin, which is already offset (right:18,
        // top:74) from the window. Relative offset = spec - popoverRoot's own offset:
        // x = -26 - (-18) = -8, y = -56 - (-74) = +18 (Unity's anchoredPosition Y is up-positive).
        caret.rectTransform.anchoredPosition = new Vector2(-8, 18);
        caret.rectTransform.localEulerAngles = new Vector3(0, 0, 45f);
        caret.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;

        var (panelRoot, panelFill, panelBorder) = CreateRoundedPanel("Panel", popoverRoot, new Vector2(320, 0),
            Colors.PopoverSurface, 14, Colors.ControlBorder, 1);
        panelRoot.anchorMin = new Vector2(0.5f, 1f);
        panelRoot.anchorMax = new Vector2(0.5f, 1f);
        panelRoot.pivot = new Vector2(0.5f, 1f);
        panelRoot.anchoredPosition = Vector2.zero;

        var panelLayout = panelRoot.gameObject.AddComponent<VerticalLayoutGroup>();
        panelLayout.childAlignment = TextAnchor.UpperCenter;
        panelLayout.childForceExpandWidth = true;
        panelLayout.childForceExpandHeight = false;
        panelLayout.childControlWidth = true;
        panelLayout.childControlHeight = true;
        panelRoot.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        // Header
        var header = CreateUIObject("Header", panelRoot);
        var headerLE = header.gameObject.AddComponent<LayoutElement>();
        headerLE.preferredHeight = 46;
        headerLE.minHeight = 46;
        var headerBorder = CreateImage("BottomBorder", header, Colors.Divider);
        headerBorder.rectTransform.anchorMin = new Vector2(0, 0);
        headerBorder.rectTransform.anchorMax = new Vector2(1, 0);
        headerBorder.rectTransform.pivot = new Vector2(0.5f, 0f);
        headerBorder.rectTransform.sizeDelta = new Vector2(0, 1);
        headerBorder.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;

        var cameraLabel = CreateText("CameraLabel", header, "CAMERA", _jbRegular, 11, Colors.TextTertiary, 0.20f, TextAlignmentOptions.Left);
        StretchFill(cameraLabel.rectTransform, 16, 0, 0, 0);
        var escHint = CreateText("EscHint", header, "ESC to close", _jbRegular, 11, Colors.TextMuted, 0, TextAlignmentOptions.Right);
        StretchFill(escHint.rectTransform, 0, 16, 0, 0);

        // Body
        var body = CreateUIObject("Body", panelRoot);
        var bodyLayout = body.gameObject.AddComponent<VerticalLayoutGroup>();
        bodyLayout.padding = new RectOffset(16, 16, 18, 18);
        bodyLayout.spacing = 20;
        bodyLayout.childAlignment = TextAnchor.UpperCenter;
        bodyLayout.childForceExpandWidth = true;
        bodyLayout.childForceExpandHeight = false;
        bodyLayout.childControlWidth = true;
        bodyLayout.childControlHeight = true;

        var exposureInstance = (GameObject)PrefabUtility.InstantiatePrefab(sliderRowPrefab.gameObject, body);
        exposureInstance.gameObject.AddComponent<LayoutElement>().preferredHeight = 46;
        var exposureRow = exposureInstance.GetComponent<SliderRowUI>();
        exposureRow.SetLabel("Exposure");

        var gainInstance = (GameObject)PrefabUtility.InstantiatePrefab(sliderRowPrefab.gameObject, body);
        gainInstance.gameObject.AddComponent<LayoutElement>().preferredHeight = 46;
        var gainRow = gainInstance.GetComponent<SliderRowUI>();
        gainRow.SetLabel("Gain");

        var toggleInstance = (GameObject)PrefabUtility.InstantiatePrefab(toggleRowPrefabGO, body);
        toggleInstance.gameObject.AddComponent<LayoutElement>().preferredHeight = 26;

        var saveInstance = (GameObject)PrefabUtility.InstantiatePrefab(savePrefab.gameObject, body);
        saveInstance.gameObject.AddComponent<LayoutElement>().preferredHeight = 42;

        // Toggle lives on the ToggleRow prefab's "Track" child, not its root.
        var autoToggle = toggleInstance.GetComponentInChildren<Toggle>(true);
        return (popoverRoot, canvasGroup, exposureRow, gainRow, autoToggle, saveInstance.GetComponent<SaveButtonFeedback>());
    }

    // ── Wiring ─────────────────────────────────────────────────────────────────────

    private static void WireGameSceneUI(
        GameSceneUI gameUI, ChipLabelUI difficultyChip, TextMeshProUGUI modeLabel, ScorePodUI scorePod,
        TimerDisplayUI timer, RawImage cameraView, StatusDot statusDot, TextMeshProUGUI statusText,
        TextMeshProUGUI respawnText, CanvasGroup flashPanel, TextMeshProUGUI flashText)
    {
        SetRef(gameUI, "difficultyChip", difficultyChip);
        SetRef(gameUI, "modeLabel", modeLabel);
        SetRef(gameUI, "scorePod", scorePod);
        SetRef(gameUI, "timer", timer);
        SetRef(gameUI, "cameraView", cameraView);
        SetRef(gameUI, "connectionDot", statusDot);
        SetRef(gameUI, "statusText", statusText);
        SetRef(gameUI, "respawnText", respawnText);
        SetRef(gameUI, "flashPanel", flashPanel);
        SetRef(gameUI, "flashText", flashText);
    }

    private static void WireCameraControl(
        CameraControl camControl, RawImage cameraView, SliderRowUI exposureRow, SliderRowUI gainRow,
        Toggle autoToggle, Button saveButton, SaveButtonFeedback saveFeedback)
    {
        SetRef(camControl, "cameraView", cameraView);
        SetRef(camControl, "exposureRow", exposureRow);
        SetRef(camControl, "gainRow", gainRow);
        SetRef(camControl, "autoToggle", autoToggle);
        SetRef(camControl, "saveButton", saveButton);
        SetRef(camControl, "saveFeedback", saveFeedback);
    }

    // ── Scene lookup helpers ─────────────────────────────────────────────────────

    private static Canvas FindCanvas(Scene scene)
    {
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            Canvas c = root.GetComponentInChildren<Canvas>(true);
            if (c != null) return c;
        }
        return null;
    }

    private static T FindComponent<T>(Scene scene) where T : Component
    {
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            T c = root.GetComponentInChildren<T>(true);
            if (c != null) return c;
        }
        return null;
    }

    private static T Load<T>(string prefabName) where T : Component
    {
        var go = AssetDatabase.LoadAssetAtPath<GameObject>($"{PrefabFolder}/{prefabName}.prefab");
        if (go == null)
        {
            Debug.LogError($"[RoboGarageUIBuilder_Phase4] Missing prefab '{prefabName}' at {PrefabFolder}.");
            return null;
        }
        // GetComponentInChildren, not GetComponent — ToggleRow's Toggle lives on its Track child.
        return go.GetComponentInChildren<T>(true);
    }
}
