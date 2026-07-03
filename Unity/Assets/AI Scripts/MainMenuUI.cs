using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// MainMenuUI — drives the main menu screen.
///
/// Two independent selection rows:
///   Row 1 — Difficulty : Easy | Medium | Hard
///   Row 2 — Match Mode : 1v1  | 2v2
///
/// Play button is disabled until BOTH rows have a selection.
///
/// Medium and Hard buttons automatically check whether their .onnx file
/// exists in Application.streamingAssetsPath. If the file is missing the
/// button still works (so you can select it in the Editor during dev) but
/// a warning label is shown. In a shipped build the button is greyed out
/// and shows "Coming soon" when the file is absent.
///
/// ── SCENE SETUP ─────────────────────────────────────────────────────────────
///
/// Canvas
///   Panel_Title
///     TitleText              ← "ROBOT SOCCER"
///   Panel_Difficulty
///     LabelDifficulty        ← static label "DIFFICULTY"
///     BtnEasy                ← Button + Image + child TextMeshProUGUI
///     BtnMedium              ← Button + Image + child TextMeshProUGUI
///     BtnHard                ← Button + Image + child TextMeshProUGUI
///   Panel_MatchMode
///     LabelMatchMode         ← static label "MATCH MODE"
///     Btn1v1                 ← Button + Image + child TextMeshProUGUI
///     Btn2v2                 ← Button + Image + child TextMeshProUGUI
///   Panel_Summary
///     SummaryText            ← e.g. "1v1  ·  Easy"
///   BtnPlay                  ← Button + Image
///   BtnExit                  ← Button
///
/// Wire each button's onClick to the matching public method on this script.
/// </summary>
public class MainMenuUI : MonoBehaviour
{
    // =========================================================================
    //  COLOURS  (defined once — no magic literals in methods)
    // =========================================================================

    // Difficulty buttons
    private static readonly Color C_EASY_ON = new Color(0.08f, 0.38f, 0.13f);
    private static readonly Color C_EASY_OFF = new Color(0.04f, 0.18f, 0.07f);
    private static readonly Color C_MEDIUM_ON = new Color(0.50f, 0.38f, 0.05f);
    private static readonly Color C_MEDIUM_OFF = new Color(0.22f, 0.17f, 0.03f);
    private static readonly Color C_HARD_ON = new Color(0.50f, 0.08f, 0.08f);
    private static readonly Color C_HARD_OFF = new Color(0.25f, 0.05f, 0.05f);

    // Match mode buttons
    private static readonly Color C_MODE_ON = new Color(0.10f, 0.25f, 0.55f);
    private static readonly Color C_MODE_OFF = new Color(0.05f, 0.10f, 0.25f);

    // Play button
    private static readonly Color C_PLAY_ON = new Color(0.10f, 0.55f, 0.20f);
    private static readonly Color C_PLAY_OFF = new Color(0.15f, 0.15f, 0.15f);

    // Summary text colours — match difficulty
    private static readonly Color C_TEXT_EASY = new Color(0.23f, 0.80f, 0.35f);
    private static readonly Color C_TEXT_MEDIUM = new Color(0.86f, 0.67f, 0.16f);
    private static readonly Color C_TEXT_HARD = new Color(0.82f, 0.23f, 0.23f);
    private static readonly Color C_TEXT_NONE = new Color(0.50f, 0.50f, 0.50f);

    // =========================================================================
    //  INSPECTOR — drag UI elements here
    // =========================================================================

    [Header("Difficulty row — button background Images")]
    [SerializeField] private Image btnEasyImage;
    [SerializeField] private Image btnMediumImage;
    [SerializeField] private Image btnHardImage;

    [Header("Difficulty row — button root Buttons (for interactable toggle)")]
    [SerializeField] private Button btnEasy;
    [SerializeField] private Button btnMedium;
    [SerializeField] private Button btnHard;

    [Header("Match mode row — button background Images")]
    [SerializeField] private Image btn1v1Image;
    [SerializeField] private Image btn2v2Image;

    [Header("Match mode row — button root Buttons")]
    [SerializeField] private Button btn1v1;
    [SerializeField] private Button btn2v2;

    [Header("Summary + Play")]
    [SerializeField] private TextMeshProUGUI summaryText;
    [SerializeField] private Button btnPlay;
    [SerializeField] private Image btnPlayImage;
    [SerializeField] private TextMeshProUGUI btnPlayText;

