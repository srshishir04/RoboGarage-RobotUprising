using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Exposure/Gain slider row (spec §3.6) — label + value + slider, with a 45%-opacity
/// disabled state driven by the Auto Exposure toggle (the one cross-control dependency
/// in the popover). Handle hover/drag visuals live on SliderHandleVisual (the handle
/// itself is what receives those pointer events, not this row).
/// </summary>
public class SliderRowUI : MonoBehaviour
{
    [SerializeField] private CanvasGroup canvasGroup;
    [SerializeField] private Slider slider;
    [SerializeField] private Image fill;
    [SerializeField] private TextMeshProUGUI labelText;
    [SerializeField] private TextMeshProUGUI valueText;

    public Slider Slider => slider;

    private const float DisabledAlpha = 0.45f;

    public void SetLabel(string text)
    {
        if (labelText != null) labelText.text = text;
    }

    public void SetValueText(string text)
    {
        if (valueText != null) valueText.text = text;
    }

    public void SetEnabled(bool enabled)
    {
        if (slider != null) slider.interactable = enabled;
        if (fill != null) fill.color = enabled ? Colors.SliderFillEnabled : Colors.SliderFillDisabled;
        if (canvasGroup != null)
        {
            canvasGroup.alpha = enabled ? 1f : DisabledAlpha;
            canvasGroup.interactable = enabled;
            canvasGroup.blocksRaycasts = enabled;
        }
    }

    public void SetDragging(bool dragging)
    {
        if (fill != null && slider != null && slider.interactable)
            fill.color = dragging ? Colors.GarageOrangeHover : Colors.SliderFillEnabled;
    }
}
