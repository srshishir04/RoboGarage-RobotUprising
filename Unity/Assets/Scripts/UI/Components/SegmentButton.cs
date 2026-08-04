using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Segment button (spec §3.1) — Easy/Medium/Hard, 1v1/2v2. Seven visual states: the
/// four normal Selectable states crossed with a persistent "selected" flag (selection is
/// an application-level choice, not Unity's focus/navigation "Selected" — those are
/// unrelated concepts that happen to share a name).
/// </summary>
public class SegmentButton : Button
{
    [SerializeField] private Image fill;
    [SerializeField] private GameObject border;          // hidden while selected
    [SerializeField] private GameObject insetShadow;      // shown only while selected (not while selected+pressed)
    [SerializeField] private RectTransform content;
    [SerializeField] private TextMeshProUGUI label;
    [SerializeField] private TMP_FontAsset semiBoldFont;  // Archivo 600 — unselected
    [SerializeField] private TMP_FontAsset extraBoldFont; // Archivo 800 — selected

    private static readonly Color DisabledSurface = new Color32(0x1E, 0x1C, 0x1A, 0xFF);

    private Vector2 contentRestPosition;
    private bool isSelected;

    protected override void Awake()
    {
        base.Awake();
        if (content != null) contentRestPosition = content.anchoredPosition;
    }

    public void SetSelected(bool selected)
    {
        isSelected = selected;
        DoStateTransition(currentSelectionState, true);
    }

    protected override void DoStateTransition(SelectionState state, bool instant)
    {
        base.DoStateTransition(state, instant);

        Color fillColor, labelColor;
        bool shadow = false;
        bool pressed = state == SelectionState.Pressed;

        if (state == SelectionState.Disabled)
        {
            fillColor = DisabledSurface;
            labelColor = Colors.TextDisabled;
        }
        else if (isSelected)
        {
            labelColor = Colors.TextOnOrange;
            switch (state)
            {
                case SelectionState.Highlighted: fillColor = Colors.GarageOrangeHover; shadow = true; break;
                case SelectionState.Pressed: fillColor = Colors.GarageOrangePressed; break;
                default: fillColor = Colors.GarageOrange; shadow = true; break;
            }
        }
        else
        {
            switch (state)
            {
                case SelectionState.Highlighted: fillColor = Colors.ControlSurfaceHover; labelColor = Colors.TextBright; break;
                case SelectionState.Pressed: fillColor = Colors.ControlSurfacePressed; labelColor = Colors.TextSecondary; break;
                default: fillColor = Colors.ControlSurface; labelColor = Colors.TextSecondary; break;
            }
        }

        if (fill != null) fill.color = fillColor;
        if (label != null)
        {
            label.color = labelColor;
            label.font = isSelected ? extraBoldFont : semiBoldFont;
        }
        if (border != null) border.SetActive(!isSelected && state != SelectionState.Disabled);
        if (insetShadow != null) insetShadow.SetActive(shadow);
        if (content != null)
            content.anchoredPosition = contentRestPosition + (pressed ? new Vector2(0f, -2f) : Vector2.zero);
    }
}
