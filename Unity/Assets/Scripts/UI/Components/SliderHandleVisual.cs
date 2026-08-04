using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// Lives on the Slider's Handle graphic (spec §3.6) — hover grows it 16→18px and
/// brightens it; dragging brightens further and tells the owning row to tint the
/// fill orange-hover. Separate from SliderRowUI because pointer events must be
/// received by the handle rect itself, not the row root.
/// </summary>
[RequireComponent(typeof(Image))]
public class SliderHandleVisual : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerDownHandler, IPointerUpHandler
{
    [SerializeField] private SliderRowUI owner;
    [SerializeField] private Slider slider;
    [SerializeField] private RectTransform handleRect;
    [SerializeField] private Image handleImage;

    private const float RestSize = 16f;
    private const float HoverSize = 18f;

    private bool hovering;
    private bool dragging;

    private void Refresh()
    {
        bool interactable = slider == null || slider.interactable;
        float size = (hovering || dragging) && interactable ? HoverSize : RestSize;
        if (handleRect != null) handleRect.sizeDelta = new Vector2(size, size);

        Color c = !interactable ? Colors.SliderHandle
                : dragging ? Colors.TextPrimary
                : hovering ? Colors.TextBright
                : Colors.SliderHandle;
        if (handleImage != null) handleImage.color = c;
    }

    public void OnPointerEnter(PointerEventData eventData) { hovering = true; Refresh(); }
    public void OnPointerExit(PointerEventData eventData) { hovering = false; Refresh(); }

    public void OnPointerDown(PointerEventData eventData)
    {
        if (slider != null && !slider.interactable) return;
        dragging = true;
        Refresh();
        if (owner != null) owner.SetDragging(true);
    }

    public void OnPointerUp(PointerEventData eventData)
    {
        dragging = false;
        Refresh();
        if (owner != null) owner.SetDragging(false);
    }
}
