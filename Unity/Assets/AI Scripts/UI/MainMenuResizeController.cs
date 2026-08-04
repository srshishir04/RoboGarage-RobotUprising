using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Main Menu resize behaviour (spec §4.3). Title shrinks 72→56px below 1024 window width;
/// the title/controls gap shrinks 34→20 below 640 window height so nothing clips in a short
/// window. The controls column's fixed 560 width + centering already comes for free from its
/// own LayoutElement/VerticalLayoutGroup setup (see RoboGarageUIBuilder_Phase3) — no extra
/// runtime logic needed for that part.
/// </summary>
public class MainMenuResizeController : MonoBehaviour
{
    private const float TitleSizeDefault = 72f;
    private const float TitleSizeSmall = 56f;
    private const float TitleWidthThreshold = 1024f;

    private const float GapDefault = 34f;
    private const float GapSmall = 20f;
    private const float GapHeightThreshold = 640f;

    [SerializeField] private TextMeshProUGUI title;
    [SerializeField] private VerticalLayoutGroup contentLayout; // outer ContentBlock group (title/pill <-> controls gap)

    private int lastWidth = -1;
    private int lastHeight = -1;

    private void Update()
    {
        int width = Screen.width;
        int height = Screen.height;
        if (width == lastWidth && height == lastHeight) return;
        lastWidth = width;
        lastHeight = height;

        if (title != null)
            title.fontSize = width < TitleWidthThreshold ? TitleSizeSmall : TitleSizeDefault;

        if (contentLayout != null)
            contentLayout.spacing = height < GapHeightThreshold ? GapSmall : GapDefault;
    }
}
