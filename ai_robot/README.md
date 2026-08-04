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

## ⚠️ Known gap: this runtime doesn't match the current (egocentric) observation contract

`Unity/CHANGES.md` documents a RobotAgent.cs redesign — every relative observation (ball, goal,
opponent, teammate) is now expressed in the robot's own facing frame (forward/right/distance)
instead of raw world axes, and slots `[2-3]` now carry self-velocity + yaw-rate instead of raw
heading. **This file was never updated to match.** Concretely, four things still need doing
(none done yet):

1. **`build_observations()` must rotate every relative vector into the robot's frame.**
   Currently `rel()` computes a plain world-axis-aligned `(dx, dz)` offset. It needs the same
   rotation `RobotAgent.cs`'s `EgoRel()`/`EgoVec()` do: `(dx*sinθ + dz*cosθ, dx*cosθ - dz*sinθ)`
   where `θ` is the robot's heading angle — giving (forward-component, right-component).
   Applies to the ball, goal, and own-goal `rel()` calls, and to the opponent heading/velocity
   terms.
2. **Robot self-velocity tracking is new and doesn't exist yet.** Obs slots `[2-3]` need
   forward-speed and yaw-rate (normalized by `robotVelScale = 0.60` and `yawRateScale = 3.2`
   respectively — these live in `RobotAgent.cs`, not `config.py`, and aren't defined here at
   all yet). The file already differences the *ball's* position frame-to-frame for
   `ball_vel_ms` — the robot's own ArUco-tracked center/heading needs the identical treatment.
3. **`EXPECTED_OBS` must become mode-aware.** It's currently `{"easy": 13, "medium": 19, "hard":
   24}`, and the call site (`obs_size = EXPECTED_OBS.get(difficulty, 13)`) ignores `mode`
   entirely. For `mode == "2v2"`, `obs_size` must be `26` regardless of difficulty — matching
   `GameSettings.ObsSize` on the Unity side.
4. **`build_team_observations()` doesn't exist.** There is currently no 2v2-shaped observation
   builder at all. It needs to mirror `RobotAgent.cs`'s `CollectTeamObservations`'s exact
   26-slot order (including teammate + fixed opponent1/opponent2 ordering).

**Practical effect:** loading a 2v2 brain fails immediately and loudly (`Brain.__init__`'s size
check catches the 13/19/24-vs-26 mismatch). **1v1 loads and runs without crashing** — sizes are
unchanged — **but silently feeds the wrong observation format** to a brain trained on the new
egocentric contract, since nothing here checks *meaning*, only *size*. If real-robot behavior
looks subtly wrong (turns the "obvious" wrong way in some headings, etc.) after the brains were
retrained, this is almost certainly why.

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
