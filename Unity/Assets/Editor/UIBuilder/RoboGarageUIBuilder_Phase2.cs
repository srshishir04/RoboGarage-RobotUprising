using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using static RoboGarageUIFactory;

/// <summary>
/// Phase 2 — reusable component prefabs (spec §3). Builds every prefab from code so the
/// geometry/colour is exactly the spec's numbers, not a hand-eyeballed approximation.
/// Run Tools/RoboGarage UI/Generate Sprites first (rounded-rect corner textures).
/// </summary>
public static class RoboGarageUIBuilder_Phase2
{
    private const string Folder = "Assets/Prefabs/UI";

    private static TMP_FontAsset _archivoRegular, _archivoSemiBold, _archivoBold, _archivoExtraBold, _archivoBlack;
    private static TMP_FontAsset _jbRegular, _jbBold;

    [MenuItem("Tools/RoboGarage UI/Phase 2 - Build Component Prefabs")]
    public static void BuildAll()
    {
        _archivoRegular = LoadFont(ArchivoRegular);
        _archivoSemiBold = LoadFont(ArchivoSemiBold);
        _archivoBold = LoadFont(ArchivoBold);
        _archivoExtraBold = LoadFont(ArchivoExtraBold);
        _archivoBlack = LoadFont(ArchivoBlack);
        _jbRegular = LoadFont(JetBrainsRegular);
        _jbBold = LoadFont(JetBrainsBold);

        if (_archivoRegular == null || _archivoSemiBold == null || _archivoBold == null || _archivoExtraBold == null ||
            _archivoBlack == null || _jbRegular == null || _jbBold == null)
        {
            Debug.LogError("[RoboGarageUIBuilder_Phase2] Aborted — one or more TMP Font Assets are missing. See errors above.");
            return;
        }

        if (LoadRoundedRect(10) == null)
        {
            Debug.LogError("[RoboGarageUIBuilder_Phase2] Aborted — sprites missing. Run Tools > RoboGarage UI > Generate Sprites first.");
            return;
        }

        System.IO.Directory.CreateDirectory(Folder);

        BuildSegmentButton();
        BuildPrimaryButton();
        BuildSecondaryButton();
        BuildStopButton();
        BuildIconButton();
        BuildSliderRow();
        BuildToggleRow();
        BuildSaveButton();
        BuildScorePod();
        BuildTimer();
        BuildDifficultyChip();
        BuildStatusDot();

        AssetDatabase.SaveAssets();
        Debug.Log($"[RoboGarageUIBuilder_Phase2] Built 12 prefabs into {Folder}.");
    }

    // ── 3.1 Segment button ──────────────────────────────────────────────────────
    private static void BuildSegmentButton()
    {
        var (root, fill, border) = CreateRoundedPanel("SegmentButton", null, new Vector2(180, 52),
            Colors.ControlSurface, 10, Colors.ControlBorder, 1);

        var shadow = CreateImage("InsetShadow", root, new Color(0, 0, 0, 0.25f), LoadRoundedRect(10), Image.Type.Sliced);
        StretchFill(shadow.rectTransform, 0, 0, 49, 0); // 3px strip at the bottom (52 - 3 = 49 top offset)
        shadow.gameObject.SetActive(false);

        var content = CreateUIObject("Content", root);
        StretchFill(content, 20, 20, 0, 0);
        var label = CreateText("Label", content, "EASY", _archivoSemiBold, 16, Colors.TextSecondary);

        var seg = root.gameObject.AddComponent<SegmentButton>();
        seg.targetGraphic = fill;
        SetRef(seg, "fill", fill);
        SetRef(seg, "border", border);
        SetRef(seg, "insetShadow", shadow.gameObject);
        SetRef(seg, "content", content);
        SetRef(seg, "label", label);
        SetRef(seg, "semiBoldFont", _archivoSemiBold);
        SetRef(seg, "extraBoldFont", _archivoExtraBold);

        SavePrefab<SegmentButton>(root.gameObject, Folder, "SegmentButton");
    }

