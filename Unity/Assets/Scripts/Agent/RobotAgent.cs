using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;

/* ============================================================================
 * RobotAgent.cs  —  1v1 (Easy / Medium / Hard) + 2v2 Team Mode  —  EGOCENTRIC obs
 * ----------------------------------------------------------------------------
 * WHAT CHANGED vs the previous version (ball-convergence rebalance + restored
 * 2v2 team mode), and WHY:
 *
 *  1. EGOCENTRIC OBSERVATIONS (fixes the absolute-heading generalization bug).
 *     Symptom, confirmed by direct ONNX inference testing on the trained
 *     brains: holding the ball's position and bearing relative to the robot
 *     fixed and only rotating the robot's ABSOLUTE world heading flipped the
 *     forward/turn decision in 4-6 of 8 cases, for every difficulty tier.
 *     Root cause, confirmed by reading the old CollectObservations: every
 *     relative vector (ball, goal, opponent, teammate) was position-relative
 *     but NOT rotated into the robot's own facing direction — it stayed in
 *     raw world X/Z axes, while heading was reported as a SEPARATE raw world
 *     forward vector. The network had to implicitly learn the rotation
 *     relating the two, which it never generalized cleanly across headings.
 *     Fix: every relative vector below is now expressed as
 *     (forward-component, right-component, distance) via EgoRel()/EgoVec(),
 *     i.e. Vector3.Dot against this robot's OWN transform.forward/right. This
 *     is a formula change only — vector length is unaffected by a 2D
 *     rotation, so Easy/Medium/Hard/2v2 obs sizes are UNCHANGED (13/19/24/26).
 *     brain_runner.py's build_observations() must apply the same rotation —
 *     see CHANGES.md.
 *
 *  2. SELF-VELOCITY replaces raw world heading (own heading becomes near
 *     content-free once everything else is egocentric — "I always face my
 *     own forward direction"). The freed 2 floats now carry forward speed and
 *     yaw rate — proprioception the policy previously had zero of. This is
 *     sim-to-real safe: the real pipeline already tracks the robot's own pose
 *     from ArUco every frame (same as it already does for the ball), so
 *     brain_runner.py just needs the same frame-to-frame differencing it
 *     already applies to the ball. See CHANGES.md for the required Python change.
 *
 *  3. 2v2 SOFT TEAM-KICKOFF (was: hard EndGroupEpisode on every goal).
 *     Mirrors the 1v1 Kickoff() density win: on score/concede, both AI
 *     robots + both opponents + the ball are reset and the episode keeps
 *     running to MaxStep, instead of ending outright. Implemented by
 *     re-invoking the same leader-guarded TeamRandomPlaceAll() used at
 *     episode start.
 *     While wiring this up, found and fixed a latent double-count bug: both
 *     teammates' OnActionReceivedTeam independently observe the same shared
 *     ball position, so both could detect "ball in goal" on the same Academy
 *     step and each call AddGroupReward/EndGroupEpisode — double-firing the
 *     terminal reward. TeamScoreEvent() now guards with a leader-owned
 *     per-step token (same pattern PlaceTeamIfNeeded already used for the
 *     start-of-episode placement race).
 *
 *  Everything else — the ball-convergence attractor (R_NEAR), the
 *  over-stopping fix (R_CONTROL_STEP removed, R_STOP_ON_BALL strengthened),
 *  the slashed wall costs, the spawn-curriculum-v2 (off-axis/wall-ball/
 *  goal-exclusion), domain randomization, and the action-latency simulation —
 *  is UNCHANGED from the previous version. Action space is UNCHANGED (4
 *  discrete S/F/L/R, matches the ESP32 firmware protocol exactly).
 *
 *  Observation contract (sizes unchanged, several slot MEANINGS changed —
 *  old checkpoints are not compatible, do not resume onto them):
 *    Easy=13, Medium=19, Hard=24, 2v2(team)=26 — see CollectObservations /
 *    CollectTeamObservations for the exact per-index layout.
 * ==========================================================================*/

public class RobotAgent : Agent
{
    public enum TrainingMode { Easy = 0, Medium = 1, Hard = 2 }
    public enum Role { Attacker = 0, Defender = 1 }

    // ── Inspector ───────────────────────────────────────────────────────────
    [Header("Mode — set Behavior Parameters Space Size to 13/19/24")]
    [SerializeField] public TrainingMode trainingMode = TrainingMode.Easy;

    [Header("Arena root")]
    [SerializeField] private Transform arenaRoot;

    [Header("Wheel Colliders")]
    [SerializeField] private WheelCollider frontLeftWheel;
    [SerializeField] private WheelCollider frontRightWheel;
    [SerializeField] private WheelCollider rearLeftWheel;
    [SerializeField] private WheelCollider rearRightWheel;

    [Header("Scene references")]
    [Tooltip("The single physical ball (must have a Rigidbody).")]
    [SerializeField] private Transform ball;
    [Tooltip("Goal the AI scores INTO.")]
    [SerializeField] private Transform goal;
    [Tooltip("Goal the AI DEFENDS (own goal). Medium+ uses it; Hard observes it.")]
    [SerializeField] private Transform ownGoal;
    [Tooltip("Opponent transform (Medium/Hard). Leave empty for Easy.")]
    [SerializeField] private Transform opponent;
    [Tooltip("Optional: ball kickoff point. Leave EMPTY to auto-find a child " +
             "named 'BallSpawnPoint' under the arena, or fall back to centre.")]
    [SerializeField] private Transform ballHome;

    [Header("Arena size — half side in metres (real arena 1.5m → 0.75)")]
    [SerializeField] private float arenaHalfSize = 0.75f;

    [Header("Interaction thresholds (metres)")]
    [Tooltip("Ball is 'in control' when within this distance AND in front.")]
    [SerializeField] private float controlDistance = 0.10f;
    [Tooltip("Min dot(heading, dirToBall) for control (0.3 ≈ within ~70°).")]
    [SerializeField] private float controlFacingDot = 0.30f;
    [Tooltip("Ball-in-goal radius for scoring/conceding.")]
    [SerializeField] private float goalRadius = 0.20f;

    [Header("Drive settings")]
    // Sim is damping-limited: top speed v = motorTorque * radius / wheelDampingRate
    //   = motorTorque * 0.04 / 0.35 = motorTorque * 0.1143 m/s.
    // Real robot measured at 0.60 m/s → motorTorque = 0.60 / 0.1143 = 5.25.
    // (motorTorque=10 gave 1.143 m/s, which is why the sim was ~1.9x too fast.)
    [SerializeField] private float motorTorque = 5.25f;   // forward drive — ~0.60 m/s
    // Turn is DECOUPLED from drive so lowering drive speed doesn't cripple turning.
    // MEASURED: sim turn rate is ~180 deg/s and did NOT change between turnTorque=10
    // and turnTorque=12 — past the WheelCollider's sideways-friction ceiling, torque
    // stops being the lever (in-place pivot turns are slip-dominated, not motor-torque-
    // dominated). Real robot was measured at TURN_SPEED=220 -> 225-250 deg/s; it has
    // NOT been re-measured at the firmware's CURRENT TURN_SPEED=200, so there is no
    // trustworthy target to tune turnTorque (or WheelCollider sideways friction) against.
    // TODO(calibration): re-measure real robot turn rate at TURN_SPEED=200 (the value the
    // current brains were trained/calibrated against — esp32_wifi_brain.ino), then decide
    // whether to raise WheelCollider Sideways Friction (Extremum Value/Stiffness) or
    // accept ~180 deg/s as close enough. Do not change turnTorque without that number.
    [SerializeField] private float turnTorque = 12f;      // in-place turn only
    [SerializeField] private float brakeTorque = 50f;

    [Header("Sim-to-real velocity scale (m/s that maps to obs = 1.0)")]
    [Tooltip("Ball velocity normalization. Ball moves much faster than the robot.")]
    [SerializeField] private float velScale = 1.5f;
    [Tooltip("Own forward-speed normalization. Matches the calibrated ~0.60 m/s top " +
             "speed above, NOT velScale — reusing the ball's scale would compress the " +
             "robot's own speed into a tiny, low-resolution slice of the obs range.")]
    [SerializeField] private float robotVelScale = 0.60f;
    [Tooltip("Own yaw-rate normalization (rad/s). ~180 deg/s measured top turn rate ≈ 3.14 rad/s.")]
    [SerializeField] private float yawRateScale = 3.2f;

    [Header("Domain randomization (sim-to-real)")]
    [SerializeField] private bool randomize = true;
    [SerializeField] private float obsPosNoise = 0.01f;     // metres
    [SerializeField] private int maxActionLatency = 2;    // decision steps

