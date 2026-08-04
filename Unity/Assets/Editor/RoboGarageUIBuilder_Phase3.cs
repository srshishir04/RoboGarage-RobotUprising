using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using TMPro;
using static RoboGarageUIFactory;

/// <summary>
/// Phase 3 — Main Menu rebuild (spec §4.1). Clears the existing Canvas hierarchy and rebuilds
/// it from the Phase 2 prefabs, then rewires MainMenuUI's fields and button click listeners.
/// MainMenuUI's own selection/play/exit logic is untouched — only its visual wiring changes.
///
/// A few literal copy strings aren't specified anywhere in the spec (footer text, subtitle) —
/// placeholders are used and called out in the Phase 3 report; swap them for real copy anytime.
/// </summary>
public static class RoboGarageUIBuilder_Phase3
{
    private const string ScenePath = "Assets/Scenes/MainMenu.unity";
    private const string PrefabFolder = "Assets/Prefabs/UI";

    private static TMP_FontAsset _archivoRegular, _archivoSemiBold, _archivoBold, _archivoExtraBold, _archivoBlack;
    private static TMP_FontAsset _jbRegular, _jbBold;
    private static TMP_FontAsset _titleFont;

    [MenuItem("Tools/RoboGarage UI/Phase 3 - Build Main Menu")]
    public static void Build()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
        {
            Debug.LogWarning("[RoboGarageUIBuilder_Phase3] Aborted — unsaved scene changes.");
            return;
        }

        // Open the scene BEFORE loading any asset references below, not after — holding an
        // Object reference across EditorSceneManager.OpenScene can leave it pointing at a
        // destroyed managed wrapper (MissingReferenceException on first use afterward).
        Scene scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

        _archivoRegular = LoadFont(ArchivoRegular);
        _archivoSemiBold = LoadFont(ArchivoSemiBold);
        _archivoBold = LoadFont(ArchivoBold);
        _archivoExtraBold = LoadFont(ArchivoExtraBold);
        _archivoBlack = LoadFont(ArchivoBlack);
        _jbRegular = LoadFont(JetBrainsRegular);
        _jbBold = LoadFont(JetBrainsBold);
        _titleFont = LoadFont(TitleFont);

        if (_archivoRegular == null || _archivoSemiBold == null || _archivoBold == null ||
            _archivoExtraBold == null || _archivoBlack == null || _jbRegular == null || _jbBold == null ||
            _titleFont == null)
        {
            Debug.LogError("[RoboGarageUIBuilder_Phase3] Aborted — one or more TMP Font Assets are missing.");
            return;
        }

        var segmentPrefab = Load<SegmentButton>("SegmentButton");
        var primaryPrefab = Load<ArcadeButton>("PrimaryButton");
        var secondaryPrefab = Load<ArcadeButton>("SecondaryButton");
        var pillPrefab = Load<ConnectionPillUI>("ConnectionPill");
        if (segmentPrefab == null || primaryPrefab == null || secondaryPrefab == null || pillPrefab == null)
        {
            Debug.LogError("[RoboGarageUIBuilder_Phase3] Aborted — run Phase 2 first.");
            return;
        }

        Canvas canvas = FindCanvas(scene);
        if (canvas == null)
        {
            Debug.LogError($"[RoboGarageUIBuilder_Phase3] No Canvas found in {ScenePath}.");
            return;
        }

        MainMenuUI menuUI = FindMainMenuUI(scene);
        if (menuUI == null)
        {
            Debug.LogError("[RoboGarageUIBuilder_Phase3] No MainMenuUI component found anywhere in the scene — aborted.");
            return;
        }

        // Wipe the old flat hierarchy — MainMenuUI's own GameObject lives outside the Canvas
        // (it's on a separate "UIManager"-style object), so this doesn't touch the script itself.
        var canvasTransform = canvas.transform;
        for (int i = canvasTransform.childCount - 1; i >= 0; i--)
            Object.DestroyImmediate(canvasTransform.GetChild(i).gameObject);