    // ── 3.2 Primary button (PLAY) ───────────────────────────────────────────────
    private static void BuildPrimaryButton()
    {
        var root = CreateUIObject("PrimaryButton", null);
        root.sizeDelta = new Vector2(398, 64);

        var glow = CreateImage("OuterGlow", root, Colors.GarageOrange, LoadGlow(), Image.Type.Simple);
        glow.rectTransform.anchorMin = glow.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
        glow.rectTransform.anchoredPosition = Vector2.zero;
        glow.rectTransform.sizeDelta = new Vector2(398 + 48, 64 + 48);
        glow.raycastTarget = false;
        Color gc = glow.color; gc.a = 0.22f; glow.color = gc;

        var sprite10 = LoadRoundedRect(12);
        var fill = CreateImage("Fill", root, Colors.GarageOrange, sprite10, Image.Type.Sliced);
        StretchFill(fill.rectTransform);

        var shadow = CreateImage("InsetShadow", root, new Color(0, 0, 0, 0.28f), sprite10, Image.Type.Sliced);
        StretchFill(shadow.rectTransform, 0, 0, 60, 0); // 4px bottom strip

        var content = CreateUIObject("Content", root);
        StretchFill(content);
        var layout = content.gameObject.AddComponent<HorizontalLayoutGroup>();
        layout.childAlignment = TextAnchor.MiddleCenter;
        layout.spacing = 12;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;
        layout.childControlWidth = true;
        layout.childControlHeight = true;

        var label = CreateLayoutText("Label", content, "PLAY", _archivoBlack, 22, Colors.TextOnOrange, 0.04f, TextAlignmentOptions.Center);
        var hint = CreateLayoutText("Hint", content, "[ENTER]", _jbBold, 12, new Color(Colors.TextOnOrange.r, Colors.TextOnOrange.g, Colors.TextOnOrange.b, 0.6f), 0, TextAlignmentOptions.Center);

        var btn = root.gameObject.AddComponent<ArcadeButton>();
        btn.targetGraphic = fill;
        btn.colors = MakeColors(Colors.GarageOrange, Colors.GarageOrangeHover, Colors.GarageOrangePressed, new Color32(0x3A, 0x32, 0x2C, 0xFF));
        btn.transition = Selectable.Transition.ColorTint;
        btn.SetLabelColors(MakeColors(Colors.TextOnOrange, Colors.TextOnOrange, Colors.TextOnOrange, Colors.TextMuted));
        SetRef(btn, "content", content);
        SetRef(btn, "insetShadow", shadow.gameObject);
        SetRef(btn, "outerGlow", glow);
        SetRef(btn, "label", label);
        SetFloat(btn, "normalGlowAlpha", 0.22f);
        SetFloat(btn, "hoverGlowAlpha", 0.30f);

        SavePrefab<ArcadeButton>(root.gameObject, Folder, "PrimaryButton");
    }

    // ── 3.3 Secondary button (Exit) ─────────────────────────────────────────────
    private static void BuildSecondaryButton()
    {
        var (root, fill, border) = CreateRoundedPanel("SecondaryButton", null, new Vector2(150, 64),
            Colors.ControlSurface, 12, Colors.ControlBorder, 1);

        var content = CreateUIObject("Content", root);
        StretchFill(content);
        var label = CreateText("Label", content, "EXIT", _archivoBold, 17, Colors.TextSecondary);

        var btn = root.gameObject.AddComponent<ArcadeButton>();
        btn.targetGraphic = fill;
        btn.colors = MakeColors(Colors.ControlSurface, Colors.ControlSurfaceHover, Colors.ControlSurfacePressed, Colors.ControlSurface);
        btn.transition = Selectable.Transition.ColorTint;
        btn.SetLabelColors(MakeColors(Colors.TextSecondary, Colors.TextBright, Colors.TextSecondary, Colors.TextDisabled));
        SetRef(btn, "content", content);
        SetRef(btn, "label", label);

        SavePrefab<ArcadeButton>(root.gameObject, Folder, "SecondaryButton");
    }

