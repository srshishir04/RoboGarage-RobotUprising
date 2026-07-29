# Project Cleanup Plan — Robo Garage Uprising

Status: **DRAFT — nothing has been executed.** No files have been moved, renamed, or deleted.
The only changes made so far are noted in "Already done" below.

---

## 0. Things you need to resolve before I execute anything

These aren't cleanup decisions — they're pre-existing issues in the repo/working tree that
the cleanup plan depends on. Fix these first (or tell me how to proceed).

### 0.1 Already done (git hygiene, no content changes)
- `Unity/Library/`, `Unity/Logs/`, `Unity/UserSettings/` (1.5 GB of Unity-regenerated cache)
  were staged for their first-ever commit, with no `.gitignore` anywhere in the repo. I
  unstaged them (`git restore --staged`) and added `Unity/.gitignore` (copied from the
  `main` branch's own `.gitignore`, which already excludes these correctly). Nothing was
  deleted from disk — this only stops git from tracking regenerated files.

### 0.2 Blocking — unresolved `git stash pop` conflicts (you're resolving these yourself)
`ai_robot/config.py`, `ai_robot/brain_runner.py`, and `ai_robot/camera_settings.json` currently
have literal `<<<<<<< Updated upstream` / `=======` / `>>>>>>> Stashed changes` conflict
markers on disk (plus 3 binary files stuck "both added" in the index: `aruco_debug_frame.png`,
`aruco_debug_gray.png`, `__pycache__/config.cpython-313.pyc`). I did **not** touch these — you
said you'd resolve them yourself. **The KEEP/DELETE recommendations below for `ai_robot/`
assume these are resolved cleanly with no markers left in the file.** Let me know when done so
I can re-check before executing.

### 0.3 Branch consolidation — the Unity source is split across two unmerged branches
This is the biggest finding of the audit and it changes what "essential" means for Unity:

- Your current branch (`Python_and_brain_file`) has **no Unity source at all** — no `Assets/`,
  no `ProjectSettings/`, no `RobotAgent.cs`. Only `Unity/Library`, `Logs`, `UserSettings`
  (generated cache) exist here.
- The Unity project (`Assets/`, `ProjectSettings/`, etc. — 218 tracked paths) lives on `main`.
- **A separate local branch called `Unity` has a commit `789dce6 "final clean unity setup"`
  made 2026-07-30 01:15 — 3 minutes *after* `main`'s current tip, and it has never been merged
  anywhere.** It is not a stray/duplicate — it's a genuine forward improvement over `main`:
  - It implements **halftime + second half + full-time/match-end overlay** in
    `LiveMatchManager.cs` and `GameSceneUI.cs`. `main`'s version explicitly has *no* halftime
    ("GAME MODEL (simplified — no periods, no half-time, no auto-end)") — which contradicts
    the "halftime" requirement you listed for the match HUD. The `Unity` branch is the version
    that actually has it.
  - It fixes real bugs in `FrameSender.cs` (texture-resize crash guard, null-camera guard,
    swallowed socket exceptions, missing STOP-on-destroy) and tunes `CameraControl.cs`
    defaults (`exposureDefault -7→-6`, `gainDefault 25→40`, matching what you resolved in the
    `camera_settings.json`/`config.py` stash conflict).
  - It **already deletes `GUI_TurtleAgent.cs`** (see §2, OLD_PROTOTYPE_OR_JUNK) — the empty
    stub script doesn't exist on this branch at all.
  - The only file `main` has that `Unity` branch doesn't is the empty `GUI_TurtleAgent.cs` (+
    its `.meta`) — i.e. `Unity` branch is a strict superset of intended functionality minus
    that one dead file.

  **Recommendation: treat the `Unity` branch as authoritative, not `main`, for these 7 files.**
  Merge `Unity` → `main` (or fast-forward/replace) before doing anything else, favoring the
  `Unity` branch's content wherever they conflict. The KEEP list in §2 below already reflects
  the `Unity`-branch versions of these files.

  Affected files: `CameraControl.cs`, `FrameSender.cs`, `GameSceneUI.cs`, `GameSettings.cs`,
  `LiveMatchManager.cs`, `Scenes/GameScene.unity`, and the absence of `GUI_TurtleAgent.cs`.

- Once branches are consolidated, `ai_robot/` (already identical between `main` and
  `Python_and_brain_file`) and the resolved Unity source need to end up on the same branch
  together — right now no single branch has both a working `ai_robot/` and the Unity assets.