    [Header("Optional: 'not trained yet' warning label")]
    [Tooltip("A TextMeshProUGUI label shown when selected brain file is missing. " +
             "Leave null to suppress the warning in UI (errors still go to Console).")]
    [SerializeField] private TextMeshProUGUI brainWarningText;

    // =========================================================================
    //  INTERNAL STATE
    // =========================================================================

    // Nullable — null means "not yet selected by the player this session"
    private GameSettings.TrainingMode? selectedDifficulty = null;
    private GameSettings.MatchMode? selectedMatchMode = null;

    // =========================================================================
    //  LIFECYCLE
    // =========================================================================

    private void Start()
    {
        // Always reset to a clean state when returning to the main menu.
        // Prevents stale difficulty / match mode from a previous play session
        // bleeding into the new one.
        GameSettings.Reset();

        RefreshAllUI();
    }

    // =========================================================================
    //  DIFFICULTY BUTTONS
    // =========================================================================

    /// <summary>onClick → BtnEasy</summary>
    public void OnEasyClicked()
    {
        selectedDifficulty = GameSettings.TrainingMode.Easy;
        GameSettings.SetDifficulty(GameSettings.TrainingMode.Easy);
        RefreshAllUI();
    }

    /// <summary>onClick → BtnMedium</summary>
    public void OnMediumClicked()
    {
        selectedDifficulty = GameSettings.TrainingMode.Medium;
        GameSettings.SetDifficulty(GameSettings.TrainingMode.Medium);
        RefreshAllUI();
    }

    /// <summary>onClick → BtnHard</summary>
    public void OnHardClicked()
    {
        selectedDifficulty = GameSettings.TrainingMode.Hard;
        GameSettings.SetDifficulty(GameSettings.TrainingMode.Hard);
        RefreshAllUI();
    }

    // =========================================================================
    //  MATCH MODE BUTTONS
    // =========================================================================

    /// <summary>onClick → Btn1v1</summary>
    public void On1v1Clicked()
    {
        selectedMatchMode = GameSettings.MatchMode.OneVOne;
        GameSettings.SetMatchMode(GameSettings.MatchMode.OneVOne);
        RefreshAllUI();
    }

    /// <summary>onClick → Btn2v2</summary>
    public void On2v2Clicked()
    {
        selectedMatchMode = GameSettings.MatchMode.TwoVTwo;
        GameSettings.SetMatchMode(GameSettings.MatchMode.TwoVTwo);
        RefreshAllUI();
    }

    // =========================================================================
    //  PLAY / EXIT
    // =========================================================================

    /// <summary>onClick → BtnPlay</summary>
    public void OnPlayClicked()
    {
        // Guard: both must be selected (button should already be non-interactable,
        // but double-check in case the Inspector wiring was skipped)
        if (selectedDifficulty == null || selectedMatchMode == null)
        {
            Debug.LogWarning("[MainMenuUI] Play pressed but selection incomplete.");
            return;
        }

        Debug.Log($"[MainMenuUI] Starting match — " +
                  $"{GameSettings.MatchLabel} · {GameSettings.DifficultyLabel} · " +
                  $"obs={GameSettings.ObsSize} · model={GameSettings.OnnxFileName}");

        SceneManager.LoadScene("GameScene");
    }

    /// <summary>onClick → BtnExit</summary>
    public void OnExitClicked()
    {
        Application.Quit();
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#endif
    }

    // =========================================================================
    //  UI REFRESH — one method drives everything
    // =========================================================================

    /// <summary>
    /// Recomputes every visual element from the current selection state.
    /// Call after any selection changes.
    /// </summary>
    private void RefreshAllUI()
    {
        RefreshDifficultyButtons();
        RefreshMatchModeButtons();
        RefreshSummary();
        RefreshPlayButton();
        RefreshBrainWarning();
    }

    // ── Difficulty row ────────────────────────────────────────────────────────

    private void RefreshDifficultyButtons()
    {
        SetButtonColour(btnEasyImage,
            selectedDifficulty == GameSettings.TrainingMode.Easy,
            C_EASY_ON, C_EASY_OFF);

        SetButtonColour(btnMediumImage,
            selectedDifficulty == GameSettings.TrainingMode.Medium,
            C_MEDIUM_ON, C_MEDIUM_OFF);

        SetButtonColour(btnHardImage,
            selectedDifficulty == GameSettings.TrainingMode.Hard,
            C_HARD_ON, C_HARD_OFF);
    }