    // ── 3.4 STOP button ──────────────────────────────────────────────────────────
    private static void BuildStopButton()
    {
        var root = CreateUIObject("StopButton", null);
        root.sizeDelta = new Vector2(140, 44);
        var sprite = LoadRoundedRect(10);

        var fill = CreateImage("Fill", root, Colors.StatusRed, sprite, Image.Type.Sliced);
        StretchFill(fill.rectTransform);

        var shadow = CreateImage("InsetShadow", root, new Color(0, 0, 0, 0.30f), sprite, Image.Type.Sliced);
        StretchFill(shadow.rectTransform, 0, 0, 41, 0); // 3px bottom strip

        var content = CreateUIObject("Content", root);
        StretchFill(content);
        var label = CreateText("Label", content, "STOP", _archivoBlack, 15, Color.white, 0.14f);

        var btn = root.gameObject.AddComponent<ArcadeButton>();
        btn.targetGraphic = fill;
        btn.colors = MakeColors(Colors.StatusRed, new Color32(0xF0, 0x4A, 0x43, 0xFF), new Color32(0xC2, 0x2B, 0x25, 0xFF), Colors.StatusRed);
        btn.transition = Selectable.Transition.ColorTint;
        SetRef(btn, "content", content);
        SetRef(btn, "insetShadow", shadow.gameObject);

        SavePrefab<ArcadeButton>(root.gameObject, Folder, "StopButton");
    }

    // ── 3.5 Icon button (gear) ───────────────────────────────────────────────────
    private static void BuildIconButton()
    {
        var (root, fill, border) = CreateRoundedPanel("IconButton", null, new Vector2(42, 42),
            Colors.ControlSurface, 10, Colors.ControlBorder, 1);

        var content = CreateUIObject("Content", root);
        StretchFill(content);
        var icon = CreateImage("Icon", content, Colors.TextSecondary, null, Image.Type.Simple);
        icon.rectTransform.anchorMin = icon.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
        icon.rectTransform.sizeDelta = new Vector2(20, 20);
        icon.rectTransform.anchoredPosition = Vector2.zero;
        // Gear sprite is a placeholder — see Phase 6 notes (no vector icon source available here).

        var btn = root.gameObject.AddComponent<IconToggleButton>();
        btn.targetGraphic = fill;
        SetRef(btn, "fill", fill);
        SetRef(btn, "border", border);
        SetRef(btn, "icon", icon);
        SetRef(btn, "iconRect", icon.rectTransform);
        SetRef(btn, "content", content);

        SavePrefab<IconToggleButton>(root.gameObject, Folder, "IconButton");
    }

