using UnityEngine;
using TMPro;

/// <summary>
/// HUD timer (spec §3.10). The match clock counts up with no end condition
/// (LiveMatchManager), so this is a plain elapsed-time readout — the spec's
/// under-10s/under-5s urgency colours assume a countdown and don't apply here.
/// </summary>
public class TimerDisplayUI : MonoBehaviour
{
    [SerializeField] private TextMeshProUGUI timerText;

    public void SetSeconds(float elapsedSeconds)
    {
        if (timerText == null) return;
        int mins = (int)(elapsedSeconds / 60f);
        int secs = (int)(elapsedSeconds % 60f);
        timerText.text = $"{mins:00}:{secs:00}";
    }
}
