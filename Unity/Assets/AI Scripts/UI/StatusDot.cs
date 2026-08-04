using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Connection status dot (spec §3.13) — connected pulses slowly with a glow, connecting
/// pulses faster with no glow, disconnected is solid.
/// </summary>
public class StatusDot : MonoBehaviour
{
    public enum State { Connected, Connecting, Disconnected }

    [SerializeField] private Image dot;
    [SerializeField] private Image glow; // only visible while Connected

    private State state = State.Disconnected;
    private float phase;

    public void SetState(State newState)
    {
        state = newState;
        phase = 0f;

        Color c = state switch
        {
            State.Connected => Colors.StatusGreen,
            State.Connecting => Colors.StatusAmber,
            _ => Colors.StatusRed,
        };
        if (dot != null) dot.color = new Color(c.r, c.g, c.b, 1f);
        if (glow != null)
        {
            glow.color = c;
            glow.gameObject.SetActive(state == State.Connected);
        }
    }

    private void Update()
    {
        if (state == State.Disconnected || dot == null) return;

        float period = state == State.Connected ? 2.4f : 1.2f;
        float minAlpha = state == State.Connected ? 0.35f : 0.35f;

        phase += Time.unscaledDeltaTime;
        float t = (Mathf.Sin(phase / period * Mathf.PI * 2f) + 1f) * 0.5f;
        float alpha = Mathf.Lerp(minAlpha, 1f, t);

        Color c = dot.color;
        c.a = alpha;
        dot.color = c;

        if (glow != null && state == State.Connected)
        {
            Color gc = glow.color;
            gc.a = alpha * 0.3f;
            glow.color = gc;
        }
    }
}