    // ── 3.6 Slider row (Exposure / Gain) ────────────────────────────────────────
    private static void BuildSliderRow()
    {
        var root = CreateUIObject("SliderRow", null);
        root.sizeDelta = new Vector2(288, 46);
        var cg = root.gameObject.AddComponent<CanvasGroup>();

        var labelRow = CreateUIObject("LabelRow", root);
        labelRow.anchorMin = new Vector2(0, 1); labelRow.anchorMax = new Vector2(1, 1);
        labelRow.pivot = new Vector2(0.5f, 1f);
        labelRow.sizeDelta = new Vector2(0, 18);
        labelRow.anchoredPosition = Vector2.zero;

        var label = CreateLayoutText("Label", labelRow, "Exposure", _archivoBold, 13, Colors.TextPrimary, 0, TextAlignmentOptions.Left);
        label.rectTransform.anchorMin = new Vector2(0, 0.5f);
        label.rectTransform.anchorMax = new Vector2(0, 0.5f);
        label.rectTransform.pivot = new Vector2(0, 0.5f);
        label.rectTransform.anchoredPosition = Vector2.zero;

        var value = CreateLayoutText("Value", labelRow, "0 ms", _jbRegular, 12, Colors.TextBright, 0, TextAlignmentOptions.Right);
        value.rectTransform.anchorMin = new Vector2(1, 0.5f);
        value.rectTransform.anchorMax = new Vector2(1, 0.5f);
        value.rectTransform.pivot = new Vector2(1, 0.5f);
        value.rectTransform.anchoredPosition = Vector2.zero;

        var sliderRoot = CreateUIObject("Slider", root);
        sliderRoot.anchorMin = new Vector2(0, 0); sliderRoot.anchorMax = new Vector2(1, 0);
        sliderRoot.pivot = new Vector2(0.5f, 0f);
        sliderRoot.sizeDelta = new Vector2(0, 16);
        sliderRoot.anchoredPosition = Vector2.zero;
        var slider = sliderRoot.gameObject.AddComponent<Slider>();
        slider.transition = Selectable.Transition.None;
        slider.direction = Slider.Direction.LeftToRight;

        var track = CreateImage("Track", sliderRoot, Colors.SliderTrackEmpty, LoadRoundedRect(3), Image.Type.Sliced);
        track.rectTransform.anchorMin = new Vector2(0, 0.5f);
        track.rectTransform.anchorMax = new Vector2(1, 0.5f);
        track.rectTransform.sizeDelta = new Vector2(0, 6);
        track.rectTransform.anchoredPosition = Vector2.zero;

        var fillArea = CreateUIObject("Fill Area", sliderRoot);
        fillArea.anchorMin = new Vector2(0, 0.5f); fillArea.anchorMax = new Vector2(1, 0.5f);
        fillArea.sizeDelta = new Vector2(0, 6);
        fillArea.anchoredPosition = Vector2.zero;
        var fill = CreateImage("Fill", fillArea, Colors.SliderFillEnabled, LoadRoundedRect(3), Image.Type.Sliced);
        StretchFill(fill.rectTransform);
        slider.fillRect = fill.rectTransform;

        var handleArea = CreateUIObject("Handle Slide Area", sliderRoot);
        StretchFill(handleArea, 8, 8, 0, 0); // inset by half the 16px handle so it doesn't overhang the row
        var handle = CreateImage("Handle", handleArea, Colors.SliderHandle, LoadCircle(), Image.Type.Simple);
        handle.rectTransform.sizeDelta = new Vector2(16, 16);
        handle.rectTransform.anchorMin = handle.rectTransform.anchorMax = new Vector2(0, 0.5f);
        slider.handleRect = handle.rectTransform;
        slider.targetGraphic = handle;

        var handleVisual = handle.gameObject.AddComponent<SliderHandleVisual>();
        SetRef(handleVisual, "slider", slider);
        SetRef(handleVisual, "handleRect", handle.rectTransform);
        SetRef(handleVisual, "handleImage", handle);

        var rowUI = root.gameObject.AddComponent<SliderRowUI>();
        SetRef(rowUI, "canvasGroup", cg);
        SetRef(rowUI, "slider", slider);
        SetRef(rowUI, "fill", fill);
        SetRef(rowUI, "labelText", label);
        SetRef(rowUI, "valueText", value);
        SetRef(handleVisual, "owner", rowUI);

        SavePrefab<SliderRowUI>(root.gameObject, Folder, "SliderRow");
    }