    // ── Match mode row ────────────────────────────────────────────────────────

    private void RefreshMatchModeButtons()
    {
        SetButtonColour(btn1v1Image,
            selectedMatchMode == GameSettings.MatchMode.OneVOne,
            C_MODE_ON, C_MODE_OFF);

        SetButtonColour(btn2v2Image,
            selectedMatchMode == GameSettings.MatchMode.TwoVTwo,
            C_MODE_ON, C_MODE_OFF);
    }

    // ── Summary line ──────────────────────────────────────────────────────────

    private void RefreshSummary()
    {
        if (summaryText == null) return;

        if (selectedDifficulty == null && selectedMatchMode == null)
        {
            summaryText.text = "Select difficulty and match mode";
            summaryText.color = C_TEXT_NONE;
            return;
        }

        string diffPart = selectedDifficulty.HasValue
            ? selectedDifficulty.Value.ToString()
            : "—";

        string modePart = selectedMatchMode.HasValue
            ? GameSettings.MatchLabel    // "1v1" or "2v2"
            : "—";

        summaryText.text = $"{modePart}  ·  {diffPart}";
        summaryText.color = DifficultyColour(selectedDifficulty);
    }

    // ── Play button ───────────────────────────────────────────────────────────

    private void RefreshPlayButton()
    {
        bool ready = selectedDifficulty.HasValue && selectedMatchMode.HasValue;

        if (btnPlay != null) btnPlay.interactable = ready;
        if (btnPlayImage != null) btnPlayImage.color = ready ? C_PLAY_ON : C_PLAY_OFF;
        if (btnPlayText != null) btnPlayText.color = ready
            ? Color.white
            : new Color(1f, 1f, 1f, 0.3f);
    }

    // ── Brain file warning ────────────────────────────────────────────────────

    /// <summary>
    /// Checks whether the selected .onnx file actually exists in StreamingAssets.
    /// Shows a warning if it doesn't — useful during development so you know
    /// which brains still need to be trained.
    ///
    /// In the Unity Editor StreamingAssets lives at Assets/StreamingAssets/.
    /// In a build it is in <build>/GameName_Data/StreamingAssets/.
    ///
    /// brain_runner.py loads the .onnx from the same directory it is launched
    /// from — this check is purely informational for the UI.
    /// </summary>
    private void RefreshBrainWarning()
    {
        if (brainWarningText == null) return;

        if (!selectedDifficulty.HasValue)
        {
            brainWarningText.text = "";
            return;
        }

        string fileName = GameSettings.OnnxFileName;
        string path = Path.Combine(Application.streamingAssetsPath, fileName);
        bool exists = File.Exists(path);

        if (exists)
        {
            brainWarningText.text = $"✓  {fileName} ready";
            brainWarningText.color = new Color(0.3f, 0.9f, 0.4f);
        }
        else
        {
            brainWarningText.text = $"⚠  {fileName} not found — train first";
            brainWarningText.color = new Color(0.9f, 0.6f, 0.1f);

            // In a shipped build, grey out the play button too so the player
            // can't start a match with a missing brain file.
            // In the Editor we leave Play enabled so you can test scene flow.
#if !UNITY_EDITOR
            if (btnPlay      != null) btnPlay.interactable = false;
            if (btnPlayImage != null) btnPlayImage.color   = C_PLAY_OFF;
#endif
        }
    }

    // =========================================================================
    //  HELPERS
    // =========================================================================

    private static void SetButtonColour(Image img, bool selected,
                                        Color onColour, Color offColour)
    {
        if (img != null)
            img.color = selected ? onColour : offColour;
    }

    private static Color DifficultyColour(GameSettings.TrainingMode? d)
    {
        if (!d.HasValue) return C_TEXT_NONE;
        switch (d.Value)
        {
            case GameSettings.TrainingMode.Easy: return C_TEXT_EASY;
            case GameSettings.TrainingMode.Medium: return C_TEXT_MEDIUM;
            case GameSettings.TrainingMode.Hard: return C_TEXT_HARD;
            default: return C_TEXT_NONE;
        }
    }
}