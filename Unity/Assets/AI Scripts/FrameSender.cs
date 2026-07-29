using UnityEngine;
using System.Net.Sockets;
using System.Net;
using System;

/// <summary>
/// FrameSender — match control sender (START / STOP to brain_runner.py).
///
/// HISTORY: this component used to capture WebCamTexture frames and stream them
/// to Python. That path is gone — Python now OWNS the physical webcam and
/// produces the processed display feed itself (see CameraControl.cs). All that
/// remains, and all this component does now, is send the two control messages
/// on controlPort (4213):
///
///   "START:<difficulty>:<match>"   e.g. "START:easy:1v1", "START:hard:2v2"
///                                  brain_runner.py parses this and
///                                  auto-configures difficulty + match mode.
///   "STOP"                         stop the robot / end the match.
///
/// difficulty = GameSettings.DifficultyLabel.ToLower()  → "easy" | "medium" | "hard"
/// match      = GameSettings.MatchLabel.ToLower()        → "1v1"  | "2v2"
/// </summary>
public class FrameSender : MonoBehaviour
{
    [Header("Must match brain_runner.py / config.py")]
    [SerializeField] private int controlPort = 4213;
    [SerializeField] private string pythonIP = "127.0.0.1";

    private UdpClient _controlSock;
    private IPEndPoint _controlEP;
    private bool _running = false;

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Open the control socket. Call once before SendStart().</summary>
    public void Init()
    {
        _controlSock = new UdpClient();
        _controlEP = new IPEndPoint(IPAddress.Parse(pythonIP), controlPort);
    }

    /// <summary>
    /// Send the player's menu selection to Python so it auto-configures itself.
    /// Message format:  "START:<difficulty>:<match>"
    /// </summary>
    public void SendStart()
    {
        _running = true;

        string diff = GameSettings.DifficultyLabel.ToLower();   // "easy" | "medium" | "hard"
        string match = GameSettings.MatchLabel.ToLower();       // "1v1"  | "2v2"
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

    // ── Cleanup ───────────────────────────────────────────────────────────────

    private void OnDestroy()
    {
        // Ensure Python gets a STOP even if the user did not press Stop.
        if (_running) SendStop();
        _controlSock?.Close();
    }
}