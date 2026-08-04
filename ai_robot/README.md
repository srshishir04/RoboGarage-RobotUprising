# ai_robot/ — Robo Garage Uprising

Python runtime for the physical match: owns the webcam, tracks robots/ball via ArUco markers,
runs the trained brain, and drives the ESP32-controlled robots over UDP. Unity is a
*visualizer* for this system, not the other way around — `GameScene.unity` has no simulated
ball/robot physics of its own; everything physical is real, on the real arena, and this file is
what's actually running the match.

## Core

| File | Role |
|---|---|
| `brain_runner.py` | Main runtime — see "Thread architecture" below. |
| `config.py` | Single source of truth for network ports, robot IPs, ArUco IDs, the physics contract (must match `RobotAgent.cs`), and brain file paths. Sectioned 1-10 (Network, Robots, Arena, Homography, Models, Vision, Physics contract, Brain timing, Safety, Debug) — read the section header comments before changing anything. |
| `camera_settings.json` | Persisted camera exposure/gain, editable live from Unity's `CameraControl.cs` sliders (or by hand). |

## Thread architecture (`brain_runner.py`, started in `if __name__ == "__main__"`)

Six daemon threads, all reading/writing a handful of lock-protected shared state:

| Thread | Function | Job |
|---|---|---|
| Camera | `camera_capture_fn` | Owns the one physical webcam via OpenCV; keeps `latest_frame` fresh. |
| CtrlRx | `control_receiver_fn` | Listens on `UNITY_CTRL_PORT` (4213) for `START:<difficulty>:<mode>` / `STOP` / `CAMERA:...` / `CAMERA_SAVE` from Unity. |
| Esp32Rx | `esp32_status_fn` | Listens on `ESP32_STATUS_PORT` (4214) for the robots' UDP status beacons. |
| ArUco | `aruco_thread_fn` | Detects corner markers on the latest frame, computes/locks the bird's-eye homography. |
| Balls | `ball_thread_fn` | HSV-based ball detection on the warped frame. |
| Brain | `brain_thread_fn` | Waits for `START`, loads the matching `.onnx` (see below), builds observations, runs inference, sends motor commands to the ESP32(s). |

The main thread just idles (`while not stop_requested: time.sleep(0.5)`) until Ctrl+C, then
signals shutdown and closes every socket cleanly.

## Running it

**Standalone (no Unity, no real robots)** — useful for checking the camera/ArUco/ball pipeline
in isolation: `python brain_runner.py` and watch the console. It starts all 6 threads
regardless; without a `START` message from Unity, the Brain thread just waits forever, but
Camera/ArUco/Balls run immediately against whatever webcam is at `config.CAMERA_INDEX`. Useful
dev tools for this stage: `camera_probe.py` (one-time exposure/gain calibration) and
`udp_motor_test.py` (raw motor-command isolation test, run with `brain_runner.py` **stopped** —
both bind `ESP32_STATUS_PORT`/4214, only one process at a time).

**Full system:** run `brain_runner.py`, then open `MainMenu.unity` in Unity (or the built game)
and pick a difficulty/mode. `MainMenuUI.cs` → `FrameSender.cs` sends `START:<difficulty>:<mode>`
on port 4213, which is what actually starts inference — everything before that is just the
camera/tracking pipeline warming up.

## Brain files (`brains/`)

Named `<mode>_<difficulty>.onnx`:

```
brains/1v1_easy.onnx    brains/2v2_easy.onnx
brains/1v1_medium.onnx  brains/2v2_medium.onnx
brains/1v1_hard.onnx    brains/2v2_hard.onnx
```

`config.py`'s `ONNX_PATHS` is keyed by mode then difficulty; `brain_runner.py` looks up
`ONNX_PATHS[mode][difficulty]` from the `START` message. `Brain.__init__` cross-checks the
`.onnx`'s actual input tensor size against the expected obs size and raises immediately on a
mismatch — so a wrong/untrained brain fails loudly at match start, not mid-match.

To wire in a freshly-trained brain: rename it to match the convention above and drop it in this
folder, replacing the old file. No code changes needed for 1v1. **For 2v2, see the gap below.**

