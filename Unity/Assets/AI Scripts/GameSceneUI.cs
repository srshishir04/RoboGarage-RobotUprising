using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// GameSceneUI — HUD + camera feed + Python event router for the live (physical) match.
///
/// RESPONSIBILITIES
///   • Camera feed (RawImage) + FrameSender lifecycle (START/STOP to Python).
///   • Connection dot — green while Python heartbeats arrive, red otherwise.
///   • Kickoff trigger — first heartbeat calls LiveMatchManager.BeginMatch().
///   • HUD display: mode/difficulty label, scoreboard, clock, half, kickoff/respawn text.
///   • Flash feedback (goal / pickup / wall / tackle).
///   • Halftime banner + match-end overlay.
///   • Routes Python goal events into LiveMatchManager.RegisterGoal(...).
///
/// NOT RESPONSIBLE FOR
///   • Owning the score or the clock — LiveMatchManager owns both.
///   • Robot control / goal detection — brain_runner.py owns both.
///
/// THREADING
///   The UDP socket runs on a background thread. It writes only into a thread-safe
///   queue and a long timestamp; all Unity API calls happen on the main thread.
///
/// PHASE 4 NOTE
///   HandleUdpEvent currently parses events with a tolerant contains-check. The robust
///   JSON parse + full Python->Unity protocol hardening lands in Phase 4. The score
///   ROUTING below (RegisterGoal) is final and correct.
/// </summary>
[RequireComponent(typeof(LiveMatchManager))]
public class GameSceneUI : MonoBehaviour
{
    // ── Top bar ────────────────────────────────────────────────────────────────
    [Header("Top bar")]
    [SerializeField] private TextMeshProUGUI modeText;          // "1v1 · EASY"
    [SerializeField] private TextMeshProUGUI scoreText;         // "AI  0 — 0  Human"
    [SerializeField] private TextMeshProUGUI scoreAIText;       // optional separate label
    [SerializeField] private TextMeshProUGUI scoreHumanText;    // optional separate label
    [SerializeField] private TextMeshProUGUI halfText;          // "1st" / "2nd"
    [SerializeField] private TextMeshProUGUI timerText;         // "03:00"
    [SerializeField] private Image connectionDot;     // green/red

    // ── Camera ──────────────────────────────────────────────────────────────────
    [Header("Camera")]
    [SerializeField] private RawImage cameraView;
    [Tooltip("Camera device index. 0 = first device listed in the Console at startup.")]
    [SerializeField] private int cameraDeviceIndex = 0;
    [SerializeField] private int cameraWidth = 960;
    [SerializeField] private int cameraHeight = 960;
    [SerializeField] private int cameraFps = 30;

    // ── Bottom bar ────────────────────────────────────────────────────────────────
    [Header("Bottom bar")]
    [SerializeField] private TextMeshProUGUI statusText;
    [SerializeField] private TextMeshProUGUI respawnText;       // "Kickoff in 3..." (optional)

    // ── Flash feedback ──────────────────────────────────────────────────────────
    [Header("Flash feedback")]
    [SerializeField] private CanvasGroup flashPanel;
    [SerializeField] private TextMeshProUGUI flashText;

    // ── Halftime banner ───────────────────────────────────────────────────────────
    [Header("Halftime banner")]
    [SerializeField] private CanvasGroup halftimeBanner;
    [SerializeField] private TextMeshProUGUI halftimeText;

    // ── Match end overlay ─────────────────────────────────────────────────────────
    [Header("Match end overlay")]
    [SerializeField] private CanvasGroup matchEndPanel;
    [SerializeField] private TextMeshProUGUI winnerText;
    [SerializeField] private TextMeshProUGUI finalScoreText;

    // ── UDP ────────────────────────────────────────────────────────────────────────
    [Header("UDP — Python → Unity events")]
    [Tooltip("Must match config.py UNITY_EVENT_PORT (default 4211).")]
    [SerializeField] private int eventPort = 4211;

    // ── Colours / constants ─────────────────────────────────────────────────────
    private static readonly Color DOT_GREEN = new Color(0.20f, 0.85f, 0.30f);
    private static readonly Color DOT_RED = new Color(0.90f, 0.20f, 0.20f);
    private static readonly Color C_GOAL = new Color(0.20f, 0.85f, 0.30f);
    private static readonly Color C_PICKUP = new Color(1.00f, 0.85f, 0.10f);
    private static readonly Color C_WALL = new Color(0.90f, 0.30f, 0.10f);
    private static readonly Color C_TACKLE = new Color(0.60f, 0.20f, 0.90f);
    private static readonly Color C_CONCEDE = new Color(0.90f, 0.20f, 0.20f);

