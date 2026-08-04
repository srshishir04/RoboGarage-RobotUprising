using System.Collections;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Icon button with a persistent "active" state (spec §3.5) — used for the gear/settings
/// button, which turns solid orange while its popover is open. Active doesn't define its
/// own hover/pressed variants in the spec, so it stays solid orange except for the shared
/// +2y press offset.
/// </summary>
public class IconToggleButton : Button
{
    [SerializeField] private Image fill;
    [SerializeField] private GameObject border;
    [SerializeField] private Image icon;
    [SerializeField] private RectTransform iconRect;
    [SerializeField] private RectTransform content;
    [SerializeField] private bool rotateIconOnActivate = true;

    private Vector2 contentRestPosition;
    private bool isActive;
    private Coroutine rotateRoutine;

    protected override void Awake()
    {
        base.Awake();
        if (content != null) contentRestPosition = content.anchoredPosition;
    }

    public void SetActive(bool active)
    {
        if (isActive == active) return;
        isActive = active;
        DoStateTransition(currentSelectionState, true);

        if (rotateIconOnActivate && iconRect != null)
        {
            if (rotateRoutine != null) StopCoroutine(rotateRoutine);
            rotateRoutine = StartCoroutine(RotateIcon(active ? 45f : 0f));
        }
    }

    private IEnumerator RotateIcon(float targetZ)
    {
        float startZ = iconRect.localEulerAngles.z;
        if (startZ > 180f) startZ -= 360f;
        const float duration = 0.18f;
        float t = 0f;
        while (t < duration)
        {
            t += Time.unscaledDeltaTime;
            float z = Mathf.Lerp(startZ, targetZ, Mathf.Clamp01(t / duration));
            iconRect.localEulerAngles = new Vector3(0f, 0f, z);
            yield return null;
        }
        iconRect.localEulerAngles = new Vector3(0f, 0f, targetZ);
    }

    protected override void DoStateTransition(SelectionState state, bool instant)
    {
        base.DoStateTransition(state, instant);

        bool pressed = state == SelectionState.Pressed;
        Color fillColor, iconColor;

        if (isActive)
        {
            fillColor = Colors.GarageOrange;
            iconColor = Colors.TextOnOrange;
        }
        else
        {
            switch (state)
            {
                case SelectionState.Highlighted: fillColor = Colors.ControlSurfaceHover; iconColor = Colors.TextBright; break;
                case SelectionState.Disabled: fillColor = Colors.ControlSurface; iconColor = Colors.TextDisabled; break;
                default: fillColor = Colors.ControlSurface; iconColor = Colors.TextSecondary; break;
            }
        }

        if (fill != null) fill.color = fillColor;
        if (icon != null) icon.color = iconColor;
        if (border != null) border.SetActive(!isActive);
        if (content != null)
            content.anchoredPosition = contentRestPosition + (pressed ? new Vector2(0f, -2f) : Vector2.zero);
    }
}
