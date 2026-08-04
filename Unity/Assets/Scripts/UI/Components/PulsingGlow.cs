using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Continuous breathing-glow pulse for anything that needs to catch the eye without being
/// frantic about it (e.g. the Setup Guide button) — same sine-based technique as StatusDot's
/// connected-state pulse, just standalone/reusable rather than tied to connection state.
/// </summary>
public class PulsingGlow : MonoBehaviour
{
    [SerializeField] private Image glow;
    [SerializeField] private float periodSeconds = 1.8f;
    [SerializeField] private float minAlpha = 0.25f;
    [SerializeField] private float maxAlpha = 0.65f;

    private float phase;
    private Color baseColor;

    private void Awake()
    {
        if (glow != null) baseColor = glow.color;
    }

    private void Update()
    {
        if (glow == null) return;

        phase += Time.unscaledDeltaTime;
        float t = (Mathf.Sin(phase / periodSeconds * Mathf.PI * 2f) + 1f) * 0.5f;
        Color c = baseColor;
        c.a = Mathf.Lerp(minAlpha, maxAlpha, t);
        glow.color = c;
    }
}
