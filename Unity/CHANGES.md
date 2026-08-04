# RobotAgent / ScriptedOpponent redesign — CHANGES.md

This documents the redesign requested after the ball-chasing/2v2 audit. It compares
three states: the **repo baseline** (what was committed — 1v1-only, no 2v2, no
ball-convergence fixes), **your draft** (pasted from your other machine — ball-convergence
fixes + restored 2v2 team mode, never committed here), and **this redesign** (built on your
draft, per your sign-off on the three open questions).

**All 6 brains (1v1 Easy/Medium/Hard, 2v2 Easy/Medium/Hard) need a full retrain. Do not
resume any old checkpoint onto these configs — several observation slot *meanings* changed
even though the *sizes* didn't.**

## 1. The headline fix: egocentric observations

**Diagnosis (confirmed by direct ONNX inference testing, not just code reading):** holding
the ball's position and bearing relative to the robot fixed and only rotating the robot's
*absolute* world heading flipped the forward/turn decision in 4-6 of 8 cases, for every
1v1 difficulty tier. Reading the code confirmed why: every relative vector (ball, goal,
opponent, teammate) was position-relative but expressed in raw world X/Z axes, while heading
was a *separate* raw world vector. The network had to implicitly learn the rotation
relating the two, and never generalized it cleanly across headings.

**Fix:** every relative vector is now `(forward-component, right-component, distance)` —
computed via `Vector3.Dot(worldVec, transform.forward)` / `Vector3.Dot(worldVec,
transform.right)` (`EgoRel()` / `EgoVec()` in `RobotAgent.cs`). This is a rotation, so vector
*length* is unaffected — **Easy/Medium/Hard/2v2 obs sizes are unchanged: 13/19/24/26.**

Applies to: ball position + velocity, scoring goal, own goal, opponent(s), teammate.
**Does not apply to:** the robot's own absolute position (`[0-1]`, legitimate as-is — wall
proximity / field-half awareness isn't a bearing) and ball-to-own-goal distance (a scalar
between two other objects, already frame-independent).

## 2. Self-velocity replaces raw heading (per your sign-off)

Once everything else is egocentric, "my own heading" becomes near content-free (I always
face my own forward axis, by definition). The freed observation slots (`[2-3]` in every
contract) now carry **forward speed + yaw rate** instead — proprioception the policy
previously had none of.

- Sim: `Vector3.Dot(rb.linearVelocity, transform.forward)` and `rb.angularVelocity.y`,
  normalized by new `robotVelScale` (0.60, matching the calibrated real top speed — reusing
  the ball's `velScale` (1.5) would have compressed this into an unreadable sliver of the
  obs range) and `yawRateScale` (3.2 rad/s, ≈180°/s measured top turn rate).
- **Real robot: this is a new Python-side requirement, not automatic — see §6.**

## 3. Action space: unchanged (per your sign-off)

Kept the 4 discrete actions (Stop/Forward/turn-Left-in-place/turn-Right-in-place). I
verified adding a combined forward+turn action would have been a *small* firmware change
(`esp32_wifi_brain.ino` already does independent per-side PWM, one new `case` would do it),
but you chose to defer that — no firmware risk this cycle. The egocentric fix should already
substantially help the "aim, then go" two-phase problem on its own.

## 4. 2v2: soft team-kickoff instead of hard episode-end (per your sign-off)

Your draft ended the whole team's episode (`EndGroupEpisode`) on every goal. Changed to a
soft reset — both AI robots, both opponents, and the ball return to fresh positions
(`TeamRandomPlaceAll()`, already-tested code, just re-invoked mid-episode), the episode keeps
running to `MaxStep`. Same density rationale as 1v1's existing `Kickoff()`.

**Bug found and fixed while wiring this up (not something you asked me to hunt for, but
worth flagging):** both teammates' `OnActionReceivedTeam` observe the same shared ball
position, so both could detect "ball in goal" on the same Academy step and each independently
call `AddGroupReward`/`EndGroupEpisode` — double-firing the terminal reward. This was
previously masked because ending an already-ended episode is a silent no-op; a *live* reset
is not silent, so it would have surfaced immediately as a visible double-reset. Fixed with a
leader-owned per-step guard (`TeamScoreEvent`, `teamScoreHandledStep`) — same pattern the
existing `PlaceTeamIfNeeded` already used for the start-of-episode placement race.

## 5. ScriptedOpponent.cs: continuous skill, not 3 fixed presets

**On the "wrong goal" report:** re-investigated from the actual object wiring (not just the
code) — `scoringGoal` (renamed `targetGoal`, see below) was and is correctly wired to the
AI's *defended* goal, which is exactly where the opponent is supposed to score (that's how it
scores *against* the AI — this matches `ai_robot/config.py`'s `ARUCO_ID_OWN_GOAL` comment
verbatim: "human shoots here"). **This was not a code bug.** I renamed the field to
`targetGoal` purely for clarity (with `[FormerlySerializedAs("scoringGoal")]` so your existing
Inspector wiring in `Environment.prefab` migrates automatically — nothing to re-drag) and
added a Scene-view gizmo label ("opponent scores here") so it's visually unmistakable.

**New:** a continuous `skillLevel` (0-1), orthogonal to the existing speed multiplier, driven
by a new `opponent_skill` curriculum parameter. Blends three behaviors that used to be fixed
per discrete difficulty:
- **Hesitation chance** — same mechanic, now continuous (was a 3-value array).
- **Ball interception** — at skill 0, chases the ball's current position (old behavior); at
  skill 1, leads it up to 0.45s ahead using `ballRb.linearVelocity` — a sharp opponent cuts
  off the ball's path instead of trailing it.
