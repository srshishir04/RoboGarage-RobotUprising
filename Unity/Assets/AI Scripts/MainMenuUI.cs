using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// MainMenuUI — drives the main menu screen (visuals per the RoboGarage UI spec, direction 2a).
///
/// Two independent selection rows:
///   Row 1 — Difficulty : Easy | Medium | Hard
///   Row 2 — Match Mode : 1v1  | 2v2
///
/// Play button is disabled until BOTH rows have a selection. (The spec's Connection Pill is
/// cosmetic only in this build — there's no pre-match robot-connection check yet, so Play's
/// gating is unchanged from before: difficulty + match mode selected, nothing else.)
///
/// Medium and Hard buttons automatically check whether their .onnx file exists in
/// Application.streamingAssetsPath. If the file is missing the button still works (so you can
/// select it in the Editor during dev) but a warning label is shown. In a shipped build the
/// button is greyed out and shows "Coming soon" when the file is absent.
/// </summary>
public class MainMenuUI : MonoBehaviour
{
    [Header("Difficulty row")]
    [SerializeField] private SegmentButton btnEasy;
    [SerializeField] private SegmentButton btnMedium;
    [SerializeField] private SegmentButton btnHard;

    [Header("Match mode row")]
    [SerializeField] private SegmentButton btn1v1;
    [SerializeField] private SegmentButton btn2v2;

    [Header("Play / Exit")]
    [SerializeField] private Button btnPlay;
    [SerializeField] private Button btnExit;

    [Header("Optional: 'not trained yet' warning label")]
    [Tooltip("A TextMeshProUGUI label shown when selected brain file is missing. " +
             "Leave null to suppress the warning in UI (errors still go to Console).")]
    [SerializeField] private TextMeshProUGUI brainWarningText;

    // Nullable — null means "not yet selected by the player this session"
    private GameSettings.TrainingMode? selectedDifficulty = null;
    private GameSettings.MatchMode? selectedMatchMode = null;

    private void Start()
    {
        // Always reset to a clean state when returning to the main menu.
        // Prevents stale difficulty / match mode from a previous play session
        // bleeding into the new one.
        GameSettings.Reset();

        RefreshAllUI();
    }

    // ── Difficulty buttons ────────────────────────────────────────────────────

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

    // ── Match mode buttons ────────────────────────────────────────────────────

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

    // ── Play / Exit ───────────────────────────────────────────────────────────

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

    // ── UI refresh — one method drives everything ────────────────────────────

    /// <summary>Recomputes every visual element from the current selection state.</summary>
    private void RefreshAllUI()
    {
        RefreshSegment(btnEasy, selectedDifficulty == GameSettings.TrainingMode.Easy);
        RefreshSegment(btnMedium, selectedDifficulty == GameSettings.TrainingMode.Medium);
        RefreshSegment(btnHard, selectedDifficulty == GameSettings.TrainingMode.Hard);
        RefreshSegment(btn1v1, selectedMatchMode == GameSettings.MatchMode.OneVOne);
        RefreshSegment(btn2v2, selectedMatchMode == GameSettings.MatchMode.TwoVTwo);

        if (btnPlay != null) btnPlay.interactable = selectedDifficulty.HasValue && selectedMatchMode.HasValue;

        RefreshBrainWarning();
    }

    private static void RefreshSegment(SegmentButton segment, bool selected)
    {
        if (segment != null) segment.SetSelected(selected);
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
            brainWarningText.color = Colors.StatusGreen;
        }
        else
        {
            brainWarningText.text = $"⚠  {fileName} not found — train first";
            brainWarningText.color = Colors.StatusAmber;

            // In a shipped build, grey out the play button too so the player
            // can't start a match with a missing brain file.
            // In the Editor we leave Play enabled so you can test scene flow.
#if !UNITY_EDITOR
            if (btnPlay != null) btnPlay.interactable = false;
#endif
        }
    }
}