### 0.4 Confirm Unity editor version
`Unity/ProjectSettings/ProjectVersion.txt` reports `m_EditorVersion: 6000.3.8f1` — that's
**Unity 6**, not Unity 2022 LTS. Please confirm which editor you're actually opening this
project with; if it's genuinely 2022 LTS this file is wrong and the project may not open
correctly.

### 0.5 2v2 brain files are not yet wired into the runtime (functional gap, not a cleanup item)
`ai_robot/config.py`'s `ONNX_PATHS` dict only maps `"easy"/"medium"/"hard"` → the three 1v1
files, and `brain_runner.py` loads via `C.ONNX_PATHS.get(difficulty)` — it never branches on
`match_mode`. On the Unity side, `GameSettings.SetMatchMode()` never touches `OnnxFileName`
either — only `SetDifficulty()` does, and it always derives a 1v1 filename. **So right now,
selecting 2v2 in the menu still loads a 1v1 brain; the `2v2_easy/medium/hard.onnx` files exist
but nothing loads them.** This isn't something the cleanup should silently paper over — it's a
feature gap in `brain_runner.py`/`config.py`/`GameSettings.cs` that needs actual code changes,
independent of any renaming. Flagging it now because the rename in §3 will touch these same
lines anyway.

### 0.6 Before executing: commit or branch first
Per your instructions, before any move/rename/delete happens I will either create a fresh
branch or commit the current (post-conflict-resolution, post-branch-consolidation) state so
everything is reversible. I won't do this until you've resolved §0.2 and told me how you want
§0.3 handled.

---

## 1. `ai_robot/`

### KEEP (as-is)
| Path | Purpose |
|---|---|
| `brain_runner.py` | Main runtime — 6 daemon threads (frame receive, control receive, ESP32 status, ArUco detection, ball detection, brain inference). Pending conflict resolution (§0.2). |
| `config.py` | Central config — camera, network, ArUco IDs, physics contract, ONNX paths. Pending conflict resolution (§0.2). |
| `aruco_generator.py` | One-off ArUco marker image generator (dev utility). |
| `camera_probe.py` | One-time webcam calibration helper (dev utility). |
| `udp_motor_test.py` | Isolation test tool for ESP32 motor commands, independent of the brain (debug utility). |
| `esp32_wifi_brain/esp32_wifi_brain.ino` | ESP32 firmware — receives motor commands over UDP. Essential, matches "ESP32 status" requirement. |
| `camera_settings.json` | Persisted camera exposure/gain settings, written by `CameraControl.cs`'s "save" button. Pending conflict resolution (§0.2). |

### RENAME + MOVE (brain model files, into a `brains/` subfolder with a consistent scheme)
All 6 required combos exist and are the only `.onnx` files ever committed on any branch —
**no old checkpoints or stray duplicates found.** They're just inconsistently named (1v1 files
have no mode prefix; casing differs from the 2v2 files).

| Current path | New path |
|---|---|
| `ai_robot/Easy.onnx` | `ai_robot/brains/1v1_easy.onnx` |
| `ai_robot/Medium.onnx` | `ai_robot/brains/1v1_medium.onnx` |
| `ai_robot/Hard.onnx` | `ai_robot/brains/1v1_hard.onnx` |
| `ai_robot/2v2_easy.onnx` | `ai_robot/brains/2v2_easy.onnx` |
| `ai_robot/2v2_medium.onnx` | `ai_robot/brains/2v2_medium.onnx` |
| `ai_robot/2v2_hard.onnx` | `ai_robot/brains/2v2_hard.onnx` |

**This is not a pure file move** — both sides of the pipeline hardcode the old filenames and
must be updated in the same change:
- `ai_robot/config.py` — `ONNX_PATHS` dict (currently `os.path.join(_HERE, "Easy.onnx")` etc.)
  needs to point at `brains/1v1_easy.onnx` etc., and gain 2v2 entries (see §0.5).
- `Unity/Assets/AI Scripts/GameSettings.cs` — `DeriveOnnxFileName()` / `OnnxFileName` /
  `Reset()` currently hardcode `"Easy.onnx"` etc. and have no match-mode awareness at all
  (§0.5) — this needs the same fix, not just a renamed string.

