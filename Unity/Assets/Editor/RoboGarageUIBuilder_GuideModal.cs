using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using TMPro;
using static RoboGarageUIFactory;

/// <summary>
/// Setup Guide button + modal for the Main Menu. Additive only — like Phase 5, this finds
/// existing objects by name and only adds new ones under HeaderBar and Canvas; it never
/// touches or rebuilds anything else, so it's safe to run against the polished scene.
///
/// Placeholder step copy — the user described the gist ("set up the arena, turn on the
/// robot, run the python file") but not exact wording; edit GuideSteps below or directly
/// in the scene afterward.
/// </summary>
public static class RoboGarageUIBuilder_GuideModal
{
    private static readonly string[] GuideSteps =
    {
        "1.  Set up the arena and place the ball.",
        "2.  Power on the robot(s) and wait for them to boot.",
        "3.  On your computer, run brain_runner.py.",
        "4.  Wait for the brain to connect, then press PLAY.",
    };

    [MenuItem("Tools/RoboGarage UI/Add Setup Guide (Main Menu)")]
    public static void Build()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
        {
            Debug.LogWarning("[RoboGarageUIBuilder_GuideModal] Aborted — unsaved scene changes.");
            return;
        }

        Scene scene = EditorSceneManager.OpenScene("Assets/Scenes/MainMenu.unity", OpenSceneMode.Single);

        var bodyFont = LoadFont(ArchivoRegular);   // resolves to LiberationSans SDF
        var boldFont = LoadFont(ArchivoBold);      // also LiberationSans SDF
        if (bodyFont == null || boldFont == null)
        {
            Debug.LogError("[RoboGarageUIBuilder_GuideModal] Aborted — font assets missing.");
            return;
        }

        GameObject canvasGO = FindByName(scene, "Canvas");
        GameObject headerBarGO = FindByName(scene, "HeaderBar");
        if (canvasGO == null || headerBarGO == null)
        {
            Debug.LogError("[RoboGarageUIBuilder_GuideModal] Aborted — couldn't find Canvas/HeaderBar by name.");
            return;
        }

        if (FindByName(scene, "GuideButton") != null || FindByName(scene, "GuideModal") != null)
        {
            Debug.LogWarning("[RoboGarageUIBuilder_GuideModal] GuideButton/GuideModal already exist — aborted to avoid duplicates. Delete them first if you want a clean rebuild.");
            return;
        }

        var guideButton = BuildGuideButton(headerBarGO.transform, bodyFont);
        var (modalRoot, panel, canvasGroup, scrim, closeBtn, gotItBtn) = BuildGuideModal(canvasGO.transform, bodyFont, boldFont);

        var modal = modalRoot.gameObject.AddComponent<GuideModalUI>();
        SetRef(modal, "panel", panel);
        SetRef(modal, "canvasGroup", canvasGroup);
        SetRef(modal, "scrimButton", scrim);
        SetRef(modal, "closeButton", closeBtn);
        SetRef(modal, "openButton", guideButton);