## 2v2 observations — fixed, verified against real brains

`Unity/CHANGES.md` documents a `RobotAgent.cs` redesign: every relative observation is now
expressed in the robot's own facing frame (forward-component, right-component, distance)
instead of raw world axes, and 2v2 uses a fixed 26-float team contract
(`CollectTeamObservations`) with its own self-velocity/yaw-rate slots. This runtime now
implements that contract for 2v2:

- `build_team_observations()` mirrors `CollectTeamObservations`'s exact 26-slot layout —
  own pos, own fwd-speed/yaw-rate, ball (egocentric) + velocity, scoring goal, own goal,
  in-control, teammate (egocentric), am-I-nearest-the-ball, opponent1 + opponent2 (egocentric,
  fixed order — never sorted by distance).
- `_ego_project()` does the actual forward/right rotation, mirroring `RobotAgent.cs`'s
  `EgoRel()`/`EgoVec()` — every value was hand-verified against the Unity-side formula with a
  synthetic observation before trusting it against a real brain.
- Own forward-speed/yaw-rate (config's new `ROBOT_VEL_SCALE`/`YAW_RATE_SCALE`, matching
  `RobotAgent.cs`'s `robotVelScale`/`yawRateScale`) come from frame-to-frame differencing of the
  robot's own ArUco-tracked center + heading in `_run_match` — the same technique already used
  for `ball_vel_ms`, just applied to the robot itself.
- `EXPECTED_OBS`/`obs_size` lookup is mode-aware (`TEAM_OBS_SIZE = 26` for `mode == "2v2"`,
  regardless of difficulty).

**Verified, not just written:** loaded all three 2v2 brains (`2v2_easy/medium/hard.onnx`) with
`onnxruntime` and ran real inference against a `build_team_observations()` output — all three
load and return valid actions with no shape mismatch. The 1v1 path (`build_observations()`) is
untouched and still returns correct-shaped 13/19/24-float vectors.

## Known gap: 1v1 still uses the old (non-egocentric) observation format

`build_observations()` (1v1 only) was **not** touched by the above — it still computes
plain world-axis-aligned relative vectors and raw heading in slots `[2-3]`, not the
egocentric-rotation + self-velocity contract `RobotAgent.cs` now uses. Since the 1v1 brains
were retrained under the new contract too, this is the same class of bug as 2v2 had, scoped down
to 1v1 — it won't crash (obs sizes 13/19/24 are unchanged) but silently feeds the wrong format.
If you want this closed out the same way, the fix is the same shape: rotate `rel()`'s output via
`_ego_project()` and replace the raw-heading `[2-3]` slots with self fwd-speed/yaw-rate (the
per-robot motion tracking added for 2v2 in `_run_match` can be reused for the single 1v1 robot).

## ESP32 firmware

`esp32_wifi_brain/esp32_wifi_brain.ino` — receives single-byte motor commands (`F`/`L`/`R`/`B`/
`S`) from `brain_runner.py` over UDP port 4210, drives the robot with in-place tank turns
matching how the policy trained, and sends a status beacon back on port 4214. Flash it, note
the IP it prints over serial, and put that IP in `config.py`'s `ROBOT1_IP`/`ROBOT2_IP`. If a
robot turns the wrong way, there is exactly **one** knob to flip — see the firmware's own header
comment (`SWAP_MOTORS`) before touching anything else; editing both the firmware and
`config.py`'s `ACTION_TO_CMD` mapping at once cancels out and leaves you back where you started.

## Dev utilities

| File | Purpose |
|---|---|
| `aruco_generator.py` | One-off ArUco marker image generator. |
| `camera_probe.py` | One-time webcam calibration helper — run once per camera/computer. |
| `udp_motor_test.py` | Isolation test for ESP32 motor commands, independent of the brain/camera. |

## Housekeeping

`__pycache__/` is gitignored (`.gitignore`) — don't commit Python bytecode cache. Debug
snapshots written by the ArUco/ball detectors at runtime aren't tracked in git either; they
regenerate on demand.