        BuildBackground(canvasTransform);
        BuildHazardStripe(canvasTransform);
        BuildHeaderBar(canvasTransform);
        BuildFooterBar(canvasTransform);
        var content = BuildContentBlock(canvasTransform);

        var (btnEasy, btnMedium, btnHard) = BuildDifficultyRow(content, segmentPrefab);
        var (btn1v1, btn2v2) = BuildMatchModeRow(content, segmentPrefab);
        var (btnPlay, btnExit) = BuildActionRow(content, primaryPrefab, secondaryPrefab);
        BuildConnectionPill(content, pillPrefab);
        var warningText = BuildBrainWarning(content);

        WireMainMenuUI(menuUI, btnEasy, btnMedium, btnHard, btn1v1, btn2v2, btnPlay, btnExit, warningText);

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log("[RoboGarageUIBuilder_Phase3] Main Menu rebuilt and MainMenuUI rewired.");
    }

    // ── Top-level regions ─────────────────────────────────────────────────────────

    private static void BuildBackground(Transform canvas)
    {
        var img = CreateImage("Background", canvas, Colors.Chassis);
        img.raycastTarget = false;
        StretchFill(img.rectTransform);
    }

    private static void BuildHazardStripe(Transform canvas)
    {
        var img = CreateImage("HazardStripe", canvas, Color.white,
            AssetDatabase.LoadAssetAtPath<Sprite>(RoboGarageSpriteFactory.HazardStripePath), Image.Type.Tiled);
        var rt = img.rectTransform;
        rt.anchorMin = new Vector2(0, 1);
        rt.anchorMax = new Vector2(1, 1);
        rt.pivot = new Vector2(0.5f, 1f);
        rt.sizeDelta = new Vector2(0, 8);
        rt.anchoredPosition = Vector2.zero;
        img.raycastTarget = false;
    }

    private static void BuildHeaderBar(Transform canvas)
    {
        var header = CreateUIObject("HeaderBar", canvas);
        header.anchorMin = new Vector2(0, 1);
        header.anchorMax = new Vector2(1, 1);
        header.pivot = new Vector2(0.5f, 1f);
        header.sizeDelta = new Vector2(0, 60);
        header.anchoredPosition = new Vector2(0, -8);

        var leftCluster = CreateUIObject("LeftCluster", header);
        leftCluster.anchorMin = new Vector2(0, 0.5f);
        leftCluster.anchorMax = new Vector2(0, 0.5f);
        leftCluster.pivot = new Vector2(0, 0.5f);
        leftCluster.anchoredPosition = new Vector2(30, 0);
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
        // image.png is a 400x400 (1:1) flat-orange mascot — colour stays white so the baked
        // orange isn't tinted. Run Tools > RoboGarage UI > Fix Mascot Import Settings first if
        // this comes back null (it needs Texture Type = Sprite, not Default).
        var mascotLE = mascot.gameObject.AddComponent<LayoutElement>();
        mascotLE.preferredWidth = 32;
        mascotLE.preferredHeight = 32;
        mascot.rectTransform.sizeDelta = new Vector2(32, 32);

        CreateLayoutText("Wordmark", leftCluster, "ROBOGARAGE", _archivoBlack, 16, Colors.TextPrimary, 0.06f);
    }

    private static void BuildFooterBar(Transform canvas)
    {
        var fill = CreateImage("FooterBar", canvas, Colors.FooterBar);
        var root = fill.rectTransform;
        root.anchorMin = new Vector2(0, 0);
        root.anchorMax = new Vector2(1, 0);
        root.pivot = new Vector2(0.5f, 0f);
        root.sizeDelta = new Vector2(0, 48);
        root.anchoredPosition = Vector2.zero;

        var text = CreateText("Text", root, "ROBOGARAGE UPRISING — LOCAL BUILD", _jbRegular, 11, Colors.TextMuted, 0.14f, TextAlignmentOptions.Left);
        StretchFill(text.rectTransform, 30, 30, 0, 0);
        // Exact footer copy isn't specified in the spec — placeholder telemetry-style text.
    }

    private static RectTransform BuildContentBlock(Transform canvas)
    {
        var content = CreateUIObject("ContentBlock", canvas);
        StretchFill(content, 90, 90, 68, 48);

        var layout = content.gameObject.AddComponent<VerticalLayoutGroup>();
        layout.spacing = 34;
        layout.childAlignment = TextAnchor.MiddleCenter;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;
        layout.childControlWidth = true;
        layout.childControlHeight = true;

        // Title + subtitle + connection pill are grouped at 14px spacing (an override of the
        // block's default 34px column gap), then this whole group sits 34px above Controls.
        var titleAndPill = CreateUIObject("TitleAndPill", content);
        var tapLayout = titleAndPill.gameObject.AddComponent<VerticalLayoutGroup>();
        tapLayout.spacing = 14;
        tapLayout.childAlignment = TextAnchor.UpperCenter;
        tapLayout.childForceExpandWidth = false;
        tapLayout.childForceExpandHeight = false;
        tapLayout.childControlWidth = true;
        tapLayout.childControlHeight = true;
        // No ContentSizeFitter here — titleAndPill is itself a child of content's
        // VerticalLayoutGroup, which already sizes it from its own VerticalLayoutGroup's
        // reported preferred height. Adding a ContentSizeFitter on top made two systems
        // fight over the same size and collapsed the whole subtree to zero.

        BuildTitleGroup(titleAndPill);
        // Connection pill instantiated by the caller (needs the pill prefab) — placeholder slot:
        var pillSlot = CreateUIObject("ConnectionPillSlot", titleAndPill);
        pillSlot.gameObject.AddComponent<LayoutElement>().preferredHeight = 34;

        var controlsColumn = CreateUIObject("ControlsColumn", content);
        controlsColumn.gameObject.AddComponent<LayoutElement>().preferredWidth = 560;
        var colLayout = controlsColumn.gameObject.AddComponent<VerticalLayoutGroup>();
        colLayout.spacing = 30;
        colLayout.childAlignment = TextAnchor.UpperCenter;
        colLayout.childForceExpandWidth = false;
        colLayout.childForceExpandHeight = false;
        colLayout.childControlWidth = true;
        colLayout.childControlHeight = true;
        // Same reasoning as titleAndPill above — no ContentSizeFitter needed or wanted here.

        return controlsColumn;
    }

    private static void BuildTitleGroup(Transform parent)
    {
        var titleGroup = CreateUIObject("TitleGroup", parent);
        var layout = titleGroup.gameObject.AddComponent<VerticalLayoutGroup>();
        layout.spacing = 10;
        layout.childAlignment = TextAnchor.UpperCenter;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        // No ContentSizeFitter — same reasoning as titleAndPill/controlsColumn above.

        // Soft glow behind the title — an approximation of the spec's blurred drop shadow;
        // there's no blur shader in play here, just a soft radial sprite.
        var glow = CreateImage("TitleGlow", titleGroup, Colors.TitleGlow, LoadGlow());
        glow.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
        glow.rectTransform.anchorMin = glow.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
        glow.rectTransform.sizeDelta = new Vector2(520, 220);
        glow.rectTransform.anchoredPosition = Vector2.zero;
        glow.raycastTarget = false;

        // Two-tone title: "ROBO GARAGE" white, "UPRISING" orange (rich-text colour tags —
        // simpler than splitting into two TMP objects for a static, never-updated title).
        string titleMarkup = $"<color=#{ColorUtility.ToHtmlStringRGB(Colors.TextPrimary)}>ROBO GARAGE</color>\n" +
                              $"<color=#{ColorUtility.ToHtmlStringRGB(Colors.GarageOrange)}>UPRISING</color>";
        var title = CreateLayoutText("Title", titleGroup, titleMarkup, _titleFont, 72, Colors.GarageOrange, -0.035f);
        title.lineSpacing = -10f; // approximates the spec's 0.90 relative line-height

        var subtitle = CreateLayoutText("Subtitle", titleGroup, "HUMAN VS. ROBOT SOCCER", _jbRegular, 12, new Color32(0xFB, 0xFB, 0xFB, 0xFF), 0.22f);
        // Subtitle copy isn't specified in the spec — placeholder tagline.
    }

    private static void BuildConnectionPill(Transform controlsColumn, ConnectionPillUI pillPrefab)
    {
        // Actual pill lives in TitleAndPill/ConnectionPillSlot, not the controls column —
        // find it via the sibling path since BuildContentBlock already created the slot.
        Transform titleAndPill = controlsColumn.parent.Find("TitleAndPill");
        Transform slot = titleAndPill.Find("ConnectionPillSlot");

        var instance = (GameObject)PrefabUtility.InstantiatePrefab(pillPrefab.gameObject, slot);
        var pill = instance.GetComponent<ConnectionPillUI>();
        // Cosmetic-only per the Phase 3 decision — no real connection check exists yet.
        pill.SetConnecting();
    }

    private static (SegmentButton easy, SegmentButton medium, SegmentButton hard) BuildDifficultyRow(
        Transform controlsColumn, SegmentButton segmentPrefab)
    {
        var block = BuildSelectorBlock(controlsColumn, "DIFFICULTY", out Transform row, 560);
        var easy = InstantiateSegment(segmentPrefab, row, "EASY", 180);
        var medium = InstantiateSegment(segmentPrefab, row, "MEDIUM", 180);
        var hard = InstantiateSegment(segmentPrefab, row, "HARD", 180);
        return (easy, medium, hard);
    }

    private static (SegmentButton one, SegmentButton two) BuildMatchModeRow(
        Transform controlsColumn, SegmentButton segmentPrefab)
    {
        var block = BuildSelectorBlock(controlsColumn, "MATCH MODE", out Transform row, 366);
        var one = InstantiateSegment(segmentPrefab, row, "1V1", 178);
        var two = InstantiateSegment(segmentPrefab, row, "2V2", 178);
        return (one, two);
    }

    private static Transform BuildSelectorBlock(Transform parent, string labelText, out Transform row, float rowWidth)
    {
        var block = CreateUIObject($"{labelText}Block", parent);
        var layout = block.gameObject.AddComponent<VerticalLayoutGroup>();
        layout.spacing = 10;
        layout.childAlignment = TextAnchor.UpperCenter;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        // No ContentSizeFitter — same reasoning as titleAndPill/controlsColumn above.

        CreateLayoutText("Label", block, labelText, _jbRegular, 11, Colors.TextTertiary, 0.22f);

        var rowRt = CreateUIObject("Row", block);
        rowRt.gameObject.AddComponent<LayoutElement>().preferredWidth = rowWidth;
        var rowLayout = rowRt.gameObject.AddComponent<HorizontalLayoutGroup>();
        rowLayout.spacing = 10;
        rowLayout.childAlignment = TextAnchor.MiddleCenter;
        rowLayout.childForceExpandWidth = false;
        rowLayout.childForceExpandHeight = false;
        rowLayout.childControlWidth = true;
        rowLayout.childControlHeight = true;

        row = rowRt;
        return block;
    }

    private static SegmentButton InstantiateSegment(SegmentButton prefab, Transform parent, string label, float width)
    {
        var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab.gameObject, parent);
        instance.GetComponent<RectTransform>().sizeDelta = new Vector2(width, 52);
        var le = instance.gameObject.AddComponent<LayoutElement>();
        le.preferredWidth = width;
        le.preferredHeight = 52;
        var labelTmp = instance.transform.Find("Content/Label").GetComponent<TextMeshProUGUI>();
        labelTmp.text = label;
        return instance.GetComponent<SegmentButton>();
    }

    private static (Button play, Button exit) BuildActionRow(
        Transform controlsColumn, ArcadeButton primaryPrefab, ArcadeButton secondaryPrefab)
    {
        var row = CreateUIObject("ActionRow", controlsColumn);
        row.gameObject.AddComponent<LayoutElement>().preferredWidth = 560;
        var layout = row.gameObject.AddComponent<HorizontalLayoutGroup>();
        layout.spacing = 12;
        layout.childAlignment = TextAnchor.MiddleCenter;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;
        layout.childControlWidth = true;
        layout.childControlHeight = true;

        var playInstance = (GameObject)PrefabUtility.InstantiatePrefab(primaryPrefab.gameObject, row);
        playInstance.GetComponent<RectTransform>().sizeDelta = new Vector2(398, 64);
        var playLE = playInstance.gameObject.AddComponent<LayoutElement>();
        playLE.preferredWidth = 398;
        playLE.preferredHeight = 64;

        var exitInstance = (GameObject)PrefabUtility.InstantiatePrefab(secondaryPrefab.gameObject, row);
        exitInstance.GetComponent<RectTransform>().sizeDelta = new Vector2(150, 64);
        var exitLE = exitInstance.gameObject.AddComponent<LayoutElement>();
        exitLE.preferredWidth = 150;
        exitLE.preferredHeight = 64;

        return (playInstance.GetComponent<Button>(), exitInstance.GetComponent<Button>());
    }

    private static TextMeshProUGUI BuildBrainWarning(Transform controlsColumn)
    {
        var root = CreateUIObject("BrainWarning", controlsColumn);
        root.gameObject.AddComponent<LayoutElement>().preferredHeight = 18;
        var text = CreateText("Text", root, "", _jbRegular, 11, Colors.TextMuted, 0, TextAlignmentOptions.Center);
        return text;
    }

    // ── Wiring ─────────────────────────────────────────────────────────────────────

    private static void WireMainMenuUI(
        MainMenuUI menuUI,
        SegmentButton btnEasy, SegmentButton btnMedium, SegmentButton btnHard,
        SegmentButton btn1v1, SegmentButton btn2v2,
        Button btnPlay, Button btnExit,
        TextMeshProUGUI warningText)
    {
        SetRef(menuUI, "btnEasy", btnEasy);
        SetRef(menuUI, "btnMedium", btnMedium);
        SetRef(menuUI, "btnHard", btnHard);
        SetRef(menuUI, "btn1v1", btn1v1);
        SetRef(menuUI, "btn2v2", btn2v2);
        SetRef(menuUI, "btnPlay", btnPlay);
        SetRef(menuUI, "btnExit", btnExit);
        SetRef(menuUI, "brainWarningText", warningText);

        UnityEventTools.AddPersistentListener(btnEasy.onClick, menuUI.OnEasyClicked);
        UnityEventTools.AddPersistentListener(btnMedium.onClick, menuUI.OnMediumClicked);
        UnityEventTools.AddPersistentListener(btnHard.onClick, menuUI.OnHardClicked);
        UnityEventTools.AddPersistentListener(btn1v1.onClick, menuUI.On1v1Clicked);
        UnityEventTools.AddPersistentListener(btn2v2.onClick, menuUI.On2v2Clicked);
        UnityEventTools.AddPersistentListener(btnPlay.onClick, menuUI.OnPlayClicked);
        UnityEventTools.AddPersistentListener(btnExit.onClick, menuUI.OnExitClicked);
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

    private static MainMenuUI FindMainMenuUI(Scene scene)
    {
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            MainMenuUI ui = root.GetComponentInChildren<MainMenuUI>(true);
            if (ui != null) return ui;
        }
        return null;
    }

    private static T Load<T>(string prefabName) where T : Component
    {
        var go = AssetDatabase.LoadAssetAtPath<GameObject>($"{PrefabFolder}/{prefabName}.prefab");
        if (go == null)
        {
            Debug.LogError($"[RoboGarageUIBuilder_Phase3] Missing prefab '{prefabName}' at {PrefabFolder}.");
            return null;
        }
        return go.GetComponentInChildren<T>(true);
    }
}