### DELETE
| Path | Reasoning |
|---|---|
| `ai_robot/__pycache__/` (`config.cpython-313.pyc`) | Python bytecode cache; shouldn't be tracked in git at all. Add `__pycache__/` to a Python `.gitignore` and remove from tracking. |
| `ai_robot/aruco_debug_frame.png`, `ai_robot/aruco_debug_gray.png` | Runtime debug snapshots written by the ArUco detector, not source — regenerate on demand rather than tracking in git. |

### UNCLEAR
- Should the two debug PNGs be kept as reference images (e.g. for documentation) instead of
  deleted? Ask before removing if you use them for anything.
- §0.5 (2v2 brain wiring) — confirm this is a known in-progress gap and whether you want it
  fixed as part of this cleanup pass or tracked separately.

---

## 2. `Unity/` (assuming §0.3 branch consolidation resolved in favor of the `Unity` branch)

### KEEP (as-is)
| Path | Purpose |
|---|---|
| `Assets/AI Scripts/RobotAgent.cs` | ML-Agents `Agent` subclass — 1v1/2v2 RL training + ONNX inference, reward shaping, domain randomization. Attached to `Environment.prefab`, used by `TrainingArena.unity`. |
| `Assets/AI Scripts/ScriptedOpponent.cs` | Scripted (non-RL) training sparring partner. Attached to `OpponentRobot.prefab`. |
| `Assets/AI Scripts/GameSettings.cs` | Central settings singleton — difficulty/mode, obs size, onnx filename, match duration. Needs the §0.5/§1 update. |
| `Assets/AI Scripts/MainMenuUI.cs` | Main menu: difficulty/mode selection, Play-button gating. |
| `Assets/AI Scripts/LiveMatchManager.cs` | Clock/score authority — kickoff, **halftime, second half, full-time** (`Unity`-branch version only, see §0.3). |
| `Assets/AI Scripts/GameSceneUI.cs` | Match HUD — score/timer/half/connection-dot/status, halftime banner, match-end overlay; routes Python UDP goal events. (`Unity`-branch version only.) |
| `Assets/AI Scripts/CameraControl.cs` | Receives JPEG feed from `brain_runner.py` (UDP 4215), sends exposure/gain back (UDP 4213). |
| `Assets/AI Scripts/FrameSender.cs` | Sends `START:<difficulty>:<match>` / `STOP` control messages to Python (UDP 4213). Frame-capture code path is present but dead (guarded by `_cam == null`) — harmless, kept for the control-channel logic. |
| `Assets/Scenes/GameScene.unity` | Live physical-match visualizer (camera feed + HUD). |
| `Assets/Scenes/MainMenu.unity` | Difficulty/mode selection menu. |
| `Assets/Scenes/TrainingArena.unity` | ML-Agents training scene. |
| `Assets/Prefabs/Environment.prefab` | Training arena content — AIRobot, goals, ball, nested OpponentRobot. |
| `Assets/Prefabs/OpponentRobot.prefab` | Scripted-opponent robot instance. |
| `Assets/AI Model/RobotAgent.onnx` | Trained brain used inside the Unity training/inference pipeline. |
| `Assets/Materials/*`, `Assets/Meshes/*`, `Assets/Images/aruco-*.png`, `Assets/TextMesh Pro/*`, `Assets/icon.png`, `Assets/Pictures/robo-garage-grafiikka.png` | Supporting assets actively used by the current scenes/prefabs (arena meshes/materials, ArUco corner markers, standard TMP Essentials import, icon, menu artwork). |
| `Packages/manifest.json`, `Packages/packages-lock.json` | Package manifest, incl. ML-Agents 4.0.2. |
| `ProjectSettings/*` | Standard project settings (see §0.4 re: editor version). |
| `Unity/.gitignore` | Already added (§0.1). |

### RENAME (optional, naming consistency — not required)
| Current | Proposed | Reasoning |
|---|---|---|
| `Assets/AI Scripts/` | `Assets/Scripts/` | Once the legacy `Assets/Scripts/` folder (below) is deleted, there's no more naming collision — "AI Scripts" as a separate category from "Scripts" only made sense while both existed. Purely cosmetic; skip if you'd rather not touch script GUIDs/meta churn. |

### DELETE
Entire legacy simulator stack from an earlier project iteration — confirmed dead by
cross-referencing scene/prefab GUIDs against every script's `.meta` file:

