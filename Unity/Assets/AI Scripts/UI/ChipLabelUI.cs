using UnityEngine;
using TMPro;

/// <summary>Generic static chip label (spec §3.12) — no interactive states.</summary>
public class ChipLabelUI : MonoBehaviour
{
    [SerializeField] private TextMeshProUGUI label;

    public void SetText(string text)
    {
        if (label != null) label.text = text;
    }
}
