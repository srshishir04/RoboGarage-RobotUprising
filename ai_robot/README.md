# ai_robot/ — Robo Garage Uprising

Python runtime for the physical match: owns the webcam, tracks robots/ball via ArUco markers,
runs the trained brain, and drives the ESP32-controlled robots over UDP.

## Core

| File | Role |
|---|---|
| `brain_runner.py` | Main runtime — 6 daemon threads: frame receive, control receive, ESP32 status, ArUco detection, ball detection, brain inference. |
| `config.py` | Single source of truth for network ports, robot IPs, ArUco IDs, the physics contract (must match `RobotAgent.cs`), and brain file paths. |
| `camera_settings.json` | Persisted camera exposure/gain, editable live from Unity's `CameraControl.cs` sliders. |

## Brain files (`brains/`)

Named `<mode>_<difficulty>.onnx`:

```
brains/1v1_easy.onnx    brains/2v2_easy.onnx
brains/1v1_medium.onnx  brains/2v2_medium.onnx
brains/1v1_hard.onnx    brains/2v2_hard.onnx
```

`config.py`'s `ONNX_PATHS` is keyed by mode then difficulty; `brain_runner.py` looks up
`ONNX_PATHS[mode][difficulty]` from the `START:<difficulty>:<mode>` message Unity sends at
match start. (Previously `ONNX_PATHS` only had a difficulty axis, so selecting 2v2 in the Unity
menu silently loaded the 1v1 brain — fixed as part of the repo cleanup.)

## Dev utilities

| File | Purpose |
|---|---|
| `aruco_generator.py` | One-off ArUco marker image generator. |
| `camera_probe.py` | One-time webcam calibration helper — run once per camera/computer. |
| `udp_motor_test.py` | Isolation test for ESP32 motor commands, independent of the brain/camera. |
| `esp32_wifi_brain/esp32_wifi_brain.ino` | ESP32 firmware — receives motor commands over UDP. |

## Housekeeping

`__pycache__/` is gitignored — don't commit Python bytecode cache. Debug snapshots written by
the ArUco/ball detectors at runtime are not tracked in git; they're regenerated on demand.
