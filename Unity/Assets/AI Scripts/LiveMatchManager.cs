using System;
using System.Collections;
using UnityEngine;

/// <summary>
/// LiveMatchManager — the match authority for the PHYSICAL GameScene.
///
/// GameScene is a visualizer only: the ball, goals, AI robot and human robot are all
/// real, on the arena floor, tracked by the overhead camera. brain_runner.py is the
/// goal SENSOR (it detects scoring from the camera and reports it). This component is
/// the single SCORE + CLOCK authority. It owns nothing physical and has no Unity ball,
/// goal collider, or RobotAgent references.
///
/// GAME MODEL (simplified — no periods, no half-time, no auto-end):
///   scene loads          -> clock frozen, "Waiting for brain..."
///   first heartbeat      -> GameSceneUI calls BeginMatch()
///   BeginMatch           -> "Kickoff in 3..2..1" -> clock starts counting UP
///   continuous play      -> clock counts up indefinitely; goals score via
///                           RegisterGoal; scoring/conceding does a soft kickoff
///                           reset on the arena side (handled in brain_runner /
///                           RobotAgent), NOT here.
///   Stop button          -> GameSceneUI returns to the menu; the match just ends.
///
/// There is deliberately NO first/second half, NO half-time break, and NO
/// full-time whistle. The match runs until the operator stops it.
///
/// Scoring: GameSceneUI forwards Python goal events to RegisterGoal("AI"|"Human").
/// </summary>
public class LiveMatchManager : MonoBehaviour
{
    // ── Events (GameSceneUI subscribes) ──────────────────────────────────────
    public static event Action<int, int, string> OnScoreChanged;   // aiScore, humanScore, scoringTeam
    public static event Action<float> OnClockTick;                 // elapsed seconds (counts up)
    public static event Action<float> OnKickoffCountdown;          // seconds remaining (pre-kickoff)

    // ── Inspector (timing only — nothing physical) ───────────────────────────
    [Header("Timing")]
    [Tooltip("Countdown shown before kickoff (seconds).")]
    [SerializeField] private float kickoffCountdownSeconds = 3f;

    // ── State ─────────────────────────────────────────────────────────────────
    private int scoreAI = 0;
    private int scoreHuman = 0;
    private float elapsed = 0f;
    private bool clockRunning = false;   // true once kickoff finishes
    private bool matchStarted = false;   // BeginMatch() called once

    // ── Public accessors (read by GameSceneUI) ───────────────────────────────
    public int ScoreAI => scoreAI;
    public int ScoreHuman => scoreHuman;
    public float ElapsedSeconds => elapsed;
    public bool ClockRunning => clockRunning;
    public bool MatchStarted => matchStarted;

    private void OnDestroy()
    {
        // Clear static events so a scene reload can't double-fire stale subscribers.
        OnScoreChanged = null;
        OnClockTick = null;
        OnKickoffCountdown = null;
    }

    private void Update()
    {
        if (!clockRunning) return;
        elapsed += Time.deltaTime;
        OnClockTick?.Invoke(elapsed);
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
        yield return StartCoroutine(KickoffCountdown());
        Debug.Log("[LiveMatch] Kickoff — play started.");
        elapsed = 0f;
        clockRunning = true;
        // No end condition: the clock counts up until the scene is torn down.
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
    /// Goals only count once play has started (after kickoff).
    /// </summary>
    public void RegisterGoal(string scoringTeam)
    {
        if (!clockRunning)
        {
            Debug.Log($"[LiveMatch] Goal '{scoringTeam}' ignored — play not started.");
            return;
        }

        if (scoringTeam == "AI") scoreAI++;
        else if (scoringTeam == "Human") scoreHuman++;
        else { Debug.LogWarning($"[LiveMatch] Unknown scoringTeam '{scoringTeam}'."); return; }

        Debug.Log($"[LiveMatch] {scoringTeam} scores! AI {scoreAI} — {scoreHuman} Human");
        OnScoreChanged?.Invoke(scoreAI, scoreHuman, scoringTeam);
    }
}