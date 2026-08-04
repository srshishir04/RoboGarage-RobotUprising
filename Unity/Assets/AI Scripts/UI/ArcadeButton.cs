using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Shared "pressed-plastic arcade key" depth language (spec §3 intro): an inset bottom
/// shadow that disappears on press while the content shifts down 2px, plus an optional
/// outer glow that only shows while idle/hovered (used by the PLAY button's drop shadow).
/// Colour tinting itself still comes from the normal Button/Selectable ColorBlock.
/// </summary>
public class ArcadeButton : Button
{
    [SerializeField] private RectTransform content;      // label/icon container that shifts +2y when pressed
    [SerializeField] private GameObject insetShadow;      // optional — not all buttons have one (e.g. Secondary)
    [SerializeField] private Image outerGlow;             // optional — PLAY button only
    [SerializeField] private float normalGlowAlpha = 0f;
    [SerializeField] private float hoverGlowAlpha = 0f;

    // Some buttons recolour their label per state too (e.g. Secondary on hover/disabled,
    // Primary on disabled) — others keep one static label colour throughout (STOP, SAVE).
    // Only applied once SetLabelColors has actually been called, so the latter case is a no-op.
    [SerializeField] private Graphic label;
    private ColorBlock labelColors;
    private bool labelColorsSet;

    private Vector2 contentRestPosition;
    private const float PressOffsetY = -2f;

    public void SetLabelColors(ColorBlock colors)
    {
        labelColors = colors;
        labelColorsSet = true;
    }

    protected override void Awake()
    {
        base.Awake();
        if (content != null) contentRestPosition = content.anchoredPosition;
    }

    /// <summary>Re-applies the current state's colours/shadow — used after something
    /// (e.g. SaveButtonFeedback's temporary "SAVED" flash) overrides them directly.</summary>
    public void ForceRefreshVisual() => DoStateTransition(currentSelectionState, true);

    protected override void DoStateTransition(SelectionState state, bool instant)
    {
        base.DoStateTransition(state, instant);

        bool pressed = state == SelectionState.Pressed;
        bool hovered = state == SelectionState.Highlighted;
        bool disabled = state == SelectionState.Disabled;

        if (content != null)
            content.anchoredPosition = contentRestPosition + (pressed ? new Vector2(0f, PressOffsetY) : Vector2.zero);

        if (insetShadow != null)
            insetShadow.SetActive(!pressed && !disabled);

        if (outerGlow != null)
        {
            float a = disabled || pressed ? 0f : (hovered ? hoverGlowAlpha : normalGlowAlpha);
            Color c = outerGlow.color;
            c.a = a;
            outerGlow.color = c;
        }

        if (label != null && labelColorsSet)
        {
            Color c = disabled ? labelColors.disabledColor
                    : hovered ? labelColors.highlightedColor
                    : pressed ? labelColors.pressedColor
                    : labelColors.normalColor;
            label.CrossFadeColor(c, instant ? 0f : labelColors.fadeDuration, true, true);
        }
    }
}
