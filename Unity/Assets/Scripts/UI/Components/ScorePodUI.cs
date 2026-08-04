using System.Collections;
using UnityEngine;
using TMPro;

/// <summary>
/// HUD score pod (spec §3.9). JetBrains Mono is monospaced so the digits are already
/// tabular-figure — no reflow on a goal without needing a font feature flag.
/// </summary>
public class ScorePodUI : MonoBehaviour
{
    [SerializeField] private TextMeshProUGUI humanDigits;
    [SerializeField] private TextMeshProUGUI robotDigits;

    private int lastHuman;
    private int lastRobot;

    public void SetScore(int human, int robot)
    {
        bool humanChanged = human != lastHuman;
        bool robotChanged = robot != lastRobot;
        lastHuman = human;
        lastRobot = robot;

        if (humanDigits != null)
        {
            humanDigits.text = human.ToString();
            if (humanChanged) StartCoroutine(PopScale(humanDigits.rectTransform));
        }
        if (robotDigits != null)
        {
            robotDigits.text = robot.ToString();
            if (robotChanged) StartCoroutine(PopScale(robotDigits.rectTransform));
        }
    }

    private static IEnumerator PopScale(RectTransform rt)
    {
        const float duration = 0.26f;
        const float peak = 1.18f;
        float t = 0f;
        while (t < duration)
        {
            t += Time.unscaledDeltaTime;
            float u = Mathf.Clamp01(t / duration);
            float eased = 1f - Mathf.Pow(1f - u, 2f); // ease-out
            float scale = eased < 0.5f
                ? Mathf.Lerp(1f, peak, eased / 0.5f)
                : Mathf.Lerp(peak, 1f, (eased - 0.5f) / 0.5f);
            rt.localScale = new Vector3(scale, scale, 1f);
            yield return null;
        }
        rt.localScale = Vector3.one;
    }
}
