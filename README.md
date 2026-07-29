# Robo Garage Uprising

Physical robot soccer: real robots on a real arena, driven by an ML-Agents-trained brain,
coordinated by a Python vision/control runtime, visualized in Unity.

## Structure

| Folder | What it is |
|---|---|
| [`Unity/`](Unity/README.md) | Unity 6 project — main menu, live-match HUD, and the ML-Agents training arena. |
| [`ai_robot/`](ai_robot/README.md) | Python runtime — camera vision, ArUco tracking, brain inference, motor commands over UDP to the ESP32s. |

See each folder's own README for details.

## How the pieces fit together

1. **Training** happens in Unity's `TrainingArena` scene using ML-Agents — the `RobotAgent`
   learns to play against a scripted opponent, producing a `.onnx` brain per difficulty/mode.
2. **Match day**: the operator picks difficulty (Easy/Medium/Hard) and mode (1v1/2v2) in the
   Unity `MainMenu`, which loads `GameScene`.
3. `GameScene` is a **visualizer only** — the ball, goals, and robots are physical, on the real
   arena floor. `ai_robot/brain_runner.py` owns the webcam, tracks everything via ArUco markers,
   runs the matching `.onnx` brain, and sends motor commands to the AI robot(s) over UDP.
   Unity displays the annotated camera feed and the score/timer HUD, driven by UDP events from
   Python.

## Repo housekeeping

- `Unity/Library/`, `Logs/`, `UserSettings/` are Unity-regenerated caches — never commit them
  (see `Unity/.gitignore`).
- `ai_robot/__pycache__/` is Python bytecode cache — don't commit it either.
- See `PROJECT_CLEANUP_PLAN.md` at the repo root for the reasoning behind the current structure
  (what was renamed/deleted and why) if you're wondering where something went.