    private const float CONNECTION_TIMEOUT = 3f;
    private const float FLASH_FADE_SPEED = 1.5f;
    private const float HALFTIME_FADE_SPEED = 2.0f;
    private const float HALFTIME_HOLD = 3.0f;

    // ── Runtime state ─────────────────────────────────────────────────────────────
    private LiveMatchManager match;

    private int scoreAI = 0;
    private int scoreHuman = 0;
    private float halfSecondsRemaining = 0f;
    private int currentHalf = 1;

    private bool running = true;

    // Camera
    private WebCamTexture webcamTexture;
    private FrameSender frameSender;

    // UDP (background thread → main thread)
    private UdpClient udpClient;
    private Thread udpThread;
    private readonly ConcurrentQueue<string> eventQueue = new ConcurrentQueue<string>();

    // Connection watchdog — Stopwatch ticks are thread-safe to read; Time.time is NOT off-thread.
    private long lastPacketTicks = 0;
    private bool pythonConnected = false;
    private bool kickoffTriggered = false;

    // =========================================================================
    //  LIFECYCLE
    // =========================================================================
    private void Awake()
    {
        match = GetComponent<LiveMatchManager>();
    }

    private void Start()
    {
        LiveMatchManager.OnScoreChanged += HandleScoreChanged;
        LiveMatchManager.OnMatchEnd += HandleMatchEnd;
        LiveMatchManager.OnHalftime += HandleHalftime;
        LiveMatchManager.OnClockTick += HandleClockTick;
        LiveMatchManager.OnKickoffCountdown += HandleKickoffCountdown;

        ApplyModeLabel();
        UpdateScoreDisplay();
        halfSecondsRemaining = GameSettings.MatchDurationSeconds;
        UpdateTimerDisplay();

        if (halftimeBanner != null) { halftimeBanner.alpha = 0f; halftimeBanner.blocksRaycasts = false; }
        if (matchEndPanel != null) { matchEndPanel.alpha = 0f; matchEndPanel.blocksRaycasts = false; }
        if (flashPanel != null) flashPanel.alpha = 0f;
        if (respawnText != null) respawnText.text = "";
        if (connectionDot != null) connectionDot.color = DOT_RED;
        if (statusText != null) statusText.text = "Waiting for brain...";

        StartCamera();
        StartUdpListener();
    }

    private void Update()
    {
        if (!running) return;
        DrainEventQueue();
        UpdateConnectionDot();
        FadeFlashPanel();
    }

    private void OnDestroy()
    {
        LiveMatchManager.OnScoreChanged -= HandleScoreChanged;
        LiveMatchManager.OnMatchEnd -= HandleMatchEnd;
        LiveMatchManager.OnHalftime -= HandleHalftime;
        LiveMatchManager.OnClockTick -= HandleClockTick;
        LiveMatchManager.OnKickoffCountdown -= HandleKickoffCountdown;

        ShutdownNetworkingAndCamera();
    }

    // =========================================================================
    //  CAMERA
    //  NOTE: Python (brain_runner.py) now OWNS the physical webcam directly so it
    //  can control hardware exposure/gain. Unity no longer opens WebCamTexture —
    //  doing so would BLOCK Python from opening the same USB device. The live feed
    //  arrives from Python via CameraControl.cs (UDP display port) and is blitted
    //  onto `cameraView` there. Here we only kick off the match (START message).
    // =========================================================================
    private void StartCamera()
    {
        // No local capture. Just tell Python to start the match using the menu
        // selections. Python opens the camera; CameraControl shows the feed.
        StartCoroutine(StartAfterShortDelay());
    }

    private IEnumerator StartAfterShortDelay()
    {
        if (statusText != null) statusText.text = "Connecting to brain...";

        // Find / create the control sender (kept as FrameSender for the START/STOP
        // control messages — it no longer sends frames, just START/STOP on 4213).
        frameSender = GetComponent<FrameSender>();
        if (frameSender == null) frameSender = gameObject.AddComponent<FrameSender>();
        frameSender.Init(null);   // no WebCamTexture needed anymore

        // Small delay so CameraControl's display socket is bound first.
        yield return new WaitForSeconds(0.3f);

        Debug.Log("[GameSceneUI] Sending START to Python (Python owns the camera).");
        if (statusText != null) statusText.text = "Waiting for brain...";
        frameSender.SendStart();   // "START:<difficulty>:<mode>" from GameSettings
    }

    // =========================================================================
    //  UDP LISTENER (background thread)
    // =========================================================================
    private void StartUdpListener()
    {
        try
        {
            udpClient = new UdpClient(eventPort);
            udpThread = new Thread(UdpLoop) { IsBackground = true, Name = "GameSceneUDP" };
            udpThread.Start();
            Debug.Log($"[GameSceneUI] UDP listener on port {eventPort}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[GameSceneUI] UDP listener failed to start: {e.Message}");
        }
    }

