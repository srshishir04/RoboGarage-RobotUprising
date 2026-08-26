# Unity/ — Robo Garage Uprising

This is the handoff guide for whoever continues this project next. It assumes general Unity
familiarity but zero context on this specific game — every section below tells you exactly
which file to open for a given task.

## 1. Opening the project

- **Editor version: Unity `6000.3.8f1`** (Unity 6). Install via Unity Hub — opening with a
  different major version may fail to load or silently corrupt serialized scene/prefab data.
- Key packages (`Packages/manifest.json`): `com.unity.ml-agents 4.0.2`, `com.unity.ai.navigation`,
  `com.unity.timeline`, `com.unity.ugui`. Unity's Package Manager will resolve these on first
  open — no manual install needed for the Unity side.
- For training, you also need the Python `mlagents` package installed and matched to the Unity
  package version above. **The matching pip version isn't pinned anywhere in this repo** — check
  the [ML-Agents releases page](https://github.com/Unity-Technologies/ml-agents/releases) for
  the Python package that corresponds to Unity package `4.0.2` before running `mlagents-learn`.
  A step-by-step walkthrough of this setup is in
  [`docs/Setup & installation guide.pdf`](docs/Setup%20%26%20installation%20guide.pdf).

## 2. Project structure — what's in `Assets/` and why

| Folder | Contents | Plain-language purpose |
|---|---|---|
| `Scenes/` | `MainMenu.unity`, `GameScene.unity`, `TrainingArena.unity` | Menu → live-match visualizer → ML-Agents training arena. |
| `Scripts/Agent/` | `RobotAgent.cs`, `ScriptedOpponent.cs` | The learning agent and its rule-based training opponent. |
| `Scripts/Core/` | `GameSettings.cs`, `LiveMatchManager.cs` | Difficulty/mode selection + derived values (obs size, brain filename); match clock/score authority. |
| `Scripts/Camera/` | `CameraControl.cs`, `FrameSender.cs` | Receive the live camera feed from Python; send match START/STOP + camera exposure/gain settings to Python. |
| `Scripts/UI/` | `GameSceneUI.cs`, `MainMenuUI.cs` | Top-level HUD and main-menu controllers — wire the `UI/Components/` pieces together and talk to `GameSettings`/`LiveMatchManager`. |
| `Scripts/UI/Components/` | 16 small reusable scripts (buttons, sliders, score pod, timer, status dot, segmented selectors, popover, modal, resize controllers) | The actual visual/interactive UI building blocks. See §3 for how to restyle them. |
| `Editor/UIBuilder/` | 11 Editor-only tools | Code that *constructed* the UI (see §3) — not part of the shipped game, only runs inside the Editor via `Tools/RoboGarage UI/...` menu items. |
| `Prefabs/` | `Environment.prefab`, `OpponentRobot.prefab`, `Prefabs/UI/*` | Training-arena content; the 10 UI component prefabs. |
| `config/` | `Ra_easy.yaml`, `Ra_medium.yaml`, `Ra_hard.yaml`, `Ra_2v2.yaml` | ML-Agents trainer configs — see §4. |
| `Materials/`, `Meshes/`, `Images/`, `Textures/UI/`, `Fonts/`, `TextMesh Pro/` | Supporting assets | Arena visuals, ArUco corner-marker materials, UI sprites/fonts. |
| `CHANGES.md` | — | Detailed log of the RobotAgent egocentric-observation redesign — read this if anything about observations/rewards looks unfamiliar. |

**Not in Unity at all:** the trained `.onnx` brain files. Those live in `ai_robot/brains/` on
the Python side, not under `Assets/`. `Assets/AI Model/` (a stale single `RobotAgent.onnx`
placeholder) was removed in this cleanup pass since it didn't match any real brain and wasn't
referenced anywhere.

## 3. "I want to change the UI design"

The UI is component-based and palette-driven, not made of one-off hand-tweaked colors.

- **The palette:** `Scripts/UI/Components/Colors.cs` — a static class with named `Color`
  constants (Surfaces, Accent, Text, Status, Dividers/sliders, Glow/scrim), each parsed from a
  hex string. Every UI script reads from `Colors.X` instead of hardcoding a hex value.
- **Worked example — change the PLAY button's color:** the Play button
  (`Scripts/UI/MainMenuUI.cs`'s `btnPlay`) is an *instance* of the `PrimaryButton` prefab
  (`Prefabs/UI/PrimaryButton.prefab`, script `ArcadeButton.cs`) — `Editor/UIBuilder/
  RoboGarageUIBuilder_Phase3.cs`'s `BuildActionRow` just instantiates it, it doesn't set colors
  itself. The color is actually baked into the prefab by `Editor/UIBuilder/
  RoboGarageUIBuilder_Phase2.cs`'s `BuildPrimaryButton()` (~line 125:
  `btn.colors = MakeColors(Colors.GarageOrange, Colors.GarageOrangeHover,
  Colors.GarageOrangePressed, ...)`). **Edit `GarageOrange`/`GarageOrangeHover`/
  `GarageOrangePressed` in `Colors.cs` (lines 32-34)** — note this is the general accent color,
  also used for several other elements (difficulty-chip text, robot-count digits, retry label),
  not just Play — then either:
  - Re-run the builder (`Tools/RoboGarage UI/...` menu, the relevant Phase item) to regenerate
    the prefab/scene instances from scratch, or
  - Manually re-tint the existing `Button` component's `ColorBlock` in the Inspector — the
    `Colors.cs` constants only drive the *builder tool*, not a live runtime binding, so already-
    built scene objects don't update automatically just from editing the constant.
- **Fonts:** body text uses Unity's built-in LiberationSans SDF (Archivo/JetBrains Mono didn't
  render clearly at HUD sizes). The Main Menu title alone still uses `Archivo-Black SDF`
  (`Assets/Fonts/Archivo/Archivo-Black SDF.asset`).