    // ── 3.7 Toggle row (Auto exposure) ──────────────────────────────────────────
    private static void BuildToggleRow()
    {
        var root = CreateUIObject("ToggleRow", null);
        root.sizeDelta = new Vector2(288, 26);

        var label = CreateLayoutText("Label", root, "Auto exposure", _archivoBold, 13, Colors.TextPrimary, 0, TextAlignmentOptions.Left);
        label.rectTransform.anchorMin = new Vector2(0, 0.5f);
        label.rectTransform.anchorMax = new Vector2(0, 0.5f);
        label.rectTransform.pivot = new Vector2(0, 0.5f);
        label.rectTransform.anchoredPosition = Vector2.zero;

        var track = CreateImage("Track", root, Colors.Divider, LoadRoundedRect(13), Image.Type.Sliced);
        track.rectTransform.anchorMin = track.rectTransform.anchorMax = new Vector2(1, 0.5f);
        track.rectTransform.pivot = new Vector2(1, 0.5f);
        track.rectTransform.sizeDelta = new Vector2(46, 26);
        track.rectTransform.anchoredPosition = Vector2.zero;
        var toggle = track.gameObject.AddComponent<Toggle>();
        toggle.transition = Selectable.Transition.None;

        var knob = CreateImage("Knob", track.transform, Colors.SliderHandle, LoadCircle(), Image.Type.Simple);
        knob.rectTransform.sizeDelta = new Vector2(20, 20);
        knob.rectTransform.anchorMin = knob.rectTransform.anchorMax = new Vector2(0, 0.5f);
        knob.rectTransform.anchoredPosition = new Vector2(13, 0); // inset 3px from left edge (23-10)

        toggle.targetGraphic = track;
        toggle.graphic = null;

        var animated = track.gameObject.AddComponent<AnimatedToggle>();
        SetRef(animated, "toggle", toggle);
        SetRef(animated, "track", track);
        SetRef(animated, "knob", knob.rectTransform);
        SetRef(animated, "knobImage", knob);

        SavePrefab<Toggle>(root.gameObject, Folder, "ToggleRow");
    }

    // ── 3.8 SAVE button ──────────────────────────────────────────────────────────
    private static void BuildSaveButton()
    {
        var root = CreateUIObject("SaveButton", null);
        root.sizeDelta = new Vector2(288, 42);
        var sprite = LoadRoundedRect(9);

        var fill = CreateImage("Fill", root, Colors.GarageOrange, sprite, Image.Type.Sliced);
        StretchFill(fill.rectTransform);

        var shadow = CreateImage("InsetShadow", root, new Color(0, 0, 0, 0.28f), sprite, Image.Type.Sliced);
        StretchFill(shadow.rectTransform, 0, 0, 39, 0); // 3px bottom strip

        var content = CreateUIObject("Content", root);
        StretchFill(content);
        var label = CreateText("Label", content, "SAVE", _archivoBlack, 14, Colors.TextOnOrange, 0.06f);

        var btn = root.gameObject.AddComponent<ArcadeButton>();
        btn.targetGraphic = fill;
        btn.colors = MakeColors(Colors.GarageOrange, Colors.GarageOrangeHover, Colors.GarageOrangePressed, Colors.GarageOrange);
        btn.transition = Selectable.Transition.ColorTint;
        SetRef(btn, "content", content);
        SetRef(btn, "insetShadow", shadow.gameObject);

        var feedback = root.gameObject.AddComponent<SaveButtonFeedback>();
        SetRef(feedback, "button", btn);
        SetRef(feedback, "fill", fill);
        SetRef(feedback, "label", label);

        SavePrefab<SaveButtonFeedback>(root.gameObject, Folder, "SaveButton");
    }