    private void UdpLoop()
    {
        IPEndPoint ep = new IPEndPoint(IPAddress.Any, 0);
        while (running)
        {
            try
            {
                byte[] data = udpClient.Receive(ref ep);     // blocks; throws when socket closed
                Interlocked.Exchange(ref lastPacketTicks, DateTime.UtcNow.Ticks);
                eventQueue.Enqueue(Encoding.UTF8.GetString(data));
            }
            catch
            {
                // Socket closed during shutdown — expected. Exit if we're stopping.
                if (!running) break;
            }
        }
    }

    private void DrainEventQueue()
    {
        while (eventQueue.TryDequeue(out string json))
            HandleUdpEvent(json);
    }

    // =========================================================================
    //  PYTHON EVENT HANDLER
    //  PHASE 4: replace the tolerant contains-check with a real JSON parse and
    //  finalise the full event protocol. Score ROUTING below is already final.
    // =========================================================================
    private void HandleUdpEvent(string json)
    {
        if (string.IsNullOrEmpty(json)) return;

        // Tolerant match: strip spaces so '"event": "x"' and '"event":"x"' both work.
        string compact = json.Replace(" ", "");

        // First heartbeat starts the match clock (kickoff sequence).
        if (compact.Contains("\"event\":\"heartbeat\""))
        {
            TriggerKickoffOnce();
            return;
        }

        if (compact.Contains("\"event\":\"pickup\"")) { ShowFlash("Ball picked up! 🤖", C_PICKUP); return; }
        if (compact.Contains("\"event\":\"wall\"")) { ShowFlash("Wall hit!", C_WALL); return; }
        if (compact.Contains("\"event\":\"tackle\"")) { ShowFlash("Tackle! 💥", C_TACKLE); return; }

        // Goals → route to the single score owner. Check the more specific tags first.
        if (compact.Contains("\"event\":\"score_a\"")) { match.RegisterGoal("AI"); return; }
        if (compact.Contains("\"event\":\"score_b\"")) { match.RegisterGoal("Human"); return; }
        if (compact.Contains("\"event\":\"score\"")) { match.RegisterGoal("AI"); return; } // legacy 1v1
    }

    private void TriggerKickoffOnce()
    {
        if (kickoffTriggered) return;
        kickoffTriggered = true;
        match.BeginMatch();
    }

    // =========================================================================
    //  LiveMatchManager EVENT HANDLERS (main thread)
    // =========================================================================
    private void HandleScoreChanged(int ai, int human, string scoringTeam)
    {
        scoreAI = ai; scoreHuman = human;
        UpdateScoreDisplay();
        ShowFlash(scoringTeam == "AI" ? "GOAL! ⚽" : "They scored! 😤",
                  scoringTeam == "AI" ? C_GOAL : C_CONCEDE);
    }

    private void HandleMatchEnd(int ai, int human)
    {
        scoreAI = ai; scoreHuman = human;
        UpdateScoreDisplay();
        ShowMatchEndOverlay(ai, human);
    }

    private void HandleHalftime()
    {
        currentHalf = 2;
        UpdateTimerDisplay();
        StartCoroutine(ShowHalftimeBanner());
    }

    private void HandleClockTick(float secondsRemaining)
    {
        halfSecondsRemaining = secondsRemaining;
        UpdateTimerDisplay();
    }

    private void HandleKickoffCountdown(float secondsRemaining)
    {
        if (respawnText == null) return;
        int secs = Mathf.CeilToInt(secondsRemaining);
        respawnText.text = secs > 0 ? $"Kickoff in {secs}..." : "";
        if (secs <= 0 && statusText != null) statusText.text = $"Brain connected  [{GameSettings.DifficultyLabel}]";
    }

    // =========================================================================
    //  DISPLAY
    // =========================================================================
    private void ApplyModeLabel()
    {
        if (modeText == null) return;
        modeText.text = $"{GameSettings.MatchLabel}  ·  {GameSettings.DifficultyLabel.ToUpper()}";
        switch (GameSettings.Difficulty)
        {
            case GameSettings.TrainingMode.Easy: modeText.color = new Color(0.23f, 0.80f, 0.35f); break;
            case GameSettings.TrainingMode.Hard: modeText.color = new Color(0.82f, 0.23f, 0.23f); break;
            default: modeText.color = new Color(0.86f, 0.67f, 0.16f); break;
        }
    }