- **The `Editor/UIBuilder/` scripts are re-runnable tools, not one-time throwaway code** — if you
  want to add a new UI component in the same visual style, look at `RoboGarageUIFactory.cs`
  first (shared helpers: `CreateRoundedPanel`, `CreateText`, `CreateImage`, etc.) and
  `RoboGarageSpriteFactory.cs` (generates the rounded-rect/circle/glow sprites Unity's `Image`
  can't draw natively).
- **Caveat if you reorganize `Fonts/`, `Textures/UI/`, `Images/`, `Prefabs/UI/`, or `Scenes/`:**
  the Editor builder scripts reference those by hardcoded string path (e.g.
  `"Assets/Textures/UI"`, `"Assets/Scenes/MainMenu.unity"`), not by GUID — unlike normal
  Inspector references, these do **not** survive a folder move automatically. (Script folders
  were already reorganized safely in this cleanup pass — verified no such hardcoded references
  to `Assets/AI Scripts` existed anywhere.)

## 4. "I want to train a new brain"

**Scene:** `Scenes/TrainingArena.unity` — instantiates `Prefabs/Environment.prefab` (~20+ times,
for parallel training) containing the AI robot (`RobotAgent.cs`) and a scripted opponent
(`Prefabs/OpponentRobot.prefab`, `ScriptedOpponent.cs`).

**Configs (`config/*.yaml`):**

| File | Trainer | Behavior Name | Obs size | Notes |
|---|---|---|---|---|
| `Ra_easy.yaml` | ppo | `RobotAgent` | 13 | No opponent. Uses a uniform sampler (not a lesson curriculum) for spawn radius — an earlier lesson-based curriculum stalled and never reached the full spread needed to teach both turn directions. |
| `Ra_medium.yaml` | ppo | `RobotAgent` | 19 | Adds opponent awareness. Progress-gated `opponent_speed` + `opponent_skill` curricula. |
| `Ra_hard.yaml` | ppo | `RobotAgent` | 24 | Adds own-goal/role/threat awareness. Larger network (512 hidden units); opponent-speed curriculum ceiling was deliberately lowered after the agent's sim speed was recalibrated to match the real robot (1.14 → 0.60 m/s) — the old ceiling made the opponent ~3.5× the agent's speed. |
| `Ra_2v2.yaml` | **poca** (not ppo — required for `SimpleMultiAgentGroup`/`AddGroupReward` multi-agent credit assignment) | `RobotAgentTeam` | 26 | One file covers all three difficulties (difficulty comes from each `ScriptedOpponent`'s Inspector settings). Soft team-kickoff on goals (episode doesn't end). |

**Before training, in the Editor:** confirm each mode's Behavior Parameters component matches
the table above (Behavior Name, Vector Obs Space Size, Discrete Branch 0 = 4 for all). None of
these *values* changed in the last redesign, but it's exactly the kind of setting that silently
produces a garbage policy if it drifts — always re-verify, don't assume.

**⚠️ 2v2 specifically is not fully set up in this repo right now** — see §6, item 1. You'll need
to add a second `RobotAgent` to `Environment.prefab` with `teamMode = true` and the
`teammate`/`opponent`/`opponent2`/`ownGoal` references wired before `Ra_2v2.yaml` will train
anything meaningful.

**Run** (from the repo root):
```
mlagents-learn Unity/config/Ra_easy.yaml --run-id=easy_v2 --force
```
Swap in `Ra_medium.yaml` / `Ra_hard.yaml` / `Ra_2v2.yaml` for the other modes. **Never
`--resume` an old checkpoint onto these configs** — even where obs *sizes* didn't change across
the last redesign, slot *meanings* did (see `CHANGES.md`), so a resumed policy would be actively
misled by stale checkpoint weights.

**Where the result lands:** `mlagents-learn` writes `results/<run-id>/<BehaviorName>.onnx`
(e.g. `results/easy_v2/RobotAgent.onnx`) in whatever directory you ran the command from.

**Wiring a newly-trained brain into the game:**
1. Copy the resulting `.onnx` out of `results/<run-id>/`.
2. Rename it to match the convention `ai_robot/brains/<mode>_<difficulty>.onnx` — e.g.
   `1v1_easy.onnx`, `2v2_hard.onnx` (see `ai_robot/README.md`).
3. Drop it into `ai_robot/brains/`, replacing the old file of the same name.
4. **That's it for 1v1** — `ai_robot/core/config.py`'s `ONNX_PATHS` and `GameSettings.cs`'s
   `DeriveOnnxFileName()` already resolve by this naming convention; no code changes needed.
5. **For 2v2, there's an additional gap** — see §6, item 2. The brain will load, but the
   observations Python sends it won't match what it was trained on.

## 5. "I want to change agent behavior / rewards"

`RobotAgent.cs` (the learning agent) and `ScriptedOpponent.cs` (its non-learning training
partner) are where behavior lives.

**Reward shaping in plain terms** (`RobotAgent.cs`, `OnActionReceived`/`OnActionReceivedTeam`):
the agent gets small continuous nudges every step, not just a reward at the end of an episode:
- **Approach shaping** — rewarded for closing distance to the ball, and separately for turning
  to face it (using the *shorter* rotation direction). Both are "potential-based" (reward = how
  much closer you got this step, not a flat bonus for being close) — this makes them
  policy-invariant, i.e. they can't be gamed by orbiting the ball forever to keep collecting the
  same reward.
- **Progress shaping** — rewarded for pushing the ball closer to the scoring goal once in
  control, with an alignment bonus for approaching the ball from behind (relative to where it
  needs to go), not stopping on top of it.
- **Terminal rewards** — a real goal (`R_SCORE`, with a speed bonus for scoring fast) or
  conceding (`R_CONCEDE`), which also triggers a kickoff reset.
- **Penalties** — going out of bounds, hovering near a wall, spinning in place, standing still
  off-ball, or parking on the ball without pushing it.
- **Medium+ only** — a "steal" bonus for gaining control right after the opponent had it, and a
  penalty for losing control you had.

**If you change any reward constant, you must retrain — it will not affect any existing
`.onnx` file.** The trained weights are a frozen snapshot of the *old* reward landscape; editing
`RobotAgent.cs` after the fact does nothing until you run `mlagents-learn` again.

**`ScriptedOpponent.cs`** blends three behaviors along a continuous `skillLevel` (0-1) driven by
a curriculum parameter, rather than 3 fixed difficulty presets: hesitation chance, ball
interception (chases current ball position at skill 0, leads it up to 0.45s ahead at skill 1),
and aim precision (shot jitter shrinking from ±0.22m to ~0). `opponentDifficulty` still sets a
manual-testing baseline; the YAML curriculum overrides it during training.

## 6. Known limitations (honest, as of this handoff)

1. **2v2 team mode is wired in code but not in the committed Editor scene.** `RobotAgent.cs`
   supports `teamMode`/`teammate`/`opponent2`, and `Ra_2v2.yaml` exists with the correct `poca`
   trainer — but `Environment.prefab` (and every instance of it in `TrainingArena.unity`) still
   has only one, 1v1-configured `RobotAgent` (Behavior Name `RobotAgent`, not `RobotAgentTeam`).
   The current `ai_robot/brains/2v2_*.onnx` files were trained via local Editor changes that
   were never committed back to this repo. To retrain 2v2, you'll need to redo this wiring
   yourself (add a second AI robot, set `teamMode = true`, wire `teammate`/`opponent2`/`ownGoal`
   on both, set Behavior Name to `RobotAgentTeam` on both).
2. ~~`ai_robot/brain_runner.py`'s 1v1 path used the old (non-egocentric) observation format.~~
   **Fixed and verified**, same rigor as the 2v2 fix: hand-checked a synthetic observation
   against manual trigonometry (exact match, including the opponent-facing-vs-position
   distinction), then confirmed empirically — before the fix, a heading-invariance sweep and a
   multi-step dynamic simulation showed all three 1v1 brains spinning in place indefinitely from
   most starting headings (zero net progress toward the ball); after the fix, all three
   converge to the ball consistently regardless of starting heading. See `ai_robot/README.md`
   for the full before/after evidence.
   **Separately, 2v2 shows a close-range stall** (oscillates just outside control distance,
   persists over 200 simulated steps, unaffected by adding ball-push physics to the test) that
   testing alone can't attribute to a tunable threshold vs. a genuine policy limitation vs. this
   test harness's approximated (uncalibrated) turn rate — needs a live Unity/hardware test.
3. **Turn-torque calibration is an open TODO** (`RobotAgent.cs`, `motorTorque`/`turnTorque`
   fields, ~line 116-124): the real robot's turn rate has not been re-measured at the firmware's
   current `TURN_SPEED=200` — the last real measurement was at `TURN_SPEED=220`. Don't change
   `turnTorque` without a fresh measurement; the code comment explains why torque isn't even the
   right lever past the WheelCollider's slip-dominated turning regime.
4. ~~2v2 "steal" detection only tracked `opponent1`.~~ **Fixed** — `OnActionReceivedTeam` now
   uses `AnyOpponentInControl()`, which checks both `opponent` and `opponent2`. This also makes
   the opponent1/opponent2 marker-ID assignment order harmless (see `ai_robot/README.md`'s note
   on that). Reward-shaping only, no observation/inference change — doesn't affect the current
   six brains, only future retraining.
5. **The main-menu "N robots connected" pill is built but not wired in.** `ConnectionPillUI.cs`
   and its prefab were deleted in this cleanup pass — they were unreferenced anywhere in the
   scene, and `MainMenuUI.cs`'s own comment confirms there's no pre-match connection check
   implemented yet. If you want this feature, you'll be building it from scratch, not
   reconnecting something that already half-works.
6. **The in-Editor "brain file ready" warning always shows "not found."** `MainMenuUI.cs` checks
   `Application.streamingAssetsPath` for the selected `.onnx`, but no `Assets/StreamingAssets/`
   folder exists in this project — the real brain files live in `ai_robot/brains/`, which Unity
   never looks at. Purely cosmetic; doesn't affect the real Python-side loading at all.
