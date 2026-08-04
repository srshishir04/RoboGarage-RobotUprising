using System.Collections;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Auto Exposure toggle (spec §3.7) — track + knob, 120ms ease-out knob travel,
/// 8%-lighten track on hover, 45% opacity when disabled (driven externally by
/// whatever owns the toggle — see SetInteractableAndFade).
/// </summary>
[RequireComponent(typeof(Toggle))]
public class AnimatedToggle : MonoBehaviour, UnityEngine.EventSystems.IPointerEnterHandler, UnityEngine.EventSystems.IPointerExitHandler
{
    [SerializeField] private Toggle toggle;
    [SerializeField] private Image track;
    [SerializeField] private RectTransform knob;
    [SerializeField] private Image knobImage;
    [SerializeField] private float insetFromEdge = 3f;

    private static readonly Color TrackOn = Colors.GarageOrange;
    private static readonly Color TrackOff = Colors.Divider;
    private static readonly Color KnobOn = Colors.PopoverSurface;   // #1F1C19
    private static readonly Color KnobOff = Colors.TextSecondary;

    private float leftX, rightX;
    private bool hovering;
    private Coroutine travelRoutine;

    private void Awake()
    {
        if (toggle == null) toggle = GetComponent<Toggle>();
        RectTransform trackRect = track != null ? track.rectTransform : (RectTransform)transform;
        // The knob is anchored to the track's LEFT edge (anchorMin/Max = (0, 0.5)), not its
        // centre, so both rest positions are measured as positive offsets from that edge —
        // not a symmetric +/-half around a centre anchor that isn't actually there.
        float knobRadius = knob.rect.width * 0.5f;
        leftX = insetFromEdge + knobRadius;
        rightX = trackRect.rect.width - insetFromEdge - knobRadius;

        toggle.onValueChanged.AddListener(OnValueChanged);
        SnapToState(toggle.isOn);
    }

    public void SetState(bool on, bool animate = true)
    {
        toggle.SetIsOnWithoutNotify(on);
        if (animate) OnValueChanged(on);
        else SnapToState(on);
    }

    private void OnValueChanged(bool on)
    {
        if (travelRoutine != null) StopCoroutine(travelRoutine);
        travelRoutine = StartCoroutine(AnimateKnob(on));
        if (track != null) track.color = on ? TrackOn : TrackOff;
    }

    private void SnapToState(bool on)
    {
        if (knob != null) knob.anchoredPosition = new Vector2(on ? rightX : leftX, knob.anchoredPosition.y);
        if (track != null) track.color = on ? TrackOn : TrackOff;
        if (knobImage != null) knobImage.color = on ? KnobOn : KnobOff;
    }

    private IEnumerator AnimateKnob(bool on)
    {
        float startX = knob.anchoredPosition.x;
        float endX = on ? rightX : leftX;
        Color startColor = knobImage != null ? knobImage.color : Color.white;
        Color endColor = on ? KnobOn : KnobOff;
        const float duration = 0.12f;
        float t = 0f;
        while (t < duration)
        {
            t += Time.unscaledDeltaTime;
            float k = 1f - Mathf.Pow(1f - Mathf.Clamp01(t / duration), 2f); // ease-out
            knob.anchoredPosition = new Vector2(Mathf.Lerp(startX, endX, k), knob.anchoredPosition.y);
            if (knobImage != null) knobImage.color = Color.Lerp(startColor, endColor, k);
            yield return null;
        }
        knob.anchoredPosition = new Vector2(endX, knob.anchoredPosition.y);
        if (knobImage != null) knobImage.color = endColor;
    }

    public void OnPointerEnter(UnityEngine.EventSystems.PointerEventData eventData)
    {
        if (!toggle.interactable) return;
        hovering = true;
        if (track != null) track.color = Lighten(toggle.isOn ? TrackOn : TrackOff, 0.08f);
    }

    public void OnPointerExit(UnityEngine.EventSystems.PointerEventData eventData)
    {
        hovering = false;
        if (track != null) track.color = toggle.isOn ? TrackOn : TrackOff;
    }

    private static Color Lighten(Color c, float amount)
    {
        return new Color(
            c.r + (1f - c.r) * amount,
            c.g + (1f - c.g) * amount,
            c.b + (1f - c.b) * amount,
            c.a);
    }
}