- **Aim precision** — a lateral shot jitter, rolled once per possession (not per-frame, so
  it doesn't look twitchy), shrinking from ±0.22m at skill 0 to ~0 at skill 1.

`opponentDifficulty` (Easy/Medium/Hard) still sets the speed tier and seeds a default skill
baseline for manual testing without a curriculum; `opponent_skill` from the YAML overrides it
at runtime. This is what makes the opponent "progressively challenging" rather than 3 discrete
rungs — see the new curriculum blocks in `Ra_medium.yaml`/`Ra_hard.yaml`/`Ra_2v2.yaml`.

## 6. Python side — required changes, NOT yet made (tell me if you want these done too)

I did not touch `brain_runner.py`/`config.py` this round (your file list didn't include
them) — but the redesign does not work end-to-end on real hardware without these:

1. **`build_observations()` must apply the same egocentric rotation.** Currently it computes
   `rel()` as a world-axis-aligned `(dx, dz)` offset, matching the *old* `SafeVec`. It needs
   to rotate by the robot's heading the same way `EgoRel`/`EgoVec` do — `(dx*sinθ + dz*cosθ,
   dx*cosθ - dz*sinθ)` where θ is the robot's heading angle, giving (forward-component,
   right-component). Applies everywhere `rel()` is called (ball, goal, own-goal) and to
   `opp_heading`/velocity terms too.
2. **New: robot self-velocity tracking.** `build_observations()`'s `[2-3]` slots now need
   forward-speed + yaw-rate instead of raw heading. The file already tracks the *ball's*
   position frame-to-frame to compute `ball_vel_ms` — the robot's own ArUco-tracked center/
   heading needs the identical differencing, which doesn't exist yet for the robot itself.
3. **`EXPECTED_OBS` must become mode-aware.** Currently `EXPECTED_OBS = {"easy": 13,
   "medium": 19, "hard": 24}`, keyed only by difficulty — `obs_size = EXPECTED_OBS.get(
   difficulty, 13)` at the call site ignores `mode` entirely. For `mode == "2v2"`, `obs_size`
   must be `26` regardless of difficulty (matching `GameSettings.ObsSize`'s fix in this
   redesign) — otherwise `Brain.__init__`'s own size-check will hard-fail the moment a 2v2
   `.onnx` is loaded.
4. **New: `build_team_observations()`.** There is currently no 2v2-shaped observation builder
   in Python at all — `build_observations()` only ever produces the 13/19/24 1v1 layout. A
   new function mirroring `CollectTeamObservations`'s exact 26-slot order (including
   teammate + fixed opponent1/opponent2 ordering) needs to be written from scratch.

I can do all four as a follow-up once you're ready — say so and I'll treat it as its own
task rather than bundling it into this already-large change.

## 7. Files changed/added

| File | What changed |
|---|---|
| `Unity/Assets/AI Scripts/RobotAgent.cs` | Full redesign per §1-4 above, built on your pasted draft |
| `Unity/Assets/AI Scripts/ScriptedOpponent.cs` | Continuous skill system per §5, field rename |
| `Unity/Assets/AI Scripts/GameSettings.cs` | `ObsSize` is now match-mode-aware (was silently wrong for 2v2 — see below) |
| `Unity/config/Ra_easy.yaml` | New location (per your note it lived in `Unity/config` on your other machine). No hyperparameter changes — reward scale unchanged |
| `Unity/config/Ra_medium.yaml` | Added `opponent_skill` curriculum |
| `Unity/config/Ra_hard.yaml` | Added `opponent_skill` curriculum |
| `Unity/config/Ra_2v2.yaml` | Added `opponent_skill` curriculum, documented soft-kickoff behavior change |
| `CHANGES.md` | This file |

**Also found while implementing:** `GameSettings.ObsSize` previously called
`DeriveObsSize(Difficulty)` — completely ignoring `Match`, so it would have returned
13/19/24 even when `Match == TwoVTwo`, silently sending the wrong-sized observation vector
to a 26-float 2v2 brain. Fixed: `DeriveObsSize(Match, Difficulty)` now returns the fixed
`TEAM_OBS_SIZE = 26` whenever `Match == TwoVTwo`, regardless of difficulty.

## 8. What you need to do in the Unity Editor before training

For **each** of the 4 Behavior Parameters components (Easy/Medium/Hard/2v2):

| Mode | Behavior Name | Vector Obs Space Size | Discrete Branch 0 |
|---|---|---|---|
| Easy | `RobotAgent` | **13** (unchanged) | 4 |
| Medium | `RobotAgent` | **19** (unchanged) | 4 |
| Hard | `RobotAgent` | **24** (unchanged) | 4 |
| 2v2 (both robots) | `RobotAgentTeam` | **26** (unchanged) | 4 |

None of the sizes changed, so if these were already set correctly you don't need to touch
them — just don't skip re-verifying, since it's exactly the kind of thing that silently
produces a garbage policy if it drifts.

Additionally:
- On `ScriptedOpponent`: nothing to re-wire — `[FormerlySerializedAs("scoringGoal")]` migrates
  the existing `targetGoal` reference automatically the next time the prefab/scene is opened
  in the Editor. Worth a quick visual check (Scene view, select the opponent) that the new
  "opponent scores here" gizmo label points at the goal you expect.
- For 2v2 specifically: confirm `teamMode = true`, `teammate`/`opponent`/`opponent2`/`ownGoal`
  are all assigned on **both** AI robots, and both use the `RobotAgentTeam` Behavior Name
  (shared policy) — all pre-existing requirements from your draft, unchanged here.
- Run every mode fresh: `mlagents-learn Unity/config/Ra_<mode>.yaml --run-id=<name>_v2_egocentric --force`.
  Never `--resume` an old run onto these — the obs slot semantics changed even where sizes
  didn't.
