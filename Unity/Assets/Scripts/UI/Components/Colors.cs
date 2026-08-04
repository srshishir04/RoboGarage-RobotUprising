using UnityEngine;

/// <summary>
/// Single source of truth for the RoboGarage UI palette (spec §1).
/// Every screen/component should pull colours from here instead of hardcoding hex.
/// </summary>
public static class Colors
{
    private static Color Hex(string rgb, float alpha = 1f)
    {
        ColorUtility.TryParseHtmlString("#" + rgb, out Color c);
        c.a = alpha;
        return c;
    }

    // ── Surfaces ──────────────────────────────────────────────────────────────
    public static readonly Color Chassis = Hex("191715");           // Main Menu screen background
    public static readonly Color FieldBackground = Hex("100F0E");   // HUD field area, behind camera feed
    public static readonly Color BarSurface = Chassis;              // HUD top/bottom bar, popover parent
    public static readonly Color PopoverSurface = Hex("1F1C19");
    public static readonly Color ControlSurface = Hex("23201D");    // buttons, chips, pods
    public static readonly Color ControlBorder = Hex("34302B");
    public static readonly Color FooterBar = Hex("141210");         // Main Menu footer

    // Component-level hover/pressed tones (§3) reused across several unrelated
    // components — segment button, icon button, secondary button all share these.
    public static readonly Color ControlSurfaceHover = Hex("2B2724");
    public static readonly Color ControlBorderHover = Hex("3E3933");
    public static readonly Color ControlSurfacePressed = Hex("1E1B19");

    // ── Accent ────────────────────────────────────────────────────────────────
    public static readonly Color GarageOrange = Hex("FF4E00");
    public static readonly Color GarageOrangePressed = Hex("D94200");
    public static readonly Color GarageOrangeHover = Hex("FF6A26");

    // ── Text ──────────────────────────────────────────────────────────────────
    public static readonly Color TextPrimary = Hex("F5F1EC");       // "Bone"
    public static readonly Color TextOnOrange = Chassis;            // never white on orange
    public static readonly Color TextSecondary = Hex("B7B0A6");
    public static readonly Color TextTertiary = Hex("8A837A");      // micro-labels
    public static readonly Color TextMuted = Hex("6E685F");         // footer telemetry
    public static readonly Color TextDisabled = Hex("5A544C");
    public static readonly Color TextBright = Hex("D6D0C8");        // connection pill / popover values / HUD status text

    // ── Status ────────────────────────────────────────────────────────────────
    public static readonly Color StatusGreen = Hex("4BD37B");       // connected
    public static readonly Color StatusAmber = Hex("FFB020");       // connecting / waiting
    public static readonly Color StatusRed = Hex("E8362F");         // STOP / error — reserved for STOP only

    // ── Dividers / sliders ────────────────────────────────────────────────────
    public static readonly Color Divider = Hex("2A2622");
    public static readonly Color SliderTrackEmpty = Divider;
    public static readonly Color SliderFillEnabled = GarageOrange;
    public static readonly Color SliderFillDisabled = TextMuted;
    public static readonly Color SliderHandle = TextSecondary;

    // ── Glow / scrim (rgba) ───────────────────────────────────────────────────
    public static readonly Color TitleGlow = Hex("FF4E00", 0.28f);
    public static readonly Color Scrim = new Color(0f, 0f, 0f, 0.35f); // reserved; unused in 2a (popover doesn't dim the field)
}
