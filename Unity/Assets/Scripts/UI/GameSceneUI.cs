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
///   • HUD display: mode/difficulty label, scoreboard, count-up clock, kickoff/respawn text.
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
/// UI (spec §4.2): the HUD's visual components changed from raw Text/Image to the shared
/// component set (ScorePodUI, TimerDisplayUI, StatusDot, ChipLabelUI) — every event handler
/// below still computes the exact same values as before, just pushes them through the new
/// components' APIs instead of setting .text/.color directly.
/// </summary>
[RequireComponent(typeof(LiveMatchManager))]
public class GameSceneUI : MonoBehaviour
{
    // ── Top bar ────────────────────────────────────────────────────────────────
    [Header("Top bar")]
    [SerializeField] private ChipLabelUI difficultyChip;         // "EASY" / "MEDIUM" / "HARD"
    [SerializeField] private TextMeshProUGUI modeLabel;          // "1V1" / "2V2"
    [SerializeField] private ScorePodUI scorePod;
    [SerializeField] private TimerDisplayUI timer;

    // ── Camera ──────────────────────────────────────────────────────────────────
    [Header("Camera")]
    [SerializeField] private RawImage cameraView;

    // ── Bottom bar ────────────────────────────────────────────────────────────────
    [Header("Bottom bar")]
    [SerializeField] private StatusDot connectionDot;
    [SerializeField] private TextMeshProUGUI statusText;
    [SerializeField] private TextMeshProUGUI respawnText;       // "Kickoff in 3..." (optional)

    // ── UDP ────────────────────────────────────────────────────────────────────────
    [Header("UDP — Python → Unity events")]
    [Tooltip("Must match config.py UNITY_EVENT_PORT (default 4211).")]
    [SerializeField] private int eventPort = 4211;

    private const float CONNECTION_TIMEOUT = 3f;

    // ── Runtime state ─────────────────────────────────────────────────────────────
    private LiveMatchManager match;

    private int scoreAI = 0;
    private int scoreHuman = 0;
    private float elapsedSeconds = 0f;   // counts up; no period, no auto-end

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
        LiveMatchManager.OnClockTick += HandleClockTick;
        LiveMatchManager.OnKickoffCountdown += HandleKickoffCountdown;

        ApplyModeLabel();
        UpdateScoreDisplay();
        elapsedSeconds = 0f;
        UpdateTimerDisplay();

        if (respawnText != null) respawnText.text = "";
        connectionDot?.SetState(StatusDot.State.Disconnected);
        SetStatus("Waiting for brain...", Colors.TextMuted);

        StartCamera();
        StartUdpListener();
    }

    private void Update()
    {
        if (!running) return;
        DrainEventQueue();
        UpdateConnectionDot();
    }

    private void OnDestroy()
    {
        LiveMatchManager.OnScoreChanged -= HandleScoreChanged;
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
        SetStatus("Connecting to brain...", Colors.StatusAmber);

        // Find / create the control sender (kept as FrameSender for the START/STOP
        // control messages — it no longer sends frames, just START/STOP on 4213).
        frameSender = GetComponent<FrameSender>();
        if (frameSender == null) frameSender = gameObject.AddComponent<FrameSender>();
        frameSender.Init();   // opens the control socket (no camera capture anymore)

        // Small delay so CameraControl's display socket is bound first.
        yield return new WaitForSeconds(0.3f);

        Debug.Log("[GameSceneUI] Sending START to Python (Python owns the camera).");
        SetStatus("Waiting for brain...", Colors.TextMuted);
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
    }

    private void HandleClockTick(float secondsElapsed)
    {
        elapsedSeconds = secondsElapsed;
        UpdateTimerDisplay();
    }

    private void HandleKickoffCountdown(float secondsRemaining)
    {
        if (respawnText == null) return;
        int secs = Mathf.CeilToInt(secondsRemaining);
        respawnText.text = secs > 0 ? $"Kickoff in {secs}..." : "";
        if (secs <= 0) SetStatus("Brain connected", Colors.StatusGreen);
    }

    // =========================================================================
    //  DISPLAY
    // =========================================================================
    private void ApplyModeLabel()
    {
        difficultyChip?.SetText(GameSettings.DifficultyLabel.ToUpper());
        if (modeLabel != null) modeLabel.text = GameSettings.MatchLabel.ToUpper();
    }

    private void UpdateScoreDisplay()
    {
        scorePod?.SetScore(scoreHuman, scoreAI);
    }

    private void UpdateTimerDisplay()
    {
        timer?.SetSeconds(elapsedSeconds);
    }

    private void UpdateConnectionDot()
    {
        if (connectionDot == null) return;

        long ticks = Interlocked.Read(ref lastPacketTicks);
        bool connected = ticks != 0 &&
                         (DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc)).TotalSeconds < CONNECTION_TIMEOUT;

        if (connected == pythonConnected) return;   // only act on change
        pythonConnected = connected;
        connectionDot.SetState(connected ? StatusDot.State.Connected : StatusDot.State.Disconnected);
        if (connected) SetStatus("Brain connected", Colors.StatusGreen);
        else SetStatus("Brain disconnected — waiting...", Colors.StatusRed);
    }

    private void SetStatus(string text, Color color)
    {
        if (statusText == null) return;
        statusText.text = text;
        statusText.color = color;
    }

    // =========================================================================
    //  BUTTONS
    // =========================================================================
    /// <summary>onClick → StopButton (HUD).</summary>
    public void OnStopClicked() => ReturnToMenu();

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
}
