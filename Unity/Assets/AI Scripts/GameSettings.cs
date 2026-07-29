using UnityEngine;

/* ── The central settings manager of the whole game ────────
 * Stores game settings
 * Stores difficulty
 * Stores match mode
 * Stores AI model names
 * Calculates observation size
 * Helps UI
 * Helps AI
 * Helps match manager
 * ─────────────────────────────────────────────────────── */


public static class GameSettings
{
    // enum -> a list of fixed choice -> we can write "Difficulty = TrainingMode.Easy" ; "Match = MatchMode.OneVOne" later
    public enum TrainingMode
    {
        Easy = 0,   // 14-float obs, no opponent awareness
        Medium = 1,   // 19-float obs, opponent position + heading + has_ball
        Hard = 2,   // 24-float obs, + opponent goal + role flag + opp dist
    }

    public enum MatchMode
    {
        OneVOne = 1,   // 1 AI robot vs 1 human robot
        TwoVTwo = 2,   // 2 AI robots vs 2 human robots
    }

    //  CURRENT SETTINGS  (defaults shown)
    // Static becuase any script can use it, Example: "GameSettings.Difficulty"
    public static TrainingMode Difficulty = TrainingMode.Easy; 
    public static MatchMode Match = MatchMode.OneVOne; 

    // AI brain file - Loaded by GameSceneUI / brain_runner.py at match start.
    // Filename convention: <mode>_<difficulty>.onnx (e.g. "1v1_easy.onnx", "2v2_hard.onnx"),
    // matching ai_robot/brains/ and config.py's ONNX_PATHS.
    public static string OnnxFileName = "1v1_easy.onnx";

    // Human-controlled opponent (future use).
    public static string OpponentOnnxFileName = "";

    // Total number of observation values sent to the AI brain
    // Changes automatically based on difficulty
    public static int ObsSize => DeriveObsSize(Difficulty); // => means automatically calculate this value

    // Robots per team -
    // Automatically derived from MatchMode. MatchManager.cs and coordinator.py both read this.
    public static int RobotsPerTeam => (int)Match;   // (int) converts enum to number, Example: OneVOne=1, TwoVTwo=2, so (int)Match = 1 or 2

    // Display strings 
    public static string DifficultyLabel => Difficulty.ToString(); // converts enum into text, Example: "TrainingMode.Easy" becomes "Easy"
    public static string MatchLabel => Match == MatchMode.OneVOne ? "1v1" : "2v2";

    // This function changes difficulty
    // MainMenuUI difficulty buttons: "GameSettings.SetDifficulty(TrainingMode.Hard)", 1. difficulty changes, 2. ONNX file updates, 3. logs debug info
    public static void SetDifficulty(TrainingMode difficulty)
    {
        Difficulty = difficulty;
        OnnxFileName = DeriveOnnxFileName(Match, difficulty);

        Debug.Log($"[GameSettings] Difficulty = {Difficulty}  " +
                  $"obs = {ObsSize}  model = {OnnxFileName}");
    }

    // Set match mode. Updates RobotsPerTeam automatically.
    // Call from MainMenuUI mode buttons.
    // Also re-derives OnnxFileName, since 1v1 and 2v2 use different brain files
    // for the same difficulty (e.g. "1v1_easy.onnx" vs "2v2_easy.onnx").
    public static void SetMatchMode(MatchMode mode)
    {
        Match = mode;
        OnnxFileName = DeriveOnnxFileName(Match, Difficulty);

        Debug.Log($"[GameSettings] MatchMode = {MatchLabel}  " +
                  $"robots/team = {RobotsPerTeam}  model = {OnnxFileName}");
    }

    // Reset all settings to defaults.
    // Call when returning to the main menu from a match
    public static void Reset()
    {
        Difficulty = TrainingMode.Easy;
        Match = MatchMode.OneVOne;
        OnnxFileName = DeriveOnnxFileName(Match, Difficulty);
        OpponentOnnxFileName = "";
    }

    // VALIDATION  (call before loading the game scene)
    // Returns true if a valid difficulty AND match mode have been chosen.
    // MainMenuUI gates the Play button on this.
    public static bool IsReadyToPlay()
    {
        // Difficulty is always valid (enum with a default).
        // Match is always valid. So we just sanity-check the onnx filename.
        return !string.IsNullOrEmpty(OnnxFileName);
    }

    //  PRIVATE HELPERS - only this class can use this
    private static int DeriveObsSize(TrainingMode d)
    {
        switch (d) // check multiple possible values
        {
            case TrainingMode.Medium: return 19;
            case TrainingMode.Hard: return 24;
            default: return 13;   // Easy
        }
    }

    private static string DeriveOnnxFileName(MatchMode m, TrainingMode d)
    {
        string mode = m == MatchMode.OneVOne ? "1v1" : "2v2";
        string difficulty;
        switch (d)
        {
            case TrainingMode.Medium: difficulty = "medium"; break;
            case TrainingMode.Hard: difficulty = "hard"; break;
            default: difficulty = "easy"; break;
        }
        return $"{mode}_{difficulty}.onnx";
    }
}