    // ── Ball physics (match your REAL ball — measure it!) ───────────────────
    // Real Robo-soccer ball is a light hollow sphere. Set these ranges around
    // your measured mass so the sim ball accelerates/decelerates like the real
    // one. VEL_SCALE (obs normalization) is unchanged, so a lighter/faster ball
    // just fills more of the velocity range — that is intended parity.
    [Header("Ball physics (domain-randomized around real ball)")]
    [SerializeField] private Vector2 ballMassRange = new Vector2(0.025f, 0.05f);   // kg
    [SerializeField] private Vector2 ballLinearDampingRange = new Vector2(0.15f, 0.45f);
    [SerializeField] private Vector2 ballAngularDampingRange = new Vector2(0.02f, 0.10f);

    // ── Spawn curriculum v2 (fixes observed weak cases) ──────────────────────
    // #1 off-axis: force the ball to sit LEFT/RIGHT of the robot's nose most
    //    episodes, so it must turn-then-push (not just shove a ball already ahead).
    // #4 wall balls: deliberately pin the ball to a wall some episodes so it
    //    learns to extract and push along the wall.
    // #2 random kickoff: on a goal, the ball "vanishes" and respawns at a random
    //    NON-goal spot while the robot returns home (no lingering ball-in-goal).
    [Header("Spawn curriculum v2")]
    [Tooltip("Fraction of episodes where the ball is guaranteed OFF the robot's nose.")]
    [SerializeField, Range(0f, 1f)] private float offAxisSpawnChance = 0.70f;
    [Tooltip("Minimum bearing (deg) from the robot's forward when off-axis.")]
    [SerializeField] private float offAxisMinDeg = 40f;
    [Tooltip("Fraction of episodes where the ball starts pinned to a wall.")]
    [SerializeField, Range(0f, 1f)] private float wallBallChance = 0.25f;
    [Tooltip("On a goal, respawn the ball at a random non-goal spot (vs fixed centre).")]
    [SerializeField] private bool randomKickoffSpawn = true;
    [Tooltip("Keep the ball at least this far OUTSIDE goalRadius of BOTH goals when spawning.")]
    [SerializeField] private float goalExclusionMargin = 0.12f;   // metres

    // ── 2v2 Team Mode (shared policy — see CollectTeamObservations) ─────────
    // Everything below is ADDITIVE. When teamMode is false, no code path here
    // is ever touched and RobotAgent behaves exactly as in 1v1.
    [Header("2v2 Team Mode (shared policy — see CollectTeamObservations)")]
    [Tooltip("false = existing 1v1 (unchanged, 13/19/24 obs). true = 2v2: 26 obs, " +
             "Medium-style per-robot reward, group score/concede with soft team-kickoff.")]
    [SerializeField] private bool teamMode = false;
    [Tooltip("The OTHER AI robot on this team. Must also have teamMode = true, the " +
             "SAME Behavior Name, and a mirrored teammate/opponent/opponent2 wiring.")]
    [SerializeField] private Transform teammate;
    [Tooltip("Second opponent (2v2 only). The existing 'opponent' field above is " +
             "opponent1 — order is FIXED, never sorted by distance (must match " +
             "brain_runner.py's marker-ID convention: opponent1 = lower ID).")]
    [SerializeField] private Transform opponent2;

    // ── Reward constants  (AGGRESSIVE SCORER) ────────────────────────────────
    private const float R_SCORE = 12.0f;   // ball in scoring goal (terminal-scale payoff)
    private const float R_SCORE_SPEED = 3.0f;    // extra, scaled by how fast after kickoff
    private const float R_CONCEDE = -10.0f;  // ball in own goal
    private const float R_BALL_PROGRESS = 5.0f;    // ball moves toward goal (potential) — MAIN once you have the ball
    // ── Ball-acquisition shaping (the fix for "dBall never < 0.20") ─────────
    // R_NEAR is a DENSE, always-on attractor: every step off-ball pays in
    // proportion to how close you are, so the whole arena tilts downhill toward
    // the ball. This is what a pure potential term could not do — a potential
    // telescopes to ~0 over any loop, so it gave no consistent gradient for a
    // random policy to climb. R_NEAR provides that gradient; R_APPROACH (still
    // potential-based, hence policy-safe) adds a crisp "closing the gap" signal
    // on top. Together they dominate STEP_COST and the (now small) wall costs.
    private const float R_NEAR = 0.02f;   // per step: R_NEAR * (1 - dBall/ARENA_DIAG), off-ball only
    private const float R_APPROACH = 2.5f;    // robot moves toward ball (potential, pre-control) (was 1.5)
    private const float R_FACE_BALL = 0.50f;   // turn to FACE the ball (potential, direction-agnostic) (was 0.10)
    private const float R_DRIVE_AT_BALL = 0.01f;   // FORWARD while already facing the ball — teaches "aim, then go"
    // R_CONTROL_STEP intentionally REMOVED — holding the ball must not pay.
    private const float R_PUSH_ALIGN = 0.05f;   // behind-ball-toward-goal alignment while controlling
    private const float R_IDLE = -0.005f; // STOP while NOT in control
    private const float R_STOP_ON_BALL = -0.05f;  // STOP while in control — must keep pushing
    private const float R_LOST_CONTROL = -0.3f;   // had control, lost it
    private const float R_STEAL = 1.0f;    // gained control from opponent (Medium+)
    private const float R_CLEAR = 1.0f;    // pushed ball away from own goal (Hard defend)
    private const float R_DEFEND_SHAPING = 1.0f;    // move to intercept point (Hard defend)
    private const float R_OWNGOAL_PUSH = -3.0f;   // controlling + pushing ball toward own goal (Hard)
    private const float R_CORRECT_ROLE = 0.001f;  // per step, role-correct half
    private const float STEP_COST = -0.0008f; // per step (small urgency; attractor now supplies the gradient)
    private const float SPIN_PENALTY = -0.002f;
    // ── Wall costs SLASHED (were drowning the approach signal) ──────────────
    // Old values (-0.5 OOB, 0.05 soft) made fetching a wall-side ball net-negative
    // relative to the +0.03/step best-case approach. Clamping still keeps the
    // robot in-bounds; we just no longer punish it for being near a wall (the
    // ball is often there). Kept nonzero so the policy still prefers open space.
    private const float WALL_HARD = -3.0f;   // (kept for reference; unused in loop)
    private const float WALL_OOB = -0.05f;  // per-step while clamped at the wall (was -0.5)
    private const float WALL_SOFT_MAX = 0.01f;  // (was 0.05)
    private const float WALL_SOFT_FRAC = 0.12f;  // narrower band (was 0.15)

    // Fast-score window: scoring within this many steps of a kickoff earns the
    // full speed bonus, decaying to 0 by this many steps.
    private const int FAST_SCORE_STEPS = 300;

    // ── Runtime state ─────────────────────────────────────────────────────────
    private Rigidbody rb;
    private Rigidbody ballRb;
    private Renderer agentRenderer;
    private RobotAgent opponentAgent;
    private RobotAgent opponent2Agent;   // 2v2 only — mirrors opponentAgent for opponent2

    private bool inControl = false;
    private bool hadControlLast = false;
    private Role currentRole = Role.Attacker;

    private float prevBallToGoal;
    private float prevRobotToBall;
    private float prevFaceDot;
    private float prevBallToOwnGoal;
    private float prevInterceptDist;

    private bool oppInControlLast = false;

    private int stepsSinceKickoff = 0;

    // homes (local space), computed once per episode from goal geometry
    private Vector3 robotHomeLocal;
    private Vector3 oppHomeLocal;
    private Vector3 ballHomeLocal;

    // anti-stuck
    private Vector3 lastStuckCheckPos;
    private int stuckCheckCounter;
    private const int STUCK_CHECK_INTERVAL = 50;   // steps
    private const float STUCK_MOVE_MIN = 0.05f; // metres

    // action latency buffer
    private int[] actionDelayBuffer;
    private int actionLatency;

    // curriculum-driven (read from EnvironmentParameters)
    private float curBallSpawnRadius = 1.0f;   // fraction of spawnRange (1 = full)
    private float curOpponentSpeed = 1.0f;

    // ── 2v2 team-mode runtime state ───────────────────────────────────────────
    private RobotAgent teammateAgent;
    private SimpleMultiAgentGroup teamGroup;         // only ever instantiated on the team leader
    private int teamPlacedAcademyStep = -1;          // dedup token: start-of-episode placement race
    private int teamScoreHandledStep = -1;           // dedup token: mid-episode score/concede race (leader-owned)

    private static readonly Color C_IDLE = Color.blue;
    private static readonly Color C_CTRL = Color.cyan;
    private static readonly Color C_SCORE = Color.green;
    private static readonly Color C_DEFEND = new Color(0.8f, 0.1f, 0.8f);
    private static readonly Color C_WALL = new Color(1f, 0.5f, 0f);

    public bool CurrentInControl => inControl;
    public Role CurrentRole => currentRole;

    // True on exactly one of a pair of teammates (deterministic tie-break by
    // Agent-instance-id) — used to decide who owns the shared SimpleMultiAgentGroup
    // and who performs the once-per-reset team placement.
    private bool IsTeamLeader => teammateAgent == null || GetInstanceID() < teammateAgent.GetInstanceID();