        UnityEventTools.AddPersistentListener(gotItBtn.onClick, modal.Close);

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log("[RoboGarageUIBuilder_GuideModal] Setup Guide button + modal added to Main Menu.");
    }

    // ── Guide button (HeaderBar right side) ──────────────────────────────────────

    private static Button BuildGuideButton(Transform headerBar, TMP_FontAsset font)
    {
        var root = CreateUIObject("GuideButton", headerBar);
        root.anchorMin = new Vector2(1, 0.5f);
        root.anchorMax = new Vector2(1, 0.5f);
        root.pivot = new Vector2(1, 0.5f);
        root.anchoredPosition = new Vector2(-30, 0);
        root.sizeDelta = new Vector2(150, 40);

        // Soft breathing glow behind the button — bigger than the button itself, so it reads
        // as ambient light drawing the eye rather than a hard-edged shape competing with it.
        var glow = CreateImage("Glow", root, Colors.GarageOrange, LoadGlow());
        glow.raycastTarget = false;
        glow.rectTransform.anchorMin = glow.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
        glow.rectTransform.sizeDelta = new Vector2(230, 120);
        glow.rectTransform.anchoredPosition = Vector2.zero;
        glow.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
        var pulse = glow.gameObject.AddComponent<PulsingGlow>();
        SetRef(pulse, "glow", glow);

        var fill = CreateImage("Fill", root, Colors.GarageOrange, LoadRoundedRect(10), Image.Type.Sliced);
        StretchFill(fill.rectTransform);

        var shadow = CreateImage("InsetShadow", root, new Color(0, 0, 0, 0.28f), LoadRoundedRect(10), Image.Type.Sliced);
        StretchFill(shadow.rectTransform, 0, 0, 37, 0); // 3px bottom strip

        var content = CreateUIObject("Content", root);
        StretchFill(content);
        var label = CreateText("Label", content, "SETUP GUIDE", font, 14, Colors.TextOnOrange, 0.04f);
        label.fontStyle = FontStyles.Bold;

        var btn = root.gameObject.AddComponent<ArcadeButton>();
        btn.targetGraphic = fill;
        btn.transition = Selectable.Transition.ColorTint;
        btn.colors = new ColorBlock
        {
            normalColor = Colors.GarageOrange,
            highlightedColor = Colors.GarageOrangeHover,
            pressedColor = Colors.GarageOrangePressed,
            selectedColor = Colors.GarageOrange,
            disabledColor = Colors.GarageOrange,
            colorMultiplier = 1f,
            fadeDuration = 0.09f,
        };
        SetRef(btn, "content", content);
        SetRef(btn, "insetShadow", shadow.gameObject);

        return btn;
    }

    // ── Guide modal (centered, dimmed background) ────────────────────────────────

    private static (RectTransform modalRoot, RectTransform panel, CanvasGroup canvasGroup, Button scrim, Button closeBtn, Button gotItBtn)
        BuildGuideModal(Transform canvas, TMP_FontAsset bodyFont, TMP_FontAsset boldFont)
    {
        var modalRoot = CreateUIObject("GuideModal", canvas);
        StretchFill(modalRoot);
        var canvasGroup = modalRoot.gameObject.AddComponent<CanvasGroup>();

        var scrimImg = CreateImage("Scrim", modalRoot, Colors.Scrim);
        StretchFill(scrimImg.rectTransform);
        var scrimBtn = scrimImg.gameObject.AddComponent<Button>();
        scrimBtn.transition = Selectable.Transition.None;

        var (panelRoot, panelFill, panelBorder) = CreateRoundedPanel("Panel", modalRoot, new Vector2(560, 0),
            Colors.PopoverSurface, 14, Colors.ControlBorder, 1);
        panelRoot.anchorMin = new Vector2(0.5f, 0.5f);
        panelRoot.anchorMax = new Vector2(0.5f, 0.5f);
        panelRoot.pivot = new Vector2(0.5f, 0.5f);
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
        headerLE.preferredHeight = 56;
        headerLE.minHeight = 56;
        var headerBorder = CreateImage("BottomBorder", header, Colors.Divider);
        headerBorder.rectTransform.anchorMin = new Vector2(0, 0);
        headerBorder.rectTransform.anchorMax = new Vector2(1, 0);
        headerBorder.rectTransform.pivot = new Vector2(0.5f, 0f);
        headerBorder.rectTransform.sizeDelta = new Vector2(0, 1);
        headerBorder.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;

        var titleText = CreateText("Title", header, "SETUP GUIDE", boldFont, 18, Colors.TextPrimary, 0.04f, TextAlignmentOptions.Left);
        titleText.fontStyle = FontStyles.Bold;
        StretchFill(titleText.rectTransform, 20, 60, 0, 0);

        var closeRoot = CreateUIObject("CloseButton", header);
        closeRoot.anchorMin = new Vector2(1, 0.5f);
        closeRoot.anchorMax = new Vector2(1, 0.5f);
        closeRoot.pivot = new Vector2(1, 0.5f);
        closeRoot.anchoredPosition = new Vector2(-16, 0);
        closeRoot.sizeDelta = new Vector2(32, 32);
        var closeFill = CreateImage("Fill", closeRoot, Colors.ControlSurface, LoadRoundedRect(8), Image.Type.Sliced);
        StretchFill(closeFill.rectTransform);
        var closeContent = CreateUIObject("Content", closeRoot);
        StretchFill(closeContent);
        var closeLabel = CreateText("Label", closeContent, "✕", bodyFont, 14, Colors.TextSecondary);
        var closeBtn = closeRoot.gameObject.AddComponent<ArcadeButton>();
        closeBtn.targetGraphic = closeFill;
        closeBtn.transition = Selectable.Transition.ColorTint;
        closeBtn.colors = new ColorBlock
        {
            normalColor = Colors.ControlSurface,
            highlightedColor = Colors.ControlSurfaceHover,
            pressedColor = Colors.ControlSurfacePressed,
            selectedColor = Colors.ControlSurface,
            disabledColor = Colors.ControlSurface,
            colorMultiplier = 1f,
            fadeDuration = 0.09f,
        };
        SetRef(closeBtn, "content", closeContent);

        // Body — numbered steps as one readable block (short static checklist; no per-row
        // layout machinery needed for four lines of text).
        var body = CreateUIObject("Body", panelRoot);
        var bodyLE = body.gameObject.AddComponent<LayoutElement>();
        bodyLE.preferredHeight = 34 * GuideSteps.Length + 24;
        var stepsText = CreateText("Steps", body, string.Join("\n\n", GuideSteps), bodyFont, 16, Colors.TextPrimary, 0, TextAlignmentOptions.TopLeft);
        StretchFill(stepsText.rectTransform, 24, 24, 12, 12);
        stepsText.textWrappingMode = TextWrappingModes.Normal;

        // Footer — "Got it" dismiss button
        var footer = CreateUIObject("Footer", panelRoot);
        var footerLE = footer.gameObject.AddComponent<LayoutElement>();
        footerLE.preferredHeight = 76;
        var gotItRoot = CreateUIObject("GotItButton", footer);
        gotItRoot.anchorMin = new Vector2(0.5f, 0.5f);
        gotItRoot.anchorMax = new Vector2(0.5f, 0.5f);
        gotItRoot.pivot = new Vector2(0.5f, 0.5f);
        gotItRoot.sizeDelta = new Vector2(160, 44);
        var gotItFill = CreateImage("Fill", gotItRoot, Colors.GarageOrange, LoadRoundedRect(10), Image.Type.Sliced);
        StretchFill(gotItFill.rectTransform);
        var gotItShadow = CreateImage("InsetShadow", gotItRoot, new Color(0, 0, 0, 0.28f), LoadRoundedRect(10), Image.Type.Sliced);
        StretchFill(gotItShadow.rectTransform, 0, 0, 41, 0);
        var gotItContent = CreateUIObject("Content", gotItRoot);
        StretchFill(gotItContent);
        var gotItLabel = CreateText("Label", gotItContent, "GOT IT", boldFont, 15, Colors.TextOnOrange, 0.04f);
        gotItLabel.fontStyle = FontStyles.Bold;
        var gotItBtn = gotItRoot.gameObject.AddComponent<ArcadeButton>();
        gotItBtn.targetGraphic = gotItFill;
        gotItBtn.transition = Selectable.Transition.ColorTint;
        gotItBtn.colors = new ColorBlock
        {
            normalColor = Colors.GarageOrange,
            highlightedColor = Colors.GarageOrangeHover,
            pressedColor = Colors.GarageOrangePressed,
            selectedColor = Colors.GarageOrange,
            disabledColor = Colors.GarageOrange,
            colorMultiplier = 1f,
            fadeDuration = 0.09f,
        };
        SetRef(gotItBtn, "content", gotItContent);
        SetRef(gotItBtn, "insetShadow", gotItShadow.gameObject);

        return (modalRoot, panelRoot, canvasGroup, scrimBtn, closeBtn, gotItBtn);
    }

    // ── Scene lookup ──────────────────────────────────────────────────────────────

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
