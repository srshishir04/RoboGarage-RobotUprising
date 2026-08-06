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

## Observations — egocentric contract implemented for both 1v1 and 2v2

`Unity/CHANGES.md` documents a `RobotAgent.cs` redesign: every relative observation is now
expressed in the robot's own facing frame (forward-component, right-component, distance)
instead of raw world axes, self forward-speed/yaw-rate replace raw heading, and 2v2 uses its own
fixed 26-float team contract (`CollectTeamObservations`). This runtime now implements that
contract on both paths:

- `build_team_observations()` (2v2) and `build_observations()` (1v1) both mirror their Unity
  counterparts slot-for-slot via a shared `_ego_project()` rotation helper (mirrors
  `RobotAgent.cs`'s `EgoRel()`/`EgoVec()`).
- **1v1's opponent-facing slots (Medium+) needed a second, distinct fix**, not just the same
  rotation applied twice: `RobotAgent.cs` projects the *opponent's own heading* onto *my* frame
  (`EgoVec(opponent.forward)`), not the opponent's raw world heading — a direction-vector
  rotation, not a position-relative one. Confirmed via full field-by-field comparison against
  `CollectObservations()`, not assumed from the size match alone.
- Own forward-speed/yaw-rate (config's `ROBOT_VEL_SCALE`/`YAW_RATE_SCALE`, matching
  `RobotAgent.cs`'s `robotVelScale`/`yawRateScale`) come from frame-to-frame differencing of the
  robot's own ArUco-tracked center + heading in `_run_match` — the same technique already used
  for `ball_vel_ms`, now shared by both 1v1 and 2v2 per-robot loops.
- `EXPECTED_OBS`/`obs_size` lookup is mode-aware (`TEAM_OBS_SIZE = 26` for `mode == "2v2"`,
  regardless of difficulty).

**Verified via real inference, not just code review:** hand-derived a synthetic observation for
each of `build_observations()` and `build_team_observations()` and checked every value against
manual trigonometry — exact match on all 24 and all 26 slots respectively. Then ran a
heading-invariance sweep (identical relative ball geometry at 8 different absolute robot
headings — a correctly-egocentric pipeline must produce the same action regardless of absolute
heading) and a multi-step dynamic simulation (robot actually moves under its own chosen actions
across several ticks) for all six brains. Before the 1v1 fix, all three 1v1 brains span in
place indefinitely from most starting headings, with zero net progress toward the ball — a
direct reproduction of the original heading-generalization bug. After the fix, all three
converge to the ball consistently regardless of starting heading.

**2v2 close-range approach still shows a stall/oscillation just outside control distance** in
this same multi-step testing (persists across 200 simulated steps, with or without a ball-push
physics model) — the observation math itself is verified correct, so this is either a genuine
trained-policy characteristic at that specific range, or an artifact of this test harness's
approximated turn rate (the real turn rate is itself an open calibration TODO in `RobotAgent.cs`
— see `Unity/README.md`'s known limitations). Not resolved from testing alone; would need a
live Unity/hardware test to pin down further.

**Opponent1/opponent2 marker-ID mapping**: `brain_runner.py` maps ArUco marker 3 → opponent1,
marker 4 → opponent2, consistently for both AI robots. 2v2 training happens in pure Unity
simulation, which has no concept of real marker IDs, so there's no training-time "ground truth"
this needs to match — any consistent convention is valid. The only place a mapping-order
asymmetry could have mattered was `RobotAgent.cs`'s `R_STEAL` reward only checking `opponent`
(not `opponent2`) during training — fixed (`AnyOpponentInControl()`, see `Unity/CHANGES.md`) so
future retraining is unaffected by opponent1-vs-opponent2 assignment order. This fix is
reward-shaping only; it doesn't touch the observation vector or inference, so the six current
brains are unaffected either way.

## ESP32 firmware

`esp32_wifi_brain/esp32_wifi_brain.ino` — receives single-byte motor commands (`F`/`L`/`R`/`B`/
`S`) from `brain_runner.py` over UDP port 4210, drives the robot with in-place tank turns
matching how the policy trained, and sends a status beacon back on port 4214.

**⚠️ Required setup before this works — neither of these ships with real values, on purpose:**
1. **WiFi credentials**: open `esp32_wifi_brain.ino` and set `WIFI_SSID`/`WIFI_PASSWORD`
   (currently placeholders — `"YOUR_WIFI_SSID"`/`"YOUR_WIFI_PASSWORD"`) to your own network
   *before* flashing.
2. **Robot IPs**: after flashing, open the Serial Monitor at 115200 baud, note the IP address
   each robot prints once it connects, and set `config.py`'s `ROBOT1_IP`/`ROBOT2_IP` (currently
   `"SET_ME"`) accordingly. These will change if your router reassigns DHCP leases — re-check
   them if a robot stops responding after a network change.

If a robot turns the wrong way, there is exactly **one** knob to flip — see the firmware's own
header comment (`SWAP_MOTORS`) before touching anything else; editing both the firmware and
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
