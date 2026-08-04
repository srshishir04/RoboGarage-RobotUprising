using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// HUD resize behaviour (spec §4.3). At/above 1280 window width, bars stay pixel-constant —
/// this is just the CanvasScaler's normal match-height math, which this script replicates
/// manually so it can layer the extra behaviour below 1280 on top of it (Unity's own
/// ScaleWithScreenSize can't do this — its Match slider is one continuous formula, not two
/// different rules on either side of a threshold). Below 1280 width, an additional uniform
/// shrink kicks in; below 1024 the mode label hides to keep the top bar clusters from
/// overlapping the centred score pod; above a 2560-wide effective canvas, the bars clamp to
/// a centred safe area while the field keeps extending full-bleed behind them.
///
/// All thresholds are compared against raw window pixels (not canvas units) — simpler and
/// more predictable than mixing bases, at the cost of being a literal rather than a
/// canvas-space interpretation of the spec's numbers. Worth re-checking against the spec's
/// intent if the cluster-overlap threshold ever looks wrong in practice.
/// </summary>
public class HudResizeController : MonoBehaviour
{
    private const float ReferenceWidth = 1280f;
    private const float ReferenceHeight = 720f;
    private const float HideModeLabelBelowWidth = 1024f;
    private const float SafeAreaWidth = 2560f;

    [SerializeField] private CanvasScaler canvasScaler;
    [SerializeField] private RectTransform topBar;
    [SerializeField] private RectTransform bottomBar;
    [SerializeField] private GameObject modeLabel;

    private int lastWidth = -1;
    private int lastHeight = -1;

    private void Awake()
    {
        if (canvasScaler != null)
            canvasScaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
    }

    private void Update()
    {
        int width = Screen.width;
        int height = Screen.height;
        if (width == lastWidth && height == lastHeight) return;
        lastWidth = width;
        lastHeight = height;
        Apply(width, height);
    }

    private void Apply(int width, int height)
    {
        float heightScale = height / ReferenceHeight;
        float widthClamp = Mathf.Min(1f, width / ReferenceWidth);
        float scale = heightScale * widthClamp;

        if (canvasScaler != null) canvasScaler.scaleFactor = scale;

        if (modeLabel != null)
            modeLabel.SetActive(width >= HideModeLabelBelowWidth);

        ApplySafeArea(topBar, width, scale);
        ApplySafeArea(bottomBar, width, scale);
    }

    private static void ApplySafeArea(RectTransform bar, int screenWidthPx, float scale)
    {
        if (bar == null || scale <= 0f) return;

        float widthInCanvasUnits = screenWidthPx / scale;
        float excess = Mathf.Max(0f, widthInCanvasUnits - SafeAreaWidth) * 0.5f;

        bar.offsetMin = new Vector2(excess, bar.offsetMin.y);
        bar.offsetMax = new Vector2(-excess, bar.offsetMax.y);
    }
}
