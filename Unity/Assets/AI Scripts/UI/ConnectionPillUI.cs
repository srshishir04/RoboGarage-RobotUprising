using System;
using UnityEngine;
using TMPro;
using UnityEngine.UI;

/// <summary>
/// Main Menu connection pill (spec §3.14). Geometry is fixed across all three states —
/// only the text/dot/retry-button visibility change.
/// </summary>
public class ConnectionPillUI : MonoBehaviour
{
    [SerializeField] private StatusDot dot;
    [SerializeField] private TextMeshProUGUI statusText;
    [SerializeField] private TextMeshProUGUI latencyText;
    [SerializeField] private Button retryButton;

    private static readonly Color LatencyColor = new Color32(0x7C, 0x76, 0x6D, 0xFF);

    private Action onRetry;

    private void Awake()
    {
        if (retryButton != null) retryButton.onClick.AddListener(() => onRetry?.Invoke());
    }

    public void SetConnected(int latencyMs, int robotCount)
    {
        dot?.SetState(StatusDot.State.Connected);
        if (statusText != null)
        {
            statusText.text = $"{robotCount} robot{(robotCount == 1 ? "" : "s")} connected";
            statusText.color = Colors.TextBright;
        }
        if (latencyText != null)
        {
            latencyText.gameObject.SetActive(true);
            latencyText.text = $"· {latencyMs}ms";
            latencyText.color = LatencyColor;
        }
        if (retryButton != null) retryButton.gameObject.SetActive(false);
    }

    public void SetConnecting()
    {
        dot?.SetState(StatusDot.State.Connecting);
        if (statusText != null)
        {
            statusText.text = "Searching for robots…";
            statusText.color = Colors.TextBright;
        }
        if (latencyText != null) latencyText.gameObject.SetActive(false);
        if (retryButton != null) retryButton.gameObject.SetActive(false);
    }

    public void SetFailed(Action retryCallback)
    {
        dot?.SetState(StatusDot.State.Disconnected);
        if (statusText != null)
        {
            statusText.text = "No robots found";
            statusText.color = Colors.StatusRed;
        }
        if (latencyText != null) latencyText.gameObject.SetActive(false);
        onRetry = retryCallback;
        if (retryButton != null) retryButton.gameObject.SetActive(true);
    }
}