    // Resolves to the ONE shared group for this pair, regardless of which of the
    // two instances asks (private-field access across instances of the same
    // class is legal in C#, so the follower can read the leader's teamGroup).
    private SimpleMultiAgentGroup TeamGroup =>
        IsTeamLeader ? teamGroup : (teammateAgent != null ? teammateAgent.teamGroup : null);

    // ── Lifecycle ───────────────────────────────────────────────────────────────
    public override void Initialize()
    {
        agentRenderer = GetComponentInChildren<Renderer>();
        rb = GetComponent<Rigidbody>();
        if (arenaRoot == null) arenaRoot = transform.parent;
        if (ball != null) ballRb = ball.GetComponent<Rigidbody>();
        if (opponent != null) opponentAgent = opponent.GetComponent<RobotAgent>();
        if (opponent2 != null) opponent2Agent = opponent2.GetComponent<RobotAgent>();

        // Auto-find the ball kickoff point if not assigned in the Inspector.
        if (ballHome == null && arenaRoot != null)
            ballHome = FindDeepChild(arenaRoot, "BallSpawnPoint");

        if (ballRb == null) Debug.LogError($"[RobotAgent:{name}] Ball needs a Rigidbody.");
        if (trainingMode >= TrainingMode.Medium && opponent == null)
            Debug.LogWarning($"[RobotAgent:{name}] Medium+ but no opponent assigned.");
        if (trainingMode >= TrainingMode.Hard && ownGoal == null)
            Debug.LogWarning($"[RobotAgent:{name}] Hard but no ownGoal assigned.");

        actionDelayBuffer = new int[maxActionLatency + 1];

        // ── 2v2 team-mode setup (additive; no-op when teamMode is false) ─────
        if (teamMode)
        {
            if (teammate != null) teammateAgent = teammate.GetComponent<RobotAgent>();
            if (teammate == null) Debug.LogWarning($"[RobotAgent:{name}] teamMode but no teammate assigned.");
            if (opponent == null) Debug.LogWarning($"[RobotAgent:{name}] teamMode but no opponent (opponent1) assigned.");
            if (opponent2 == null) Debug.LogWarning($"[RobotAgent:{name}] teamMode but no opponent2 assigned.");
            if (ownGoal == null) Debug.LogWarning($"[RobotAgent:{name}] teamMode but no ownGoal assigned — concede will never fire.");

            if (IsTeamLeader)
            {
                teamGroup = new SimpleMultiAgentGroup();
                teamGroup.RegisterAgent(this);
                if (teammateAgent != null) teamGroup.RegisterAgent(teammateAgent);
            }
        }
    }

    public override void OnEpisodeBegin()
    {
        // Curriculum parameters (defaults if not set by trainer YAML)
        var ep = Academy.Instance.EnvironmentParameters;
        curBallSpawnRadius = ep.GetWithDefault("ball_spawn_radius", 1.0f);
        curOpponentSpeed = ep.GetWithDefault("opponent_speed", 1.0f);
        controlDistance = ep.GetWithDefault("control_distance", controlDistance);
        ApplyOpponentSkillCurriculum(ep);

        if (teamMode) { OnEpisodeBeginTeam(); return; }

        // Compute the home / kickoff positions for this episode from geometry.
        ComputeHomes();

        // Full randomized placement at the start of each episode (variety).
        RandomPlaceAll();
    }

    // Reads the new "opponent_skill" curriculum key (Medium/Hard/2v2 only — Easy
    // trains with no opponent) and pushes it to ScriptedOpponent. Orthogonal to
    // opponent_speed: speed scales raw motion, skill scales hesitation/aim/
    // interception smarts. Missing key/opponent => no-op, matching existing
    // opponent_speed behaviour (safe default, never breaks Easy).
    private void ApplyOpponentSkillCurriculum(EnvironmentParameters ep)
    {
        float skill = ep.GetWithDefault("opponent_skill", -1f);   // -1 sentinel = key absent, don't override Inspector value
        if (skill < 0f) return;
        if (opponent != null)
        {
            var so = opponent.GetComponent<ScriptedOpponent>();
            if (so != null) so.SetSkill(skill);
        }
        if (opponent2 != null)
        {
            var so2 = opponent2.GetComponent<ScriptedOpponent>();
            if (so2 != null) so2.SetSkill(skill);
        }
    }

    // ── Egocentric transform helpers (THE fix for the heading-generalization bug) ──
    // Every relative vector fed to the network must be expressed in the robot's
    // OWN facing frame, not raw world axes — otherwise the network has to
    // implicitly learn the rotation relating heading to bearing, which it does
    // not generalize across headings (confirmed by direct inference testing).
    // Returned as (forward-component, right-component, distance) packed into a
    // Vector3 purely as a 3-float carrier — .x/.y/.z here are NOT spatial axes.
    private Vector3 EgoRel(Transform target, Vector3 lp)
    {
        if (target == null) return Vector3.zero;
        Vector3 rel = target.localPosition - lp; rel.y = 0f;
        float fwdComp = Vector3.Dot(rel, transform.forward);
        float rightComp = Vector3.Dot(rel, transform.right);
        return new Vector3(fwdComp, rightComp, rel.magnitude);
    }
    // Same rotation, for a world-space vector that isn't a transform (velocities).
    private Vector2 EgoVec(Vector3 worldVec)
    {
        worldVec.y = 0f;
        return new Vector2(Vector3.Dot(worldVec, transform.forward), Vector3.Dot(worldVec, transform.right));
    }

    // ── Observations  (contract sizes UNCHANGED: Easy=13, Medium=19, Hard=24 — ──
    // several slot MEANINGS changed to egocentric + self-velocity, see header.
    // 2v2 team mode branches to CollectTeamObservations (26-float contract documented there).
    public override void CollectObservations(VectorSensor sensor)
    {
        if (teamMode) { CollectTeamObservations(sensor); return; }

        float n = arenaHalfSize;
        Vector3 lp = transform.localPosition;

        // [0-1] own pos — world-frame, absolute. Legitimate as-is: this is field-side/
        // wall-proximity awareness, not a bearing, so it does not need rotation.
        sensor.AddObservation(NoisyN(lp.x, n));
        sensor.AddObservation(NoisyN(lp.z, n));

        // [2-3] own motion — REPLACES raw world heading. Heading is implicit once
        // everything below is egocentric ("I always face my own forward axis"), so
        // these two slots now carry forward speed + yaw rate instead: proprioception
        // the policy previously had none of. Sim-to-real: brain_runner.py computes
        // this the same way it already computes ball velocity — frame-to-frame
        // position/heading deltas from the same ArUco tracking (see CHANGES.md).
        float fwdSpeed = rb != null ? Vector3.Dot(rb.linearVelocity, transform.forward) : 0f;
        float yawRate = rb != null ? rb.angularVelocity.y : 0f;
        sensor.AddObservation(Mathf.Clamp(fwdSpeed / robotVelScale, -2f, 2f));
        sensor.AddObservation(Mathf.Clamp(yawRate / yawRateScale, -2f, 2f));

        // [4-6] ball, egocentric (forward-component, right-component, distance)
        Vector3 bEgo = EgoRel(ball, lp);
        sensor.AddObservation(Clamp2(NoisyVal(bEgo.x) / n));
        sensor.AddObservation(Clamp2(NoisyVal(bEgo.y) / n));
        sensor.AddObservation(Mathf.Clamp(bEgo.z / n, 0f, 2f));

        // [7-8] ball velocity, egocentric
        Vector3 bVelWorld = ballRb != null ? ballRb.linearVelocity : Vector3.zero;
        Vector2 bVelEgo = EgoVec(bVelWorld);
        sensor.AddObservation(Mathf.Clamp(bVelEgo.x / velScale, -2f, 2f));
        sensor.AddObservation(Mathf.Clamp(bVelEgo.y / velScale, -2f, 2f));

        // [9-11] scoring goal, egocentric
        Vector3 gEgo = EgoRel(goal, lp);
        sensor.AddObservation(Clamp2(gEgo.x / n));
        sensor.AddObservation(Clamp2(gEgo.y / n));
        sensor.AddObservation(Mathf.Clamp(gEgo.z / n, 0f, 2f));

        // [12] in-control
        sensor.AddObservation(inControl ? 1f : 0f);

        // Medium+ : opponent
        if (trainingMode >= TrainingMode.Medium)
        {
            if (opponent != null)
            {
                Vector3 oEgo = EgoRel(opponent, lp);
                sensor.AddObservation(Clamp2(oEgo.x / n));                       // [13]
                sensor.AddObservation(Clamp2(oEgo.y / n));                       // [14]
                // Opponent's facing, projected onto MY axes (not their own world
                // heading): +1 on the first = they face toward my forward axis,
                // +1 on the second = they face toward my right. Tells the network
                // "is the opponent oriented toward me/the ball" without handing it
                // an absolute-frame heading to overfit on.
                Vector2 oFwdEgo = EgoVec(opponent.forward);
                sensor.AddObservation(oFwdEgo.x);                                // [15]
                sensor.AddObservation(oFwdEgo.y);                                // [16]
                sensor.AddObservation(Mathf.Clamp(oEgo.z / n, 0f, 2f));          // [17]
                sensor.AddObservation(OpponentInControl() ? 1f : 0f);           // [18]
            }
            else for (int i = 0; i < 6; i++) sensor.AddObservation(0f);
        }

        // Hard : own goal + role + threat
        if (trainingMode >= TrainingMode.Hard)
        {
            Vector3 ogEgo = EgoRel(ownGoal, lp);
            sensor.AddObservation(Clamp2(ogEgo.x / n));                          // [19]
            sensor.AddObservation(Clamp2(ogEgo.y / n));                          // [20]
            sensor.AddObservation(Mathf.Clamp(ogEgo.z / n, 0f, 2f));             // [21]
            sensor.AddObservation(currentRole == Role.Defender ? 1f : 0f);       // [22]
            // Ball-to-own-goal distance is a scalar between two OTHER objects (not
            // robot-relative), so it is already frame-independent — no rotation needed.
            float bToOwn = (ball != null && ownGoal != null)
                ? LocalDist(ball.localPosition, ownGoal.localPosition) : 2f * n;
            sensor.AddObservation(Mathf.Clamp(bToOwn / n, 0f, 2f));              // [23]
        }
    }

