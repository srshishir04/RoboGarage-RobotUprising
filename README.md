# Robo Garage Uprising

Physical robot soccer: real robots on a real arena, driven by an ML-Agents-trained brain,
coordinated by a Python vision/control runtime, visualized in Unity.

## Structure

| Path | What it is |
|---|---|
| [`Unity/`](Unity/README.md) | Unity 6 project — main menu, live-match HUD, and the ML-Agents training arena. Start here to modify the game or train a new brain. |
| [`ai_robot/`](ai_robot/README.md) | Python runtime — camera vision, ArUco tracking, brain inference, motor commands over UDP to the ESP32s. Start here to run an actual physical match. |
| `RoboGarageUprising.zip` | The shipped Windows build. Stored via Git LFS — see below. |

See each folder's own README for details — this page is just the map and the two ways to run
the project.

## Option A — just see it work (no Unity, no Python, no robots needed)

Extract `RoboGarageUprising.zip` anywhere and run the `.exe` inside — it needs its sibling
`_Data` folder and a couple of DLLs to be in the same directory (that's why it's a zip and not
a single `.exe` — a Unity build is never truly one file). This gets you the menu and the match
HUD, but **not a real match** — with no `ai_robot/core/brain_runner.py` running, there's no camera
feed, no brain inference, and no physical robots to control. It's for looking at the UI/HUD,
not playing.

## Option B — run the full live system (what you need to actually operate the robots)

1. Open `ai_robot/esp32_wifi_brain/esp32_wifi_brain.ino` and set `WIFI_SSID`/`WIFI_PASSWORD` to
   your own network (placeholders by default — no real credentials ship in this repo), then
   flash it to each robot's ESP32 and note the IP each one prints over serial.
2. Set those IPs in `ai_robot/core/config.py` (`ROBOT1_IP`/`ROBOT2_IP` — also placeholders by
   default), and confirm the physics contract constants there match
   `Unity/Assets/Scripts/Agent/RobotAgent.cs`.
3. Run `python ai_robot/core/brain_runner.py` — this owns the webcam and starts tracking/inference
   threads (see `ai_robot/README.md` for the full architecture).
4. Open `Unity/` in the Unity Editor (or run the extracted `RoboGarageUprising.zip` build) and
   go through
   `MainMenu` → pick difficulty/mode → `GameScene`. Picking a mode sends `START:<difficulty>:
   <mode>` to `brain_runner.py`, which is what actually starts inference and match control.

`GameScene` is a **visualizer only** — the ball, goals, and robots are physical, on the real
arena floor, not simulated in Unity. Unity displays the annotated camera feed and the
score/timer HUD, both driven by UDP events from Python.

## Training a new brain

Happens in `Unity/Assets/Scenes/TrainingArena.unity` via ML-Agents — see
[`Unity/README.md`](Unity/README.md#4-i-want-to-train-a-new-brain) for the exact command and
how to wire a freshly-trained brain back into the game.

## Repo housekeeping

- `Unity/Library/`, `Logs/`, `UserSettings/` are Unity-regenerated caches — never commit them
  (see `Unity/.gitignore`).
- `__pycache__/` folders under `ai_robot/core/` and `ai_robot/tools/` are Python bytecode
  cache — gitignored (matched at any depth by `ai_robot/.gitignore`), don't commit them.
- `RoboGarageUprising.zip` is tracked via **Git LFS**, not plain git — the onnx brain files
  alone had already bloated `.git` significantly before this was set up; a repeatedly-rebuilt
  70MB+ archive under plain git would make that much worse. Make sure `git-lfs` is installed
  before cloning (`git lfs install`), or the zip will check out as a tiny placeholder pointer
  file instead of the real archive.
- `Unity/CHANGES.md` documents the most significant recent change (the RobotAgent
  egocentric-observation redesign) in detail, including what's *not yet* finished — read it,
  and `Unity/README.md`'s "Known limitations" section, before assuming the AI/training side is
  fully wired end-to-end.