    // ── 3.9 Score pod ─────────────────────────────────────────────────────────────
    private static void BuildScorePod()
    {
        var (root, fill, _) = CreateRoundedPanel("ScorePod", null, new Vector2(100, 44), Colors.ControlSurface, 10);
        var layout = root.gameObject.AddComponent<HorizontalLayoutGroup>();
        layout.padding = new RectOffset(18, 18, 5, 5);
        layout.spacing = 16;
        layout.childAlignment = TextAnchor.MiddleCenter;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        var fitter = root.gameObject.AddComponent<ContentSizeFitter>();
        fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;

        CreateLayoutText("HumanLabel", root, "HUMAN", _archivoExtraBold, 14, Colors.TextBright, 0.10f);
        var humanDigits = CreateLayoutText("HumanDigits", root, "0", _jbBold, 34, Colors.TextPrimary);
        CreateLayoutText("Separator", root, ":", _jbRegular, 22, Colors.TextDisabled);
        var robotDigits = CreateLayoutText("RobotDigits", root, "0", _jbBold, 34, Colors.GarageOrange);
        CreateLayoutText("RobotsLabel", root, "ROBOTS", _archivoExtraBold, 14, Colors.GarageOrange, 0.10f);

        var pod = root.gameObject.AddComponent<ScorePodUI>();
        SetRef(pod, "humanDigits", humanDigits);
        SetRef(pod, "robotDigits", robotDigits);

        SavePrefab<ScorePodUI>(root.gameObject, Folder, "ScorePod");
    }

    // ── 3.10 Timer ────────────────────────────────────────────────────────────────
    private static void BuildTimer()
    {
        var root = CreateUIObject("Timer", null);
        root.sizeDelta = new Vector2(86, 34);
        var text = CreateText("Text", root, "00:00", _jbBold, 28, Colors.TextPrimary, 0.03f);

        var timer = root.gameObject.AddComponent<TimerDisplayUI>();
        SetRef(timer, "timerText", text);

        SavePrefab<TimerDisplayUI>(root.gameObject, Folder, "Timer");
    }

    // ── 3.12 Difficulty chip ──────────────────────────────────────────────────────
    private static void BuildDifficultyChip()
    {
        var (root, fill, _) = CreateRoundedPanel("DifficultyChip", null, new Vector2(80, 34), Colors.ControlSurface, 8);
        var layout = root.gameObject.AddComponent<HorizontalLayoutGroup>();
        layout.padding = new RectOffset(13, 13, 7, 7);
        layout.childAlignment = TextAnchor.MiddleCenter;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        var fitter = root.gameObject.AddComponent<ContentSizeFitter>();
        fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;

        var text = CreateLayoutText("Text", root, "EASY", _archivoExtraBold, 13, Colors.GarageOrange, 0.06f);

        var chip = root.gameObject.AddComponent<ChipLabelUI>();
        SetRef(chip, "label", text);

        SavePrefab<ChipLabelUI>(root.gameObject, Folder, "DifficultyChip");
    }

    // ── 3.13 Status dot (standalone, HUD bottom bar — 9px) ──────────────────────
    private static void BuildStatusDot()
    {
        var root = CreateUIObject("StatusDot", null);
        root.sizeDelta = new Vector2(9, 9);

        var glow = CreateImage("Glow", root, Colors.StatusGreen, LoadGlow(), Image.Type.Simple);
        glow.raycastTarget = false;
        glow.rectTransform.sizeDelta = new Vector2(26, 26);
        glow.rectTransform.anchorMin = glow.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
        glow.rectTransform.anchoredPosition = Vector2.zero;

        var dot = CreateImage("Dot", root, Colors.StatusRed, LoadCircle(), Image.Type.Simple);
        dot.raycastTarget = false;
        StretchFill(dot.rectTransform);

        var statusDot = root.gameObject.AddComponent<StatusDot>();
        SetRef(statusDot, "dot", dot);
        SetRef(statusDot, "glow", glow);

        SavePrefab<StatusDot>(root.gameObject, Folder, "StatusDot");
    }

    // ── helpers ───────────────────────────────────────────────────────────────────
    private static ColorBlock MakeColors(Color normal, Color hover, Color pressed, Color disabled)
    {
        return new ColorBlock
        {
            normalColor = normal,
            highlightedColor = hover,
            pressedColor = pressed,
            selectedColor = normal,
            disabledColor = disabled,
            colorMultiplier = 1f,
            fadeDuration = 0.09f,
        };
    }
}
