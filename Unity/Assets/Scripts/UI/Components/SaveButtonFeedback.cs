using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// SAVE button post-save flash (spec §3.8) — label swaps to "SAVED", fill turns green,
/// for 1.2s, then reverts to the button's normal state colouring.
/// </summary>
public class SaveButtonFeedback : MonoBehaviour
{
    [SerializeField] private ArcadeButton button;
    [SerializeField] private Image fill;
    [SerializeField] private TextMeshProUGUI label;

    private const string DefaultLabel = "SAVE";
    private const string SavedLabel = "SAVED";
    private const float HoldSeconds = 1.2f;

    public void ShowSaved()
    {
        StopAllCoroutines();
        StartCoroutine(FlashRoutine());
    }

    private IEnumerator FlashRoutine()
    {
        if (label != null) label.text = SavedLabel;
        if (fill != null) fill.color = Colors.StatusGreen;

        yield return new WaitForSecondsRealtime(HoldSeconds);

        if (label != null) label.text = DefaultLabel;
        if (button != null) button.ForceRefreshVisual();
    }
}