    private void UpdateScoreDisplay()
    {
        if (scoreText != null) scoreText.text = $"AI  {scoreAI} — {scoreHuman}  Human";
        if (scoreAIText != null) scoreAIText.text = scoreAI.ToString();
        if (scoreHumanText != null) scoreHumanText.text = scoreHuman.ToString();
    }

    private void UpdateTimerDisplay()
    {
        if (halfText != null) halfText.text = currentHalf == 1 ? "1st" : "2nd";
        if (timerText != null)
        {
            int mins = (int)(halfSecondsRemaining / 60f);
            int secs = (int)(halfSecondsRemaining % 60f);
            timerText.text = $"{mins:00}:{secs:00}";
        }
    }

    private void UpdateConnectionDot()
    {
        if (connectionDot == null) return;

        long ticks = Interlocked.Read(ref lastPacketTicks);
        bool connected = ticks != 0 &&
                         (DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc)).TotalSeconds < CONNECTION_TIMEOUT;

        if (connected == pythonConnected) return;   // only act on change
        pythonConnected = connected;
        connectionDot.color = connected ? DOT_GREEN : DOT_RED;
        statusText.text = "Brain connected [" + GameSettings.DifficultyLabel + "]";

        if (statusText != null && !connected)
            statusText.text = "Brain disconnected — waiting...";
    }

    // =========================================================================
    //  FLASH / HALFTIME / MATCH-END
    // =========================================================================
    private void ShowFlash(string message, Color color)
    {
        if (flashText != null) { flashText.text = message; flashText.color = color; }
        if (flashPanel != null) flashPanel.alpha = 1f;
    }

    private void FadeFlashPanel()
    {
        if (flashPanel == null || flashPanel.alpha <= 0f) return;
        flashPanel.alpha = Mathf.MoveTowards(flashPanel.alpha, 0f, Time.deltaTime * FLASH_FADE_SPEED);
    }

    private IEnumerator ShowHalftimeBanner()
    {
        if (halftimeBanner == null) yield break;
        if (halftimeText != null) halftimeText.text = "HALF TIME";
        halftimeBanner.blocksRaycasts = true;
        yield return FadeCanvasGroup(halftimeBanner, 0f, 1f, 1f / HALFTIME_FADE_SPEED);
        yield return new WaitForSeconds(HALFTIME_HOLD);
        yield return FadeCanvasGroup(halftimeBanner, 1f, 0f, 1f / HALFTIME_FADE_SPEED);
        halftimeBanner.blocksRaycasts = false;
    }

    private void ShowMatchEndOverlay(int ai, int human)
    {
        if (matchEndPanel == null) return;

        if (winnerText != null)
        {
            if (ai > human) { winnerText.text = "AI WINS! 🤖"; winnerText.color = new Color(0.20f, 0.85f, 0.30f); }
            else if (human > ai) { winnerText.text = "HUMAN WINS! 🏆"; winnerText.color = new Color(0.86f, 0.67f, 0.16f); }
            else { winnerText.text = "DRAW!"; winnerText.color = Color.white; }
        }
        if (finalScoreText != null) finalScoreText.text = $"{ai}  —  {human}";

        StartCoroutine(FadeCanvasGroup(matchEndPanel, 0f, 1f, 0.5f));
        matchEndPanel.blocksRaycasts = true;
    }

    // =========================================================================
    //  BUTTONS
    // =========================================================================
    /// <summary>onClick → StopButton (HUD) and onClick → BtnReturnToMenu (overlay).</summary>
    public void OnStopClicked() => ReturnToMenu();
    public void OnReturnToMenuClicked() => ReturnToMenu();

    private void ReturnToMenu()
    {
        frameSender?.SendStop();      // tell Python to stop the robot
        GameSettings.Reset();
        ShutdownNetworkingAndCamera();
        SceneManager.LoadScene("MainMenu");
    }

    // =========================================================================
    //  SHUTDOWN / UTIL
    // =========================================================================
    private void ShutdownNetworkingAndCamera()
    {
        running = false;

        // Closing the socket unblocks UdpClient.Receive so the thread exits cleanly.
        try { udpClient?.Close(); } catch { }
        try { if (udpThread != null && udpThread.IsAlive) udpThread.Join(200); } catch { }
        udpClient = null;
        udpThread = null;

        if (webcamTexture != null && webcamTexture.isPlaying) webcamTexture.Stop();
        // (webcamTexture is normally null now — Python owns the camera. Guard kept
        //  harmless in case an older build path created one.)
    }

    private static IEnumerator FadeCanvasGroup(CanvasGroup cg, float from, float to, float duration)
    {
        float elapsed = 0f;
        cg.alpha = from;
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            cg.alpha = Mathf.Lerp(from, to, elapsed / duration);
            yield return null;
        }
        cg.alpha = to;
    }
}