using UnityEngine;
using UnityEngine.UI;
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Collections.Concurrent;

/// <summary>
/// CameraControl � Python owns the physical webcam; Unity displays the feed and
/// drives exposure/gain. Replaces the old Unity-captures / FrameSender pattern.
///
/// Two jobs:
///   1. RECEIVE the processed JPEG feed from brain_runner.py on displayPort (4215)
///      and blit it onto the GameScene camera RawImage. Because the feed comes
///      from the REAL camera, the panel visibly darkens/brightens when the
///      exposure slider moves � that is the operator's live feedback loop.
///   2. SEND slider changes to brain_runner.py on controlPort (4213):
///        "CAMERA:<exposure>:<gain>:<auto 0|1>"   applied live to the webcam
///        "CAMERA_SAVE"                            persist to camera_settings.json
///
/// Wiring in GameScene:
///   � Put this on the same GameObject as GameSceneUI (the "Manager").
///   � Assign cameraView  -> the same RawImage GameSceneUI used for the feed.
///   � Assign exposureSlider, gainSlider, autoToggle, saveButton (see CameraSliderUI).
/// </summary>
public class CameraControl : MonoBehaviour
{
    [Header("Must match config.py")]
    [SerializeField] private string pythonIP = "127.0.0.1";
    [SerializeField] private int displayPort = 4215;   // Python -> Unity feed
    [SerializeField] private int controlPort = 4213;   // Unity  -> Python commands

    [Header("Display")]
    [SerializeField] private RawImage cameraView;          // same panel as before

    [Header("Slider limits (mirror config.py)")]
    [SerializeField] private int exposureMin = -13;
    [SerializeField] private int exposureMax = 0;
    [SerializeField] private int exposureDefault = -7;
    [SerializeField] private int gainMin = 0;
    [SerializeField] private int gainMax = 255;
    [SerializeField] private int gainDefault = 25;

    [Header("UI (optional � assign if you use the built-in slider panel)")]
    [SerializeField] private Slider exposureSlider;
    [SerializeField] private Slider gainSlider;
    [SerializeField] private Toggle autoToggle;
    [SerializeField] private Button saveButton;
    [SerializeField] private TMPro.TextMeshProUGUI exposureLabel;
    [SerializeField] private TMPro.TextMeshProUGUI gainLabel;

    // Send no more than ~15 camera msgs/sec while dragging (avoid flooding 4213).
    private const float SEND_INTERVAL = 0.066f;
    private float _lastSendTime = 0f;
    private bool _pendingSend = false;

    // Networking
    private UdpClient _controlSock;
    private IPEndPoint _controlEP;
    private UdpClient _displaySock;
    private Thread _displayThread;
    private volatile bool _running = false;

    // Latest received frame, handed main-thread side for safe texture upload.
    private readonly ConcurrentQueue<byte[]> _frameQueue = new ConcurrentQueue<byte[]>();
    private Texture2D _displayTex;

    // Current values
    private int _exposure;
    private int _gain;
    private bool _auto;

    // =====================================================================
    private void Start()
    {
        _exposure = exposureDefault;
        _gain = gainDefault;
        _auto = false;

        _controlSock = new UdpClient();
        _controlEP = new IPEndPoint(IPAddress.Parse(pythonIP), controlPort);

        _displayTex = new Texture2D(2, 2, TextureFormat.RGB24, false);
        if (cameraView != null) { cameraView.texture = _displayTex; cameraView.color = Color.white; }

        StartDisplayReceiver();
        SetupSliders();

        // Push the initial values so Python and the webcam start in sync.
        SendCameraSettings(force: true);
    }

    private void Update()
    {
        // Upload the most recent received frame on the main thread (Unity rule).
        byte[] latest = null;
        while (_frameQueue.TryDequeue(out byte[] f)) latest = f;   // keep only newest
        if (latest != null && _displayTex != null)
        {
            _displayTex.LoadImage(latest);   // auto-resizes the texture
        }

        // Debounced slider send.
        if (_pendingSend && Time.time - _lastSendTime >= SEND_INTERVAL)
            SendCameraSettings();
    }

    private void OnDestroy()
    {
        _running = false;
        try { _displaySock?.Close(); } catch { }
        try { if (_displayThread != null && _displayThread.IsAlive) _displayThread.Join(200); } catch { }
        try { _controlSock?.Close(); } catch { }
    }