    // ── Actions ────────────────────────────────────────────────────────────────
    public override void OnActionReceived(ActionBuffers actions)
    {
        if (teamMode) { OnActionReceivedTeam(actions); return; }

        stepsSinceKickoff++;

        // Action latency (sim-to-real): delay applied action by N decisions
        int requested = actions.DiscreteActions[0];
        int applied = requested;
        if (actionLatency > 0)
        {
            for (int i = actionDelayBuffer.Length - 1; i > 0; i--)
                actionDelayBuffer[i] = actionDelayBuffer[i - 1];
            actionDelayBuffer[0] = requested;
            applied = actionDelayBuffer[actionLatency];
        }
        ApplyDrive(applied);

        // Out of bounds: clamp back inside and penalise — do NOT kickoff/teleport.
        // Teleporting to home on every wall touch created a spawn→wall→teleport
        // loop that trapped the robot in the corner. Clamping keeps play going.
        if (IsOutOfBounds())
        {
            AddReward(WALL_OOB); SetColour(C_WALL);
            ClampInsideArena();
        }
        float wp = WallProximityPenalty();
        if (wp > 0f) AddReward(-wp);

        AddReward(STEP_COST);

        // Spin penalty
        if (rb != null)
        {
            float spin = Mathf.Abs(rb.angularVelocity.y);
            if (spin > 1f) AddReward(SPIN_PENALTY * (spin - 1f));
        }

        if (ball == null || goal == null) return;

        // ── Update role (Hard, dynamic in 1v1) ───────────────────────────────
        if (trainingMode >= TrainingMode.Hard) UpdateRole();

        // ── Control detection (continuous) ───────────────────────────────────
        float dBall = LocalDist(transform.localPosition, ball.localPosition);
        Vector3 dirToBall = (ball.position - transform.position); dirToBall.y = 0f;
        float facing = dirToBall.sqrMagnitude > 1e-4f
            ? Vector3.Dot(transform.forward, dirToBall.normalized) : 0f;
        inControl = (dBall < controlDistance) && (facing > controlFacingDot);

        // Steal reward (Medium+): we just gained control while opponent had it
        if (trainingMode >= TrainingMode.Medium && inControl && oppInControlLast && !hadControlLast)
            AddReward(R_STEAL);

        // Lost-control penalty (had it, now don't)
        if (hadControlLast && !inControl) AddReward(R_LOST_CONTROL);

        // ── Face-the-ball shaping (potential) — rewards turning the SHORT way ──
        AddReward((facing - prevFaceDot) * R_FACE_BALL);
        prevFaceDot = facing;

        // ── Approach shaping (pre-control) ───────────────────────────────────
        if (!inControl)
        {
            // Dense attractor: always-on, grows as the robot nears the ball, so
            // the entire arena is a gentle downhill toward it. This is the piece
            // the old (purely potential) shaping lacked — see R_NEAR comment.
            float nearFrac = 1f - Mathf.Clamp01(dBall / (2f * arenaHalfSize));
            AddReward(R_NEAR * nearFrac);
            // Potential-based closing signal (telescoping — policy-invariant).
            AddReward((prevRobotToBall - dBall) * R_APPROACH);
            // Commit: reward driving FORWARD while already pointed at the ball.
            // Turns cost nothing here, so "aim then go" beats orbiting.
            if (applied == 1 && facing > 0.5f) AddReward(R_DRIVE_AT_BALL * facing);
            if (applied == 0) AddReward(R_IDLE);   // standing still off-ball is bad
            SetColour(C_IDLE);
        }
        prevRobotToBall = dBall;

        // ── Ball-to-goal progress (the MAIN signal, potential-based) ─────────
        float bToGoal = LocalDist(ball.localPosition, goal.localPosition);
        AddReward((prevBallToGoal - bToGoal) * R_BALL_PROGRESS);
        prevBallToGoal = bToGoal;

        // ── Control + push-alignment bonus (NO reward for merely holding) ───
        if (inControl)
        {
            SetColour(C_CTRL);
            // Parking on the ball is penalised — must keep pushing.
            if (applied == 0) AddReward(R_STOP_ON_BALL);
            // behind the ball, pushing toward goal: dot(robot->ball, ball->goal)
            Vector3 rToB = (ball.position - transform.position); rToB.y = 0f;
            Vector3 bToG = (goal.position - ball.position); bToG.y = 0f;
            if (rToB.sqrMagnitude > 1e-4f && bToG.sqrMagnitude > 1e-4f)
                AddReward(R_PUSH_ALIGN * Mathf.Max(0f, Vector3.Dot(rToB.normalized, bToG.normalized)));
        }

        // ── Hard: defending, own-goal prevention, clearance ──────────────────
        if (trainingMode >= TrainingMode.Hard && ownGoal != null)
        {
            float bToOwn = LocalDist(ball.localPosition, ownGoal.localPosition);
            if (bToOwn < goalRadius)
            {
                AddReward(R_CONCEDE); Kickoff(); return;
            }
        }

        // ── Score: BALL in scoring goal — reward + fast-score bonus + KICKOFF ─
        if (bToGoal < goalRadius)
        {
            float speedFrac = Mathf.Clamp01(1f - (float)stepsSinceKickoff / FAST_SCORE_STEPS);
            AddReward(R_SCORE + R_SCORE_SPEED * speedFrac);
            SetColour(C_SCORE);
            Kickoff();
            return;
        }

        // ── Anti-stuck ────────────────────────────────────────────────────────
        // Only fire when genuinely stranded FAR from the ball. Turning in place
        // to acquire heading does not translate, so punishing all non-movement
        // taxed the very skill Easy must learn. Parking once in range is already
        // handled by R_STOP_ON_BALL.
        if (++stuckCheckCounter >= STUCK_CHECK_INTERVAL)
        {
            float moved = LocalDist(transform.localPosition, lastStuckCheckPos);
            if (moved < STUCK_MOVE_MIN && dBall > controlDistance * 2f)
                AddReward(-0.03f);  // nudge away from idling in open space
            lastStuckCheckPos = transform.localPosition;
            stuckCheckCounter = 0;
        }

        hadControlLast = inControl;
        oppInControlLast = (trainingMode >= TrainingMode.Medium) && OpponentInControl();
    }

    // ── Home / kickoff geometry ──────────────────────────────────────────────────
    // Robot home is auto-computed from ownGoal: a defensive spot partway from the
    // own goal toward centre. Easy (no ownGoal) uses the mirror of the scoring
    // goal. Opponent home mirrors the robot. Ball home = BallSpawnPoint or centre.
    private void ComputeHomes()
    {
        // Home = an INTERIOR point on the own-goal side, well clear of the walls
        // AND of the arena centre (so the ball's centre kickoff is unobstructed).
        // It is NOT lerped onto the goal corner — doing that pinned the robot in
        // the corner and, together with the out-of-bounds handling, trapped it
        // against the wall so it never reached the ball. Goals are in diagonal
        // corners, so "own side" is just the direction toward ownGoal.
        Vector3 ownDir = (ownGoal != null && ownGoal.localPosition.sqrMagnitude > 1e-4f)
            ? ownGoal.localPosition.normalized
            : new Vector3(0f, 0f, -1f);

        const float HOME_RADIUS = 0.35f;   // metres from centre — safe interior, clear of walls
        robotHomeLocal = ownDir * HOME_RADIUS;
        robotHomeLocal.y = transform.localPosition.y;

        oppHomeLocal = -ownDir * HOME_RADIUS;                 // opposite side (toward AIGoal)
        oppHomeLocal.y = (opponent != null) ? opponent.localPosition.y : robotHomeLocal.y;

        if (ballHome != null) ballHomeLocal = ballHome.localPosition;
        else ballHomeLocal = Vector3.zero;   // arena centre
        ballHomeLocal.y = (ball != null) ? ball.localPosition.y : 0f;
    }

