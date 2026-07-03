using UnityEngine;
using System.Net.Sockets;
using System.Net;
using System;

/// <summary>
/// FrameSender — production-clean version.
///
/// Fixes applied vs original:
///   FIX A  _snap Texture2D is re-created if the webcam resolution changes after
///          Init() is called, preventing an ArgumentException crash when the
///          camera changes size on the first real frame.
///   FIX B  Null-guard on _cam before calling GetPixels() so that if Init() is
///          called before webcamTexture.Play() resolves, we don't crash.
///   FIX C  Socket exceptions on Send are logged as warnings, not swallowed, so
///          network errors are visible in the Unity console.
///   FIX D  OnDestroy calls SendStop() first (if running) so Python always
///          receives a clean shutdown signal when the scene is destroyed, even if
///          the user does not press the Stop button explicitly.
///   FIX E  SendStart() now encodes difficulty + match mode from GameSettings
///          into the message: "START:easy:1v1" (or hard:2v2 etc.)
///          brain_runner.py reads this and auto-configures itself — no CLI args
///          or manual config edits needed.
/// </summary>
public class FrameSender : MonoBehaviour
{
    [Header("Must match brain_runner.py / config.py")]
    [SerializeField] private int framePort = 4212;
    [SerializeField] private int controlPort = 4213;
    [SerializeField] private string pythonIP = "127.0.0.1";

    [Header("Quality vs bandwidth")]
    [SerializeField] private int jpegQuality = 60;   // 40–70 is fine for 960×960
    [SerializeField] private int sendEveryNth = 3;   // 1-in-3 frames ≈ 10 fps

    private UdpClient _frameSock;
    private UdpClient _controlSock;
    private IPEndPoint _frameEP;
    private IPEndPoint _controlEP;
    private WebCamTexture _cam;
    private Texture2D _snap;
    private int _frameCount = 0;
    private bool _running = false;

    // ── Public API ────────────────────────────────────────────────────────────

    public void Init(WebCamTexture cam)
    {
        // cam may be null now: Python owns the camera, so Unity does not capture.
        // This component is kept ONLY as the START/STOP control sender on 4213.
        _cam = cam;
        _frameSock = new UdpClient();
        _controlSock = new UdpClient();
        _frameEP = new IPEndPoint(IPAddress.Parse(pythonIP), framePort);
        _controlEP = new IPEndPoint(IPAddress.Parse(pythonIP), controlPort);

        if (cam != null)
        {
            // Legacy path (Unity capturing) — normally unused now.
            _snap = new Texture2D(
                Mathf.Max(cam.width, 1),
                Mathf.Max(cam.height, 1),
                TextureFormat.RGB24, false);
        }
    }

    /// <summary>
    /// Called by GameSceneUI once the camera has a real frame.
    ///
    /// FIX E: Encodes the player's menu selection (difficulty + match mode) into
    /// the START message so brain_runner.py auto-configures itself.
    ///
    /// Message format:  "START:<difficulty>:<match>"
    /// Examples:        "START:easy:1v1"
    ///                  "START:hard:2v2"
    ///
    /// difficulty = GameSettings.DifficultyLabel.ToLower()  → "easy" | "medium" | "hard"
    /// match      = GameSettings.MatchLabel.ToLower()        → "1v1"  | "2v2"
    /// </summary>
    public void SendStart()
    {
        _running = true;

        // Build settings-encoded payload — brain_runner.py parses this
        string diff = GameSettings.DifficultyLabel.ToLower();   // "easy" | "medium" | "hard"
        string match = GameSettings.MatchLabel.ToLower();        // "1v1"  | "2v2"
        string payload = $"START:{diff}:{match}";

        byte[] msg = System.Text.Encoding.UTF8.GetBytes(payload);
        try
        {
            _controlSock.Send(msg, msg.Length, _controlEP);
            Debug.Log($"[FrameSender] Sent to Python: {payload}");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[FrameSender] Failed to send START: {e.Message}");
        }
    }

    /// <summary>Called by GameSceneUI when the stop button is pressed.</summary>
    public void SendStop()
    {
        _running = false;
        byte[] msg = System.Text.Encoding.UTF8.GetBytes("STOP");
        try
        {
            _controlSock.Send(msg, msg.Length, _controlEP);
            Debug.Log("[FrameSender] Sent STOP to Python.");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[FrameSender] Failed to send STOP: {e.Message}");
        }
    }

    // ── Update — capture and send frames ─────────────────────────────────────

    private void Update()
    {
        // DISABLED: Python now owns the camera and produces the feed. Unity does
        // not capture or send frames. START/STOP still go out via SendStart/SendStop.
        // (Guard also prevents running if no WebCamTexture was supplied.)
        if (_cam == null) return;

        if (!_running || !_cam.isPlaying) return;

        _frameCount++;
        if (_frameCount % sendEveryNth != 0) return;

        // FIX A: rebuild snapshot texture if camera resolution has changed
        if (_snap == null || _snap.width != _cam.width || _snap.height != _cam.height)
        {
            if (_snap != null) Destroy(_snap);
            _snap = new Texture2D(_cam.width, _cam.height, TextureFormat.RGB24, false);
        }

        _snap.SetPixels(_cam.GetPixels());
        _snap.Apply();

        byte[] jpg = _snap.EncodeToJPG(jpegQuality);

        // UDP safe payload ≈ 65 000 bytes.
        // At 60 % quality, 960×960 ≈ 40–60 kB — well within the limit.
        // If you ever exceed 65 000 B, reduce jpegQuality or resolution.
        try
        {
            _frameSock.Send(jpg, jpg.Length, _frameEP);
        }
        catch (Exception e)
        {
            // FIX C: log instead of silently swallowing
            Debug.LogWarning($"[FrameSender] Frame send failed: {e.Message}");
        }
    }

    // ── Cleanup ───────────────────────────────────────────────────────────────

    private void OnDestroy()
    {
        // FIX D: ensure Python gets a STOP even if the user did not press Stop
        if (_running) SendStop();

        _frameSock?.Close();
        _controlSock?.Close();
    }
}