| Path | Superseded by | Reasoning |
|---|---|---|
| `Assets/Scripts/RobotController.cs` | `RobotAgent.cs` | Old hand-driven/scripted simulated-robot controller. **Also currently broken**: `Robot.prefab` has zero MonoBehaviours attached, so `MainController.SpawnRobot()`'s `GetComponent<RobotController>()` would return null. |
| `Assets/Scripts/StreamCameraController.cs`, `Assets/Scripts/VideoServer.cs` | `CameraControl.cs` + `FrameSender.cs` | Old Unity-renders-its-own-camera-and-serves-MJPEG-over-HTTP pipeline. Different transport, different frame source, different consumer than the current physical-camera pipeline. Only used by `MainScene.unity`. |
| `Assets/Scripts/MainController.cs` | `GameSettings.cs` + `GameSceneUI.cs` + `LiveMatchManager.cs` | Old JSON-config-driven simulated-arena spawner. Only used by `MainScene.unity`. |
| `Assets/Scripts/GoalController.cs` | `GameSceneUI.cs` HUD | Old scoreboard using legacy `UnityEngine.UI.Text` (not TMP) — style/API mismatch confirms age. Only used by `MainScene.unity`. |
| `Assets/Scripts/DynamicObjectController.cs`, `Assets/Scripts/Draggable.cs`, `Assets/Scripts/Utils.cs` | — | Supporting code for the dead `MainController`/`RobotController` stack (arena bounce physics, drag/highlight, torque math). Dead once the above are removed. |
| `Assets/Prefabs/Ball.prefab`, `Assets/Prefabs/Cube.prefab`, `Assets/Prefabs/TrafficCone.prefab` | — | Only referenced as `MainController` Inspector fields in `MainScene.unity`; never instanced in any current scene. |
| `Assets/Prefabs/Robot.prefab` | AIRobot object inside `Environment.prefab` | Visual-only mesh with zero scripts attached; meant to pair with the now-dead `RobotController`. |
| `Assets/Prefabs/OrangeBall.prefab` | — | Orphaned — referenced by nothing anywhere in `Unity/` (confirmed via GUID search). |
| `Assets/_Recovery/0.unity`, `0 (1).unity`, `0 (2).unity` | Committed `GameScene.unity` / `MainMenu.unity` | Unity crash-autosave files (standard `_Recovery/N.unity` naming). Content confirms they're stale snapshots predating the current committed scenes. |
| `Assets/AI Scripts/GUI_TurtleAgent.cs` | — | Empty `MonoBehaviour` stub (no fields, no logic), not attached to anything in any scene/prefab. Already removed on the `Unity` branch (§0.3) — nothing to do here once that branch is merged. |

### UNCLEAR
| Item | Question |
|---|---|
| `Assets/Scenes/MainScene.unity` | Delete along with the rest of the legacy stack, or keep as a reference/future "virtual simulation" mode? Unlike the `_Recovery` files, this is a deliberately-built, self-consistent (if legacy) scene, not an accidental duplicate — your call, not a code-derivable answer. |
| ML-Agents training config (`.yaml`) | **Resolved (skipped):** not present in git on any branch, ever. The only local `trainer_config.yaml` found belonged to an unrelated project (`ai-simulator`, behavior name `AIRobotGeneric`, not `RobotAgent`). The real file lives on another machine — owner will add it separately when available. Not blocking the rest of this cleanup. |
| `TrainingArena.unity`'s `"GUI_TurtleAgent"` GameObject | The scene has an empty, scriptless GameObject named after the dead script (coincidence, or leftover placeholder?). Once the script is gone, do you want this GameObject renamed/removed too, or is it a deliberately-named empty anchor? |

---

## Next steps
1. You resolve the `ai_robot/` stash conflicts (§0.2) and tell me.
2. You decide how to consolidate the `Unity`/`main` branch split (§0.3) — I'd recommend I do
   the merge favoring the `Unity` branch, but say the word.
3. Answer the UNCLEAR items above (`MainScene.unity` disposition, missing yaml, 2v2 wiring
   scope, TurtleAgent GameObject, debug PNGs, editor version).
4. I create a safety branch (or commit), then execute the approved KEEP/RENAME/MOVE/DELETE
   actions and update the hardcoded filename references in `config.py` and `GameSettings.cs`.
5. I write `README.md` at the repo root and inside `Unity/` and `ai_robot/` documenting the
   new structure.