    // Kickoff (soft reset): robot returns home, ball "vanishes" from the goal and
    // respawns at a random NON-goal spot, robot faces the new ball. Episode runs on.
    private void Kickoff()
    {
        StopWheels();
        if (rb != null) { rb.linearVelocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }

        // Robot to home first.
        transform.localPosition = robotHomeLocal;

        // Ball to a fresh spot: random & non-goal (#2), or fixed centre if disabled.
        Vector3 newBall = ballHomeLocal;
        if (ball != null)
        {
            newBall = randomKickoffSpawn
                ? SafeNonGoalPos(arenaHalfSize * 0.72f, robotHomeLocal)
                : ballHomeLocal;
            newBall.y = ball.localPosition.y;
            ball.localPosition = newBall;
            if (ballRb != null) { ballRb.linearVelocity = Vector3.zero; ballRb.angularVelocity = Vector3.zero; }
        }

        // Robot faces the new ball (so it starts a fresh chase, not a fixed heading).
        Vector3 faceDir = (newBall - robotHomeLocal); faceDir.y = 0f;
        transform.localRotation = (faceDir.sqrMagnitude > 1e-4f)
            ? Quaternion.LookRotation(faceDir.normalized) : Quaternion.identity;

        // Opponent to its mirrored home, facing the ball.
        if (opponent != null)
        {
            var oppRb = opponent.GetComponent<Rigidbody>();
            if (oppRb != null) { oppRb.linearVelocity = Vector3.zero; oppRb.angularVelocity = Vector3.zero; }
            opponent.localPosition = oppHomeLocal;
            Vector3 oFace = (newBall - oppHomeLocal); oFace.y = 0f;
            opponent.localRotation = (oFace.sqrMagnitude > 1e-4f)
                ? Quaternion.LookRotation(oFace.normalized) : Quaternion.identity;
        }

        ResetShapingBaselines();
        stepsSinceKickoff = 0;
    }

    // Full random placement — used once at episode start for variety.
    private void RandomPlaceAll()
    {
        inControl = false; hadControlLast = false; oppInControlLast = false;
        currentRole = Role.Attacker;
        SetColour(C_IDLE);

        StopWheels();
        if (rb != null) { rb.linearVelocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }

        float spawnRange = arenaHalfSize * 0.72f;

        // AI spawn: anywhere in the interior. Goals are in DIAGONAL corners, so
        // there is no simple +Z/-Z "own half" — just spawn clear of the walls.
        Vector3 aiSpawn = RandomLocalPos(spawnRange);
        transform.localPosition = aiSpawn;
        transform.localRotation = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f); // random heading — both turns

        // Opponent spawn (Medium/Hard)
        if (opponent != null)
        {
            var oppRb = opponent.GetComponent<Rigidbody>();
            if (oppRb != null) { oppRb.linearVelocity = Vector3.zero; oppRb.angularVelocity = Vector3.zero; }
            opponent.localPosition = SafeLocalPos(new[] { aiSpawn }, controlDistance * 4f, spawnRange);
            opponent.localRotation = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
            var so = opponent.GetComponent<ScriptedOpponent>();
            if (so != null) so.SetSpeedMultiplier(curOpponentSpeed);
        }

        // Ball spawn — v2 curriculum. Distance from the robot scales with the
        // curriculum radius; BEARING is forced off-axis most episodes (#1) and a
        // fraction of episodes pin the ball to a wall (#4). Never inside a goal.
        if (ball != null)
        {
            float rFrac = Mathf.Clamp01(curBallSpawnRadius);
            float dist = Mathf.Lerp(controlDistance * 2.2f, spawnRange, rFrac);
            Vector3 bSpawn = PickBallSpawn(aiSpawn, transform.localRotation, dist, spawnRange);
            bSpawn.y = ball.localPosition.y;
            ball.localPosition = bSpawn;
            if (ballRb != null) { ballRb.linearVelocity = Vector3.zero; ballRb.angularVelocity = Vector3.zero; }

            // Domain randomization of ball physics — ranges are Inspector-driven
            // so config stays the single source of truth (set to your real ball).
            if (randomize && ballRb != null)
            {
                ballRb.mass = Random.Range(ballMassRange.x, ballMassRange.y);
                ballRb.linearDamping = Random.Range(ballLinearDampingRange.x, ballLinearDampingRange.y);
                ballRb.angularDamping = Random.Range(ballAngularDampingRange.x, ballAngularDampingRange.y);
            }
        }

        // Domain randomization of drive + action latency
        RollOwnActionLatency();

