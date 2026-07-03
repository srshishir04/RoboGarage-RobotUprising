using System;
using System.Collections;
using UnityEngine;

/// <summary>
/// LiveMatchManager — the match authority for the PHYSICAL GameScene.
///
/// GameScene is a visualizer only: the ball, goals, AI robot and human robot are all
/// real, on the arena floor, tracked by the overhead camera. brain_runner.py is the
/// goal SENSOR (it detects scoring from the camera and reports it). This component is
/// the single CLOCK + SCORE authority. It owns nothing physical and has no Unity ball,
/// goal collider, or RobotAgent references — so there are no unused inspector fields.
///
/// Match flow (game feel):
///   scene loads -> clock frozen, "Waiting for brain..."  (BrainConnected == false)
///   first heartbeat -> GameSceneUI calls BeginMatch()
///   BeginMatch -> "Kickoff in 3..2..1" -> first half clock runs
///   half ends -> halftime hold -> second half
///   full time -> OnMatchEnd
///
/// Scoring: GameSceneUI forwards Python goal events to RegisterGoal("AI"|"Human").
/// </summary>
public class LiveMatchManager : MonoBehaviour
{
    // ── Events (GameSceneUI subscribes) ──────────────────────────────────────
    public static event Action<int, int, string> OnScoreChanged;  // aiScore, humanScore, scoringTeam
    public static event Action<int, int> OnMatchEnd;      // aiScore, humanScore
    public static event Action OnHalftime;
    public static event Action<float> OnClockTick;        // seconds remaining
    public static event Action<float> OnKickoffCountdown; // seconds remaining (pre-half)

    // ── Inspector (timing only — nothing physical) ───────────────────────────
    [Header("Timing")]
    [Tooltip("Length of each half in seconds. Defaults from GameSettings.MatchDurationSeconds.")]
    [SerializeField] private float halfDurationSeconds = 180f;

    [Tooltip("Countdown shown before each half starts (seconds).")]
    [SerializeField] private float kickoffCountdownSeconds = 3f;

    [Tooltip("Pause at halftime before the second half (seconds).")]
    [SerializeField] private float halftimeDelaySeconds = 5f;

    // ── State ─────────────────────────────────────────────────────────────────
    private int scoreAI = 0;
    private int scoreHuman = 0;
    private int half = 1;
    private float halfTimer = 0f;
    private bool matchActive = false;   // true only while a half clock is running
    private bool matchStarted = false;  // BeginMatch() called once
    private bool matchEnded = false;

    // ── Public accessors (read by GameSceneUI) ───────────────────────────────
    public int ScoreAI => scoreAI;
    public int ScoreHuman => scoreHuman;
    public int CurrentHalf => half;
    public float HalfTimeRemaining => Mathf.Max(halfTimer, 0f);
    public bool MatchActive => matchActive;
    public bool MatchStarted => matchStarted;

    private void Awake()
    {
        // Pull duration from the menu selection.
        halfDurationSeconds = GameSettings.MatchDurationSeconds;
    }

    private void OnDestroy()
    {
        // Clear static events so a scene reload can't double-fire stale subscribers.
        OnScoreChanged = null;
        OnMatchEnd = null;
        OnHalftime = null;
        OnClockTick = null;
        OnKickoffCountdown = null;
    }

    private void Update()
    {
        if (!matchActive) return;

        halfTimer -= Time.deltaTime;
        OnClockTick?.Invoke(Mathf.Max(halfTimer, 0f));

        if (halfTimer <= 0f)
            matchActive = false;   // RunMatch coroutine advances the phase
    }

    /// <summary>
    /// Called ONCE by GameSceneUI when the first Python heartbeat arrives.
    /// Idempotent — extra calls are ignored.
    /// </summary>
    public void BeginMatch()
    {
        if (matchStarted) return;
        matchStarted = true;
        StartCoroutine(RunMatch());
    }

    private IEnumerator RunMatch()
    {
        // ── First half ────────────────────────────────────────────────────────
        half = 1;
        yield return StartCoroutine(KickoffCountdown());
        Debug.Log("[LiveMatch] First half.");
        halfTimer = halfDurationSeconds;
        matchActive = true;
        yield return new WaitUntil(() => !matchActive);

        // ── Halftime ────────────────────────────────────────────────────────────
        Debug.Log("[LiveMatch] Halftime.");
        OnHalftime?.Invoke();
        yield return new WaitForSeconds(halftimeDelaySeconds);

        // ── Second half ──────────────────────────────────────────────────────────
        half = 2;
        yield return StartCoroutine(KickoffCountdown());
        Debug.Log("[LiveMatch] Second half.");
        halfTimer = halfDurationSeconds;
        matchActive = true;
        yield return new WaitUntil(() => !matchActive);

        // ── Full time ──────────────────────────────────────────────────────────
        matchEnded = true;
        Debug.Log($"[LiveMatch] Full time. AI {scoreAI} — {scoreHuman} Human");
        OnMatchEnd?.Invoke(scoreAI, scoreHuman);
    }

    private IEnumerator KickoffCountdown()
    {
        float t = kickoffCountdownSeconds;
        while (t > 0f)
        {
            OnKickoffCountdown?.Invoke(t);
            t -= Time.deltaTime;
            yield return null;
        }
        OnKickoffCountdown?.Invoke(0f);
    }

    /// <summary>
    /// The ONLY scoring path in GameScene. brain_runner.py detects a goal from the
    /// camera; GameSceneUI forwards it here. Single owner = no double counting.
    /// Goals only count while a half clock is running.
    /// </summary>
    public void RegisterGoal(string scoringTeam)
    {
        if (!matchActive)
        {
            Debug.Log($"[LiveMatch] Goal '{scoringTeam}' ignored — clock not running.");
            return;
        }

        if (scoringTeam == "AI") scoreAI++;
        else if (scoringTeam == "Human") scoreHuman++;
        else { Debug.LogWarning($"[LiveMatch] Unknown scoringTeam '{scoringTeam}'."); return; }

        Debug.Log($"[LiveMatch] {scoringTeam} scores! AI {scoreAI} — {scoreHuman} Human");
        OnScoreChanged?.Invoke(scoreAI, scoreHuman, scoringTeam);
    }
}