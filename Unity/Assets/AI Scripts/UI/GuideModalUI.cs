using System.Collections;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Setup Guide modal — unlike the camera popover, this one deliberately dims the background
/// (Colors.Scrim — defined from the start but never used until now, since the camera popover
/// explicitly doesn't dim the field). Opens on the guide button or the 'G' key, closes on the
/// close button, Esc, or clicking the scrim.
/// </summary>
public class GuideModalUI : MonoBehaviour
{
    [SerializeField] private RectTransform panel;
    [SerializeField] private CanvasGroup canvasGroup;
    [SerializeField] private Button scrimButton;
    [SerializeField] private Button closeButton;
    [SerializeField] private Button openButton;

    private bool isOpen;
    private Coroutine animRoutine;

    private void Awake()
    {
        canvasGroup.alpha = 0f;
        gameObject.SetActive(false);

        if (openButton != null) openButton.onClick.AddListener(Toggle);
        if (closeButton != null) closeButton.onClick.AddListener(Close);
        if (scrimButton != null) scrimButton.onClick.AddListener(Close);
    }

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.G)) Toggle();
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
        gameObject.SetActive(true);
        Restart(Animate(true));
    }

    public void Close()
    {
        if (!isOpen) return;
        isOpen = false;
        Restart(Animate(false));
    }

    private void Restart(IEnumerator routine)
    {
        if (animRoutine != null) StopCoroutine(animRoutine);
        animRoutine = StartCoroutine(routine);
    }

    private IEnumerator Animate(bool opening)
    {
        const float duration = 0.16f;
        float t = 0f;
        while (t < duration)
        {
            t += Time.unscaledDeltaTime;
            float u = Mathf.Clamp01(t / duration);
            float e = opening ? 1f - Mathf.Pow(1f - u, 3f) : 1f - (u * u * u);
            canvasGroup.alpha = e;
            float scale = Mathf.Lerp(0.94f, 1f, e);
            panel.localScale = new Vector3(scale, scale, 1f);
            yield return null;
        }
        canvasGroup.alpha = opening ? 1f : 0f;
        panel.localScale = opening ? Vector3.one : new Vector3(0.94f, 0.94f, 1f);
        if (!opening) gameObject.SetActive(false);
    }
}
