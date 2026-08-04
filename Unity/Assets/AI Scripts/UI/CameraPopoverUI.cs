using System.Collections;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Camera settings popover open/close + animation (spec §5). Anchored top-right under the
/// gear button. Opens on gear click or the 'C' key; closes on gear click again, Esc, or
/// clicking outside — the full-screen catcher consumes that outside click so it doesn't
/// also hit the field beneath. Does not pause the match or block STOP (see Phase 4 notes —
/// the catcher sits behind the top/bottom bars in sibling order so their own buttons still
/// take priority for any click landing in the overlap).
/// </summary>
public class CameraPopoverUI : MonoBehaviour
{
    [SerializeField] private RectTransform panel;
    [SerializeField] private CanvasGroup canvasGroup;
    [SerializeField] private Button catcher;
    [SerializeField] private IconToggleButton gearButton;

    private bool isOpen;
    private Vector2 restingPosition;
    private Coroutine animRoutine;

    private void Awake()
    {
        restingPosition = panel.anchoredPosition;
        canvasGroup.alpha = 0f;
        panel.gameObject.SetActive(false);
        catcher.gameObject.SetActive(false);

        gearButton.onClick.AddListener(Toggle);
        catcher.onClick.AddListener(Close);
    }

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.C)) Toggle();
        else if (isOpen && Input.GetKeyDown(KeyCode.Escape)) Close();
    }

    public void Toggle()
    {
        if (isOpen) Close();
        else Open();
    }

    public void Open()
    {
        if (isOpen) return;
        isOpen = true;
        gearButton.SetActive(true);
        panel.gameObject.SetActive(true);
        catcher.gameObject.SetActive(true);
        Restart(Animate(true));
    }

    public void Close()
    {
        if (!isOpen) return;
        isOpen = false;
        gearButton.SetActive(false);
        catcher.gameObject.SetActive(false);
        Restart(Animate(false));
    }

    private void Restart(IEnumerator routine)
    {
        if (animRoutine != null) StopCoroutine(animRoutine);
        animRoutine = StartCoroutine(routine);
    }

    private IEnumerator Animate(bool opening)
    {
        const float openDuration = 0.14f;
        const float closeDuration = 0.10f;
        float duration = opening ? openDuration : closeDuration;

        float t = 0f;
        while (t < duration)
        {
            t += Time.unscaledDeltaTime;
            float u = Mathf.Clamp01(t / duration);
            float progress = opening ? EaseOutCubic(u) : 1f - EaseInCubic(u); // 0=closed, 1=open, either direction
            Apply(progress);
            yield return null;
        }

        Apply(opening ? 1f : 0f);
        if (!opening) panel.gameObject.SetActive(false);
    }

    private void Apply(float openProgress)
    {
        float scale = Mathf.Lerp(0.96f, 1f, openProgress);
        float yOffset = Mathf.Lerp(-6f, 0f, openProgress);
        panel.localScale = new Vector3(scale, scale, 1f);
        panel.anchoredPosition = restingPosition + new Vector2(0f, yOffset);
        canvasGroup.alpha = openProgress;
    }

    private static float EaseOutCubic(float x) => 1f - Mathf.Pow(1f - x, 3f);
    private static float EaseInCubic(float x) => x * x * x;
}