    // =====================================================================
    //  DISPLAY RECEIVER (background thread)
    // =====================================================================
    private void StartDisplayReceiver()
    {
        try
        {
            _displaySock = new UdpClient(displayPort);
            _running = true;
            _displayThread = new Thread(DisplayLoop) { IsBackground = true, Name = "CamDisplayRx" };
            _displayThread.Start();
            Debug.Log($"[CameraControl] Listening for feed on UDP {displayPort}.");
        }
        catch (Exception e)
        {
            Debug.LogError($"[CameraControl] Could not bind display port {displayPort}: {e.Message}");
        }
    }

    private void DisplayLoop()
    {
        IPEndPoint ep = new IPEndPoint(IPAddress.Any, 0);
        while (_running)
        {
            try
            {
                byte[] data = _displaySock.Receive(ref ep);   // blocks; throws when closed
                if (data != null && data.Length > 0)
                    _frameQueue.Enqueue(data);
            }
            catch (SocketException) { break; }   // socket closed on shutdown
            catch (ObjectDisposedException) { break; }
            catch (Exception e)
            {
                if (_running) Debug.LogWarning($"[CameraControl] Display recv: {e.Message}");
            }
        }
    }

    // =====================================================================
    //  SLIDER WIRING
    // =====================================================================
    private void SetupSliders()
    {
        if (exposureSlider != null)
        {
            exposureSlider.wholeNumbers = true;
            exposureSlider.minValue = exposureMin;
            exposureSlider.maxValue = exposureMax;
            exposureSlider.SetValueWithoutNotify(_exposure);
            exposureSlider.onValueChanged.AddListener(OnExposureChanged);
        }
        if (gainSlider != null)
        {
            gainSlider.wholeNumbers = true;
            gainSlider.minValue = gainMin;
            gainSlider.maxValue = gainMax;
            gainSlider.SetValueWithoutNotify(_gain);
            gainSlider.onValueChanged.AddListener(OnGainChanged);
        }
        if (autoToggle != null)
        {
            autoToggle.SetIsOnWithoutNotify(_auto);
            autoToggle.onValueChanged.AddListener(OnAutoChanged);
        }
        if (saveButton != null)
            saveButton.onClick.AddListener(OnSavePressed);

        RefreshLabels();
    }

    private void OnExposureChanged(float v)
    {
        _exposure = Mathf.RoundToInt(v);
        _pendingSend = true;
        RefreshLabels();
    }

    private void OnGainChanged(float v)
    {
        _gain = Mathf.RoundToInt(v);
        _pendingSend = true;
        RefreshLabels();
    }

    private void OnAutoChanged(bool on)
    {
        _auto = on;
        // When auto is on, the manual sliders have no effect � grey them out.
        if (exposureSlider != null) exposureSlider.interactable = !on;
        if (gainSlider != null) gainSlider.interactable = !on;
        SendCameraSettings(force: true);
    }

    private void OnSavePressed()
    {
        byte[] msg = System.Text.Encoding.UTF8.GetBytes("CAMERA_SAVE");
        try
        {
            _controlSock.Send(msg, msg.Length, _controlEP);
            Debug.Log("[CameraControl] Sent CAMERA_SAVE.");
        }
        catch (Exception e) { Debug.LogWarning($"[CameraControl] Save send failed: {e.Message}"); }
    }

    private void RefreshLabels()
    {
        if (exposureLabel != null) exposureLabel.text = $"Exposure: {_exposure}";
        if (gainLabel != null) gainLabel.text = $"Gain: {_gain}";
    }

    // =====================================================================
    //  SEND
    // =====================================================================
    private void SendCameraSettings(bool force = false)
    {
        if (!force && Time.time - _lastSendTime < SEND_INTERVAL) return;
        _lastSendTime = Time.time;
        _pendingSend = false;

        string payload = $"CAMERA:{_exposure}:{_gain}:{(_auto ? 1 : 0)}";
        byte[] msg = System.Text.Encoding.UTF8.GetBytes(payload);
        try
        {
            _controlSock.Send(msg, msg.Length, _controlEP);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[CameraControl] Camera send failed: {e.Message}");
        }
    }
}