        ResetShapingBaselines();
        stepsSinceKickoff = 0;
        lastStuckCheckPos = transform.localPosition;
        stuckCheckCounter = 0;
    }

    // Recompute all potential-based shaping baselines so the first step after a
    // (soft or hard) reset does not produce a spurious reward spike.
    private void ResetShapingBaselines()
    {
        inControl = false; hadControlLast = false; oppInControlLast = false;

        prevRobotToBall = ball != null ? LocalDist(transform.localPosition, ball.localPosition) : 0f;
        prevFaceDot = 0f;
        if (ball != null)
        {
            Vector3 d0 = ball.position - transform.position; d0.y = 0f;
            if (d0.sqrMagnitude > 1e-4f) prevFaceDot = Vector3.Dot(transform.forward, d0.normalized);
        }
        prevBallToGoal = (ball != null && goal != null) ? LocalDist(ball.localPosition, goal.localPosition) : 0f;
        prevBallToOwnGoal = (ball != null && ownGoal != null) ? LocalDist(ball.localPosition, ownGoal.localPosition) : 0f;
        prevInterceptDist = ComputeInterceptDist();
    }

    // ── Role logic (Hard 1v1, dynamic) ──────────────────────────────────────────
    private void UpdateRole()
    {
        bool ballOurHalf = ball != null && ball.localPosition.z < 0f;
        bool oppHas = OpponentInControl();
        currentRole = (ballOurHalf || oppHas) ? Role.Defender : Role.Attacker;
    }

    private float ComputeInterceptDist()
    {
        if (ball == null || ownGoal == null) return 0f;
        Vector3 ip = Vector3.Lerp(ball.localPosition, ownGoal.localPosition, 0.45f);
        return LocalDist(transform.localPosition, ip);
    }

    // 1v1 only (Medium+ opponent-in-control observation slot, Hard role logic).
    private bool OpponentInControl() => IsOpponentInControlOf(opponent, opponentAgent);

    // 2v2 only — true if EITHER opponent currently has the ball. Used for
    // R_STEAL so a steal is credited regardless of which physical opponent it
    // was taken from (previously only opponent1 counted here — see
    // CHANGES.md / ai_robot/README.md's "known limitation"). This also makes
    // the opponent1-vs-opponent2 marker-ID assignment order harmless for this
    // specific check, since both are now treated identically.
    private bool AnyOpponentInControl() =>
        IsOpponentInControlOf(opponent, opponentAgent) || IsOpponentInControlOf(opponent2, opponent2Agent);

    private bool IsOpponentInControlOf(Transform opp, RobotAgent oppAgent)
    {
        if (opp == null || ball == null) return false;
        if (oppAgent != null) return oppAgent.CurrentInControl;
        var so = opp.GetComponent<ScriptedOpponent>();
        if (so != null) return so.CurrentHasBall;
        return LocalDist(opp.localPosition, ball.localPosition) < controlDistance;
    }

    // ── Heuristic (keyboard test) ────────────────────────────────────────────────
    public override void Heuristic(in ActionBuffers actionsOut)
    {
        var d = actionsOut.DiscreteActions;
        d[0] = 0;
        if (Input.GetKey(KeyCode.W)) d[0] = 1;
        if (Input.GetKey(KeyCode.A)) d[0] = 2;
        if (Input.GetKey(KeyCode.D)) d[0] = 3;
    }

    // ── Drive (in-place turns — matches ESP32 firmware) ──────────────────────────
    private void ApplyDrive(int act)
    {
        if (frontLeftWheel == null || frontRightWheel == null ||
            rearLeftWheel == null || rearRightWheel == null) return;
        switch (act)
        {
            case 1: SetAllWheels(motorTorque); break;
            case 2:
                frontLeftWheel.motorTorque = -turnTorque; rearLeftWheel.motorTorque = -turnTorque;
                frontRightWheel.motorTorque = turnTorque; rearRightWheel.motorTorque = turnTorque;
                SetAllBrakes(0f); break;
            case 3:
                frontLeftWheel.motorTorque = turnTorque; rearLeftWheel.motorTorque = turnTorque;
                frontRightWheel.motorTorque = -turnTorque; rearRightWheel.motorTorque = -turnTorque;
                SetAllBrakes(0f); break;
            default: SetAllWheels(0f); SetAllBrakes(brakeTorque); break;
        }
    }
    private void SetAllWheels(float t)
    {
        if (frontLeftWheel == null || frontRightWheel == null ||
            rearLeftWheel == null || rearRightWheel == null) return;
        frontLeftWheel.motorTorque = t; frontRightWheel.motorTorque = t;
        rearLeftWheel.motorTorque = t; rearRightWheel.motorTorque = t;
        SetAllBrakes(0f);
    }
    private void SetAllBrakes(float t)
    {
        if (frontLeftWheel == null || frontRightWheel == null ||
            rearLeftWheel == null || rearRightWheel == null) return;
        frontLeftWheel.brakeTorque = t; frontRightWheel.brakeTorque = t;
        rearLeftWheel.brakeTorque = t; rearRightWheel.brakeTorque = t;
    }
    private void StopWheels() { SetAllWheels(0f); SetAllBrakes(brakeTorque); }

    // ── Helpers ───────────────────────────────────────────────────────────────────
    private bool IsOutOfBounds()
    {
        float ax = Mathf.Abs(transform.localPosition.x);
        float az = Mathf.Abs(transform.localPosition.z);
        return ax > arenaHalfSize || az > arenaHalfSize;
    }
    // Clamp the robot back inside the arena and kill velocity — used instead of
    // teleporting home on out-of-bounds, so the robot can't get stuck looping.
    private void ClampInsideArena()
    {
        Vector3 lp = transform.localPosition;
        float lim = arenaHalfSize * 0.97f;
        lp.x = Mathf.Clamp(lp.x, -lim, lim);
        lp.z = Mathf.Clamp(lp.z, -lim, lim);
        transform.localPosition = lp;
        if (rb != null) { rb.linearVelocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
    }
    private float WallProximityPenalty()
    {
        float d = arenaHalfSize - Mathf.Max(Mathf.Abs(transform.localPosition.x),
                                            Mathf.Abs(transform.localPosition.z));
        float th = arenaHalfSize * WALL_SOFT_FRAC;
        if (d >= th) return 0f;
        float t = 1f - d / th;
        return WALL_SOFT_MAX * t * t;
    }
    private Vector3 SafeVec(Transform target, Vector3 from)
        => target == null ? Vector3.zero : target.localPosition - from;
    private float NoisyN(float v, float n)
        => Clamp2((v + (randomize ? Random.Range(-obsPosNoise, obsPosNoise) : 0f)) / n);
    private float NoisyVal(float v)
        => v + (randomize ? Random.Range(-obsPosNoise, obsPosNoise) : 0f);
    private static float LocalDist(Vector3 a, Vector3 b) => Vector3.Distance(a, b);
    private static float Clamp2(float v) => Mathf.Clamp(v, -2f, 2f);

    private Vector3 RandomLocalPos(float range, float zSign = 0f)
    {
        float x = Random.Range(-range, range);
        float z = zSign > 0f ? Random.Range(0f, range)
                : zSign < 0f ? Random.Range(-range, 0f)
                : Random.Range(-range, range);
        return new Vector3(x, transform.localPosition.y, z);
    }
    private Vector3 SafeLocalPos(Vector3[] avoids, float minDist, float range)
    {
        Vector3 best = RandomLocalPos(range); float bestClear = 0f;
        for (int i = 0; i < 200; i++)
        {
            Vector3 c = RandomLocalPos(range);
            float clear = float.MaxValue;
            foreach (var a in avoids) clear = Mathf.Min(clear, Vector3.Distance(c, a));
            if (clear >= minDist) return c;
            if (clear > bestClear) { bestClear = clear; best = c; }
        }
        return best;
    }

    private void RollOwnActionLatency()
    {
        actionLatency = randomize ? Random.Range(0, maxActionLatency + 1) : 0;
        if (actionDelayBuffer == null || actionDelayBuffer.Length != maxActionLatency + 1)
            actionDelayBuffer = new int[Mathf.Max(1, maxActionLatency + 1)];
        for (int i = 0; i < actionDelayBuffer.Length; i++) actionDelayBuffer[i] = 0;
    }

    // ── Spawn-curriculum-v2 helpers ───────────────────────────────────────────────
    // True if lp is inside (goalRadius + margin) of EITHER goal — used to keep the
    // ball from ever spawning in a goal (which caused instant scores / confusion).
    private bool InAnyGoalArea(Vector3 lp)
    {
        float m = goalRadius + goalExclusionMargin;
        if (goal != null && LocalDist(lp, goal.localPosition) < m) return true;
        if (ownGoal != null && LocalDist(lp, ownGoal.localPosition) < m) return true;
        return false;
    }

    // A random interior position that is clear of both goals AND of 'avoid'.
    private Vector3 SafeNonGoalPos(float range, Vector3 avoid)
    {
        float y = (ball != null) ? ball.localPosition.y : 0f;
        for (int i = 0; i < 120; i++)
        {
            Vector3 p = RandomLocalPos(range); p.y = y;
            if (!InAnyGoalArea(p) && Vector3.Distance(p, avoid) >= controlDistance * 2f)
                return p;
        }
        return RandomLocalPos(range);
    }

    // Ball placed at a controlled BEARING from the robot's forward. With
    // probability offAxisSpawnChance the bearing is at least offAxisMinDeg to the
    // left OR right, so the policy must rotate to acquire — the #1 fix.
    private Vector3 OffAxisBallPos(Vector3 robotPos, Quaternion robotRot, float dist, float range)
    {
        float y = (ball != null) ? ball.localPosition.y : 0f;
        Vector3 fwd = robotRot * Vector3.forward; fwd.y = 0f;
        fwd = (fwd.sqrMagnitude < 1e-4f) ? Vector3.forward : fwd.normalized;
        for (int i = 0; i < 40; i++)
        {
            bool offAxis = Random.value < offAxisSpawnChance;
            float mag = offAxis ? Random.Range(offAxisMinDeg, 180f)
                                : Random.Range(0f, offAxisMinDeg);
            float bearing = mag * (Random.value < 0.5f ? -1f : 1f);   // left OR right, 50/50
            Vector3 dir = Quaternion.Euler(0f, bearing, 0f) * fwd;
            Vector3 p = robotPos + dir * dist; p.y = y;
            p.x = Mathf.Clamp(p.x, -range, range);
            p.z = Mathf.Clamp(p.z, -range, range);
            if (!InAnyGoalArea(p) && Vector3.Distance(p, robotPos) >= controlDistance * 2f)
                return p;
        }
        return SafeNonGoalPos(range, robotPos);
    }

    // Ball pinned near a wall (interior side clear for the robot to approach).
    private Vector3 WallBallPos(Vector3 avoid)
    {
        float y = (ball != null) ? ball.localPosition.y : 0f;
        float edge = arenaHalfSize * 0.93f;                 // just off the physical wall
        float span = arenaHalfSize * 0.80f;                 // spread along the wall
        for (int i = 0; i < 40; i++)
        {
            int w = Random.Range(0, 4);                     // which wall
            float along = Random.Range(-span, span);
            Vector3 p = w == 0 ? new Vector3(edge, y, along)
                      : w == 1 ? new Vector3(-edge, y, along)
                      : w == 2 ? new Vector3(along, y, edge)
                      : new Vector3(along, y, -edge);
            if (!InAnyGoalArea(p) && Vector3.Distance(p, avoid) >= controlDistance * 2f)
                return p;
        }
        return SafeNonGoalPos(arenaHalfSize * 0.72f, avoid);
    }

    // One entry point: pick the ball spawn for this episode per the v2 curriculum.
    private Vector3 PickBallSpawn(Vector3 robotPos, Quaternion robotRot, float dist, float range)
    {
        if (randomize && Random.value < wallBallChance)
            return WallBallPos(robotPos);
        return OffAxisBallPos(robotPos, robotRot, dist, range);
    }

    // Recursive child search by name (for auto-finding BallSpawnPoint).
    private static Transform FindDeepChild(Transform parent, string childName)
    {
        if (parent == null) return null;
        foreach (Transform c in parent)
        {
            if (c.name == childName) return c;
            Transform found = FindDeepChild(c, childName);
            if (found != null) return found;
        }
        return null;
    }

    private void SetColour(Color c) { if (agentRenderer != null) agentRenderer.material.color = c; }

    // ─────────────────────────────────────────────────────────────────────────────
    // ── 2v2 TEAM MODE ────────────────────────────────────────────────────────────
    // Everything below is ADDITIVE and only ever reached when teamMode == true
    // (via the branches in CollectObservations / OnActionReceived / OnEpisodeBegin
    // above). It never runs, and never affects behaviour, for a 1v1 agent.
    // ─────────────────────────────────────────────────────────────────────────────

    // 2v2 observation contract — EXACTLY 26 floats, this fixed order. Egocentric:
    // every *_rel value is (forward-component, right-component, distance) via
    // EgoRel()/EgoVec() — rotated into THIS robot's own facing frame, same fix as
    // the 1v1 contract above. Sizes are unchanged from the previous team-mode
    // contract; only the rotation + the repurposed motion slot changed.
    //   [0]  own pos x              [1]  own pos z                (world-frame, absolute)
    //   [2]  own forward speed      [3]  own yaw rate             (replaces raw heading)
    //   [4]  ball fwd               [5]  ball right      [6]  ball dist        (egocentric)
    //   [7]  ball vel fwd           [8]  ball vel right                        (egocentric)
    //   [9]  scoring-goal fwd       [10] scoring-goal right   [11] scoring-goal dist  (ego)
    //   [12] own-goal fwd           [13] own-goal right       [14] own-goal dist      (ego)
    //   [15] self in-control
    //   [16] teammate fwd           [17] teammate right       [18] teammate dist      (ego)
    //   [19] am-I-nearest-the-ball flag (tie broken by lower GetInstanceID())
    //   [20] opponent1 fwd          [21] opponent1 right      [22] opponent1 dist     (ego)
    //   [23] opponent2 fwd          [24] opponent2 right      [25] opponent2 dist     (ego)
    // opponent1/opponent2 order is FIXED (never sorted by distance) — Python maps
    // opponent1 = lower marker ID, opponent2 = higher, and this order must match.
    // Any null transform contributes 0f to all of its slots (EgoRel already
    // returns Vector3.zero for a null target, which is what yields those zeros).
    // Keep this comment in sync with brain_runner.py's team-mode obs builder.
    private void CollectTeamObservations(VectorSensor sensor)
    {
        float n = arenaHalfSize;
        Vector3 lp = transform.localPosition;

        // [0-1] own pos (world-frame, absolute — same rationale as 1v1)
        sensor.AddObservation(NoisyN(lp.x, n));
        sensor.AddObservation(NoisyN(lp.z, n));

        // [2-3] own motion (replaces raw heading — see 1v1 CollectObservations comment)
        float fwdSpeed = rb != null ? Vector3.Dot(rb.linearVelocity, transform.forward) : 0f;
        float yawRate = rb != null ? rb.angularVelocity.y : 0f;
        sensor.AddObservation(Mathf.Clamp(fwdSpeed / robotVelScale, -2f, 2f));
        sensor.AddObservation(Mathf.Clamp(yawRate / yawRateScale, -2f, 2f));

        // [4-6] ball, egocentric + [7-8] ball velocity, egocentric
        Vector3 bEgo = EgoRel(ball, lp);
        sensor.AddObservation(Clamp2(NoisyVal(bEgo.x) / n));
        sensor.AddObservation(Clamp2(NoisyVal(bEgo.y) / n));
        sensor.AddObservation(Mathf.Clamp(bEgo.z / n, 0f, 2f));
        Vector3 bVelWorld = ballRb != null ? ballRb.linearVelocity : Vector3.zero;
        Vector2 bVelEgo = EgoVec(bVelWorld);
        sensor.AddObservation(Mathf.Clamp(bVelEgo.x / velScale, -2f, 2f));
        sensor.AddObservation(Mathf.Clamp(bVelEgo.y / velScale, -2f, 2f));

        // [9-11] scoring goal, egocentric
        Vector3 gEgo = EgoRel(goal, lp);
        sensor.AddObservation(Clamp2(gEgo.x / n));
        sensor.AddObservation(Clamp2(gEgo.y / n));
        sensor.AddObservation(Mathf.Clamp(gEgo.z / n, 0f, 2f));

        // [12-14] own goal, egocentric
        Vector3 ogEgo = EgoRel(ownGoal, lp);
        sensor.AddObservation(Clamp2(ogEgo.x / n));
        sensor.AddObservation(Clamp2(ogEgo.y / n));
        sensor.AddObservation(Mathf.Clamp(ogEgo.z / n, 0f, 2f));

        // [15] self in-control
        sensor.AddObservation(inControl ? 1f : 0f);

        // [16-18] teammate, egocentric
        Vector3 tmEgo = EgoRel(teammate, lp);
        sensor.AddObservation(Clamp2(tmEgo.x / n));
        sensor.AddObservation(Clamp2(tmEgo.y / n));
        sensor.AddObservation(Mathf.Clamp(tmEgo.z / n, 0f, 2f));

        // [19] am-I-nearest-the-ball. No teammate — trivially nearest (matches the
        // 1v1 Easy convention of the sole agent always being the attacker). Exact
        // ties are broken deterministically via the same instance-id comparison
        // IsTeamLeader already uses, so exactly one robot reads 1 in that case.
        bool amNearest = true;
        if (teammate != null && ball != null)
        {
            float myDist = LocalDist(lp, ball.localPosition);
            float tmDist = LocalDist(teammate.localPosition, ball.localPosition);
            amNearest = myDist < tmDist || (myDist == tmDist && IsTeamLeader);
        }
        sensor.AddObservation(amNearest ? 1f : 0f);

        // [20-22] opponent1, egocentric (FIXED order — never sorted by distance)
        Vector3 o1Ego = EgoRel(opponent, lp);
        sensor.AddObservation(Clamp2(o1Ego.x / n));
        sensor.AddObservation(Clamp2(o1Ego.y / n));
        sensor.AddObservation(Mathf.Clamp(o1Ego.z / n, 0f, 2f));

        // [23-25] opponent2, egocentric
        Vector3 o2Ego = EgoRel(opponent2, lp);
        sensor.AddObservation(Clamp2(o2Ego.x / n));
        sensor.AddObservation(Clamp2(o2Ego.y / n));
        sensor.AddObservation(Mathf.Clamp(o2Ego.z / n, 0f, 2f));
    }

    // 2v2 per-robot reward + drive. This is the existing Medium 1v1 shaping
    // (approach/face-ball/ball-progress/control-push-align/idle-stop/wall/spin/
    // step-cost/anti-stuck), reproduced verbatim per-robot — no role shaping, no
    // own-goal-push/clearance shaping, no teammate reward, no spacing reward.
    // Score/concede: SOFT team-kickoff (see TeamScoreEvent), mirroring the 1v1
    // density win — NOT a hard EndGroupEpisode like the previous version.
    // R_STEAL/oppInControlLast below use AnyOpponentInControl() — checks BOTH
    // opponent and opponent2, so a steal is credited regardless of which
    // physical opponent had the ball (previously opponent1-only; fixed).
    private void OnActionReceivedTeam(ActionBuffers actions)
    {
        stepsSinceKickoff++;

        // Action latency (sim-to-real), identical mechanism to 1v1, per-robot.
        int requested = actions.DiscreteActions[0];
        int applied = requested;
        if (actionLatency > 0)
        {
            for (int i = actionDelayBuffer.Length - 1; i > 0; i--)
                actionDelayBuffer[i] = actionDelayBuffer[i - 1];
            actionDelayBuffer[0] = requested;
            applied = actionDelayBuffer[actionLatency];
        }
        ApplyDrive(applied);

        if (IsOutOfBounds())
        {
            AddReward(WALL_OOB); SetColour(C_WALL);
            ClampInsideArena();
        }
        float wp = WallProximityPenalty();
        if (wp > 0f) AddReward(-wp);

        AddReward(STEP_COST);

        if (rb != null)
        {
            float spin = Mathf.Abs(rb.angularVelocity.y);
            if (spin > 1f) AddReward(SPIN_PENALTY * (spin - 1f));
        }

        if (ball == null || goal == null) return;

        float dBall = LocalDist(transform.localPosition, ball.localPosition);
        Vector3 dirToBall = (ball.position - transform.position); dirToBall.y = 0f;
        float facing = dirToBall.sqrMagnitude > 1e-4f
            ? Vector3.Dot(transform.forward, dirToBall.normalized) : 0f;
        inControl = (dBall < controlDistance) && (facing > controlFacingDot);

        if (inControl && oppInControlLast && !hadControlLast) AddReward(R_STEAL);
        if (hadControlLast && !inControl) AddReward(R_LOST_CONTROL);

        AddReward((facing - prevFaceDot) * R_FACE_BALL);
        prevFaceDot = facing;

        if (!inControl)
        {
            float nearFrac = 1f - Mathf.Clamp01(dBall / (2f * arenaHalfSize));
            AddReward(R_NEAR * nearFrac);
            AddReward((prevRobotToBall - dBall) * R_APPROACH);
            if (applied == 1 && facing > 0.5f) AddReward(R_DRIVE_AT_BALL * facing);
            if (applied == 0) AddReward(R_IDLE);
            SetColour(C_IDLE);
        }
        prevRobotToBall = dBall;

        float bToGoal = LocalDist(ball.localPosition, goal.localPosition);
        AddReward((prevBallToGoal - bToGoal) * R_BALL_PROGRESS);
        prevBallToGoal = bToGoal;

        if (inControl)
        {
            SetColour(C_CTRL);
            if (applied == 0) AddReward(R_STOP_ON_BALL);
            Vector3 rToB = (ball.position - transform.position); rToB.y = 0f;
            Vector3 bToG = (goal.position - ball.position); bToG.y = 0f;
            if (rToB.sqrMagnitude > 1e-4f && bToG.sqrMagnitude > 1e-4f)
                AddReward(R_PUSH_ALIGN * Mathf.Max(0f, Vector3.Dot(rToB.normalized, bToG.normalized)));
        }

        // Concede: ball in OWN goal — group penalty + SOFT team-kickoff.
        if (ownGoal != null)
        {
            float bToOwn = LocalDist(ball.localPosition, ownGoal.localPosition);
            if (bToOwn < goalRadius)
            {
                SetColour(C_WALL);
                TeamScoreEvent(R_CONCEDE);
                return;
            }
        }

        // Score: ball in SCORING goal — group reward + SOFT team-kickoff.
        if (bToGoal < goalRadius)
        {
            SetColour(C_SCORE);
            TeamScoreEvent(R_SCORE);
            return;
        }

        if (++stuckCheckCounter >= STUCK_CHECK_INTERVAL)
        {
            float moved = LocalDist(transform.localPosition, lastStuckCheckPos);
            if (moved < STUCK_MOVE_MIN && dBall > controlDistance * 2f)
                AddReward(-0.03f);
            lastStuckCheckPos = transform.localPosition;
            stuckCheckCounter = 0;
        }

        hadControlLast = inControl;
        oppInControlLast = AnyOpponentInControl();
    }

    // Single-owner guard for a mid-episode score/concede: both teammates'
    // OnActionReceivedTeam observe the SAME shared ball position, so both can
    // detect "ball in goal" on the same Academy step. Without this guard the
    // group reward AND the reset both double-fire (found while wiring up the
    // soft-kickoff change — the previous EndGroupEpisode version had the same
    // race, just masked because ending an already-ended episode is a silent
    // no-op; a live reset is not silent, so it surfaced here).
    // Mirrors PlaceTeamIfNeeded's TotalStepCount dedup pattern.
    private void TeamScoreEvent(float groupReward)
    {
        RobotAgent leader = IsTeamLeader ? this : teammateAgent;
        if (leader == null) { AddReward(groupReward); Kickoff(); return; }  // no teammate — behave like 1v1

        int nowStep = Academy.Instance.TotalStepCount;
        if (leader.teamScoreHandledStep == nowStep) return;   // already handled this step
        leader.teamScoreHandledStep = nowStep;

        TeamGroup?.AddGroupReward(groupReward);
        leader.TeamRandomPlaceAll();                 // soft reset: both robots, both opponents, ball
        leader.ResetShapingBaselines();
        if (leader.teammateAgent != null) leader.teammateAgent.ResetShapingBaselines();
        stepsSinceKickoff = 0;
        if (leader.teammateAgent != null) leader.teammateAgent.stepsSinceKickoff = 0;
    }

    private void OnEpisodeBeginTeam()
    {
        EnsureTeamPlaced();
        ResetShapingBaselines();
        lastStuckCheckPos = transform.localPosition;
        stuckCheckCounter = 0;
    }

    // Places both AI robots, both opponents, and the ball exactly once per group
    // reset. Guarded by Academy.Instance.TotalStepCount, which both teammates see
    // as the same value on the step their OnEpisodeBegin fires — so whichever of
    // the two calls this first performs the placement (on the LEADER instance,
    // regardless of who called it) and the second call is a no-op. This is what
    // stops the two agents from fighting over ball/opponent position, without
    // depending on OnEpisodeBegin invocation order between the two agents.
    private void EnsureTeamPlaced()
    {
        RobotAgent leader = IsTeamLeader ? this : teammateAgent;
        if (leader == null) { RandomPlaceAll(); return; }   // no teammate assigned — fall back to solo placement
        leader.PlaceTeamIfNeeded();
    }

    private void PlaceTeamIfNeeded()
    {
        int nowStep = Academy.Instance.TotalStepCount;
        if (teamPlacedAcademyStep == nowStep) return;
        teamPlacedAcademyStep = nowStep;
        TeamRandomPlaceAll();
    }

    // Leader-only: a non-overlapping layout for both AI robots, both opponents,
    // and the ball. Mirrors RandomPlaceAll's approach (SafeLocalPos / PickBallSpawn)
    // but clears all four bodies instead of just one. Reused verbatim for the
    // mid-episode soft team-kickoff (TeamScoreEvent), not just episode start.
    private void TeamRandomPlaceAll()
    {
        float spawnRange = arenaHalfSize * 0.72f;

        Vector3 selfSpawn = RandomLocalPos(spawnRange);
        Quaternion selfRot = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);

        Vector3 tmSpawn = SafeLocalPos(new[] { selfSpawn }, controlDistance * 4f, spawnRange);
        Vector3 opp1Spawn = SafeLocalPos(new[] { selfSpawn, tmSpawn }, controlDistance * 4f, spawnRange);
        Vector3 opp2Spawn = SafeLocalPos(new[] { selfSpawn, tmSpawn, opp1Spawn }, controlDistance * 4f, spawnRange);

        ApplyTeamTransform(this, selfSpawn, selfRot);
        if (teammateAgent != null)
            ApplyTeamTransform(teammateAgent, tmSpawn, Quaternion.Euler(0f, Random.Range(0f, 360f), 0f));
        PlaceOpponentTransform(opponent, opp1Spawn);
        PlaceOpponentTransform(opponent2, opp2Spawn);

        // Ball: reuse the existing v2 curriculum picker anchored on the leader, but
        // require clearance from ALL FOUR bodies (not just the leader) before accepting.
        if (ball != null)
        {
            float rFrac = Mathf.Clamp01(curBallSpawnRadius);
            float dist = Mathf.Lerp(controlDistance * 2.2f, spawnRange, rFrac);
            Vector3[] avoidAll = { selfSpawn, tmSpawn, opp1Spawn, opp2Spawn };
            Vector3 bSpawn = PickTeamBallSpawn(selfSpawn, selfRot, dist, spawnRange, avoidAll);
            bSpawn.y = ball.localPosition.y;
            ball.localPosition = bSpawn;
            if (ballRb != null) { ballRb.linearVelocity = Vector3.zero; ballRb.angularVelocity = Vector3.zero; }

            if (randomize && ballRb != null)
            {
                ballRb.mass = Random.Range(ballMassRange.x, ballMassRange.y);
                ballRb.linearDamping = Random.Range(ballLinearDampingRange.x, ballLinearDampingRange.y);
                ballRb.angularDamping = Random.Range(ballAngularDampingRange.x, ballAngularDampingRange.y);
            }
        }

        // Per-robot latency randomization (own sim-to-real comms latency) — each
        // robot rolls its own, same as RandomPlaceAll does for the 1v1 case.
        RollOwnActionLatency();
        if (teammateAgent != null) teammateAgent.RollOwnActionLatency();

        var oppSo1 = opponent != null ? opponent.GetComponent<ScriptedOpponent>() : null;
        if (oppSo1 != null) oppSo1.SetSpeedMultiplier(curOpponentSpeed);
        var oppSo2 = opponent2 != null ? opponent2.GetComponent<ScriptedOpponent>() : null;
        if (oppSo2 != null) oppSo2.SetSpeedMultiplier(curOpponentSpeed);
    }

    // Places one AI robot (self or teammate) and zeroes its physics/state. Static
    // because it is applied to whichever of the two instances is passed in — private
    // members of another instance of the SAME class are accessible in C#, so this
    // still reaches a's rb/StopWheels/SetColour/inControl normally.
    private static void ApplyTeamTransform(RobotAgent a, Vector3 localPos, Quaternion localRot)
    {
        a.StopWheels();
        if (a.rb != null) { a.rb.linearVelocity = Vector3.zero; a.rb.angularVelocity = Vector3.zero; }
        a.transform.localPosition = localPos;
        a.transform.localRotation = localRot;
        a.inControl = false; a.hadControlLast = false; a.oppInControlLast = false;
        a.SetColour(C_IDLE);
    }

    private void PlaceOpponentTransform(Transform opp, Vector3 localPos)
    {
        if (opp == null) return;
        var oppRb = opp.GetComponent<Rigidbody>();
        if (oppRb != null) { oppRb.linearVelocity = Vector3.zero; oppRb.angularVelocity = Vector3.zero; }
        opp.localPosition = localPos;
        opp.localRotation = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
    }

    // Ball spawn for 2v2: reuse the existing off-axis/wall-ball v2 curriculum
    // (PickBallSpawn, anchored on the leader) but re-roll until clear of ALL
    // avoid points (both AI robots + both opponents), not just the anchor.
    private Vector3 PickTeamBallSpawn(Vector3 anchorPos, Quaternion anchorRot, float dist, float range, Vector3[] avoidAll)
    {
        for (int i = 0; i < 40; i++)
        {
            Vector3 candidate = PickBallSpawn(anchorPos, anchorRot, dist, range);
            float clear = float.MaxValue;
            foreach (var a in avoidAll) clear = Mathf.Min(clear, Vector3.Distance(candidate, a));
            if (clear >= controlDistance * 2f) return candidate;
        }
        return SafeNonGoalPos(range, anchorPos);
    }
}
