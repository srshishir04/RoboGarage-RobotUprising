using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;

/* ============================================================================
 * RobotAgent.cs  —  1v1 REDESIGN for real ball control + sim-to-real
 * ----------------------------------------------------------------------------
 * Core change vs the old version:
 *   • The ball is ALWAYS physical and active. It is never deactivated/"carried".
 *   • "Possession" is gone. Instead there is a continuous CONTROL state:
 *       ball within controlDistance AND roughly in front of the robot.
 *   • Reward is driven by the BALL's progress toward the goal (potential-based,
 *     non-farmable), plus a small control + push-alignment bonus. The robot's
 *     own body distance to goal is NOT rewarded.
 *   • Scoring = the BALL enters the goal radius (not the robot).
 *   • Conceding = the BALL enters the OWN goal radius.
 *   • Recovery, anti-stuck, defending (Hard), own-goal prevention (Hard).
 *   • Domain randomization (ball mass/drag, motor torque, obs noise, action
 *     latency) for sim-to-real. Toggled via EnvironmentParameters (curriculum).
 *
 * Action space stays 4 discrete (S/F/L/R) to match the ESP32 firmware.
 * Observation contract: Easy=13, Medium=19, Hard=24 (see CollectObservations).
 * Set Behavior Parameters → Vector Observation Space Size to match the mode.
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

    [Header("Arena size — half side in metres (real arena 1.5m → 0.75)")]
    [SerializeField] private float arenaHalfSize = 0.75f;

    [Header("Interaction thresholds (metres)")]
    [Tooltip("Ball is 'in control' when within this distance AND in front.")]
    [SerializeField] private float controlDistance = 0.20f;
    [Tooltip("Min dot(heading, dirToBall) for control (0.3 ≈ within ~70°).")]
    [SerializeField] private float controlFacingDot = 0.30f;
    [Tooltip("Ball-in-goal radius for scoring/conceding.")]
    [SerializeField] private float goalRadius = 0.20f;

    [Header("Drive settings")]
    [SerializeField] private float motorTorque = 10f;
    [SerializeField] private float brakeTorque = 50f;

    [Header("Sim-to-real velocity scale (m/s that maps to obs = 1.0)")]
    [SerializeField] private float velScale = 1.5f;

    [Header("Domain randomization (sim-to-real)")]
    [SerializeField] private bool randomize = true;
    [SerializeField] private float obsPosNoise = 0.01f;     // metres
    [SerializeField] private int maxActionLatency = 2;    // decision steps

    // ── Reward constants ──────────────────────────────────────────────────────
    private const float R_SCORE = 10.0f;   // ball in scoring goal
    private const float R_CONCEDE = -10.0f;   // ball in own goal
    private const float R_BALL_PROGRESS = 4.0f;   // ball moves toward goal (potential) — raised
    private const float R_APPROACH = 1.2f;   // robot moves toward ball (potential, pre-control)
    private const float R_FACE_BALL = 0.08f;  // turn to FACE the ball (potential) — fixes LEFT-never-used
    private const float R_CONTROL_STEP = 0.002f; // per step while controlling — lowered (was a 'park' incentive)
    private const float R_PUSH_ALIGN = 0.03f;  // per step, behind-ball-toward-goal alignment — raised
    private const float R_IDLE = -0.004f; // per step penalty for STOP while NOT in control — kills parking
    private const float R_STOP_ON_BALL = -0.01f;  // penalty for STOP while in control — must keep pushing
    private const float R_LOST_CONTROL = -0.5f;   // one-time: had control, lost it
    private const float R_STEAL = 1.0f;   // gained control from opponent (Medium+)
    private const float R_CLEAR = 1.0f;   // pushed ball away from own goal (Hard defend)
    private const float R_DEFEND_SHAPING = 2.0f;   // move to intercept point (Hard defend, potential)
    private const float R_OWNGOAL_PUSH = -3.0f;   // controlling + pushing ball toward own goal (Hard)
    private const float R_CORRECT_ROLE = 0.001f; // per step, role-correct half
    private const float STEP_COST = -0.0008f;
    private const float SPIN_PENALTY = -0.002f;
    private const float WALL_HARD = -3.0f;
    private const float WALL_SOFT_MAX = 0.05f;
    private const float WALL_SOFT_FRAC = 0.15f;

    // ── Runtime state ───────────────────────────────────────────────────────
    private Rigidbody rb;
    private Rigidbody ballRb;
    private Renderer agentRenderer;
    private RobotAgent opponentAgent;

    private bool inControl = false;
    private bool hadControlLast = false;
    private Role currentRole = Role.Attacker;

    private float prevBallToGoal;
    private float prevRobotToBall;
    private float prevFaceDot;
    private float prevBallToOwnGoal;
    private float prevInterceptDist;

    private bool oppInControlLast = false;

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

    private static readonly Color C_IDLE = Color.blue;
    private static readonly Color C_CTRL = Color.cyan;
    private static readonly Color C_SCORE = Color.green;
    private static readonly Color C_DEFEND = new Color(0.8f, 0.1f, 0.8f);
    private static readonly Color C_WALL = new Color(1f, 0.5f, 0f);

    public bool CurrentInControl => inControl;
    public Role CurrentRole => currentRole;

    // ── Lifecycle ─────────────────────────────────────────────────────────────
    public override void Initialize()
    {
        agentRenderer = GetComponentInChildren<Renderer>();
        rb = GetComponent<Rigidbody>();
        if (arenaRoot == null) arenaRoot = transform.parent;
        if (ball != null) ballRb = ball.GetComponent<Rigidbody>();
        if (opponent != null) opponentAgent = opponent.GetComponent<RobotAgent>();

        if (ballRb == null) Debug.LogError($"[RobotAgent:{name}] Ball needs a Rigidbody.");
        if (trainingMode >= TrainingMode.Medium && opponent == null)
            Debug.LogWarning($"[RobotAgent:{name}] Medium+ but no opponent assigned.");
        if (trainingMode >= TrainingMode.Hard && ownGoal == null)
            Debug.LogWarning($"[RobotAgent:{name}] Hard but no ownGoal assigned.");

        actionDelayBuffer = new int[maxActionLatency + 1];
    }

    public override void OnEpisodeBegin()
    {
        // Curriculum parameters (defaults if not set by trainer YAML)
        var ep = Academy.Instance.EnvironmentParameters;
        curBallSpawnRadius = ep.GetWithDefault("ball_spawn_radius", 1.0f);
        curOpponentSpeed = ep.GetWithDefault("opponent_speed", 1.0f);
        controlDistance = ep.GetWithDefault("control_distance", controlDistance);

        inControl = false; hadControlLast = false; oppInControlLast = false;
        currentRole = Role.Attacker;
        SetColour(C_IDLE);

        StopWheels();
        if (rb != null) { rb.linearVelocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }

        float spawnRange = arenaHalfSize * 0.72f;

        // AI spawn: Hard starts in own half (negative Z), else anywhere
        Vector3 aiSpawn = trainingMode == TrainingMode.Hard
            ? RandomLocalPos(spawnRange, -1f) : RandomLocalPos(spawnRange);
        transform.localPosition = aiSpawn;
        transform.localRotation = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);

        // Opponent spawn (Medium/Hard)
        if (opponent != null)
        {
            var oppRb = opponent.GetComponent<Rigidbody>();
            if (oppRb != null) { oppRb.linearVelocity = Vector3.zero; oppRb.angularVelocity = Vector3.zero; }
            opponent.localPosition = trainingMode == TrainingMode.Hard
                ? RandomLocalPos(spawnRange, +1f)
                : SafeLocalPos(new[] { aiSpawn }, controlDistance * 4f, spawnRange);
            opponent.localRotation = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
            var so = opponent.GetComponent<ScriptedOpponent>();
            if (so != null) so.SetSpeedMultiplier(curOpponentSpeed);
        }

        // Ball spawn — curriculum shrinks the spread (Easy starts ball near goal)
        if (ball != null)
        {
            float r = spawnRange * Mathf.Clamp01(curBallSpawnRadius);
            Vector3 bias = (goal != null && curBallSpawnRadius < 1f)
                ? Vector3.Lerp(Vector3.zero, goal.localPosition, 0.5f) : Vector3.zero;
            Vector3 bSpawn = SafeLocalPos(new[] { aiSpawn }, controlDistance * 2f, r) + bias;
            bSpawn.x = Mathf.Clamp(bSpawn.x, -spawnRange, spawnRange);
            bSpawn.z = Mathf.Clamp(bSpawn.z, -spawnRange, spawnRange);
            bSpawn.y = ball.localPosition.y;
            ball.localPosition = bSpawn;
            if (ballRb != null) { ballRb.linearVelocity = Vector3.zero; ballRb.angularVelocity = Vector3.zero; }

            // Domain randomization of ball physics
            if (randomize && ballRb != null)
            {
                ballRb.mass = Random.Range(0.04f, 0.09f);
                ballRb.linearDamping = Random.Range(0.2f, 0.6f);
            }
        }

        // Domain randomization of drive + action latency
        actionLatency = randomize ? Random.Range(0, maxActionLatency + 1) : 0;
        // Allocate defensively: OnEpisodeBegin can run before Initialize() on some
        // reset orderings (common with many parallel arenas), so don't assume it exists.
        if (actionDelayBuffer == null || actionDelayBuffer.Length != maxActionLatency + 1)
            actionDelayBuffer = new int[Mathf.Max(1, maxActionLatency + 1)];
        for (int i = 0; i < actionDelayBuffer.Length; i++) actionDelayBuffer[i] = 0;

        // Shaping baselines
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
        lastStuckCheckPos = transform.localPosition;
        stuckCheckCounter = 0;
    }

    // ── Observations ───────────────────────────────────────────────────────────
    public override void CollectObservations(VectorSensor sensor)
    {
        float n = arenaHalfSize;
        Vector3 lp = transform.localPosition;

        // [0-1] robot pos
        sensor.AddObservation(NoisyN(lp.x, n));
        sensor.AddObservation(NoisyN(lp.z, n));
        // [2-3] heading
        sensor.AddObservation(transform.forward.x);
        sensor.AddObservation(transform.forward.z);

        // [4-6] ball relative + [7-8] ball velocity
        Vector3 bRel = SafeVec(ball, lp);
        sensor.AddObservation(Clamp2(NoisyVal(bRel.x) / n));
        sensor.AddObservation(Clamp2(NoisyVal(bRel.z) / n));
        sensor.AddObservation(Mathf.Clamp(bRel.magnitude / n, 0f, 2f));
        Vector3 bVel = ballRb != null ? ballRb.linearVelocity : Vector3.zero;
        sensor.AddObservation(Mathf.Clamp(bVel.x / velScale, -2f, 2f));
        sensor.AddObservation(Mathf.Clamp(bVel.z / velScale, -2f, 2f));

        // [9-11] goal relative
        Vector3 gRel = SafeVec(goal, lp);
        sensor.AddObservation(Clamp2(gRel.x / n));
        sensor.AddObservation(Clamp2(gRel.z / n));
        sensor.AddObservation(Mathf.Clamp(gRel.magnitude / n, 0f, 2f));

        // [12] in-control
        sensor.AddObservation(inControl ? 1f : 0f);

        // Medium+ : opponent
        if (trainingMode >= TrainingMode.Medium)
        {
            if (opponent != null)
            {
                Vector3 oRel = opponent.localPosition - lp;
                sensor.AddObservation(Clamp2(oRel.x / n));            // [13]
                sensor.AddObservation(Clamp2(oRel.z / n));            // [14]
                sensor.AddObservation(opponent.forward.x);           // [15]
                sensor.AddObservation(opponent.forward.z);           // [16]
                sensor.AddObservation(Mathf.Clamp(oRel.magnitude / n, 0f, 2f)); // [17]
                sensor.AddObservation(OpponentInControl() ? 1f : 0f);// [18]
            }
            else for (int i = 0; i < 6; i++) sensor.AddObservation(0f);
        }

        // Hard : own goal + role + threat
        if (trainingMode >= TrainingMode.Hard)
        {
            Vector3 ogRel = SafeVec(ownGoal, lp);
            sensor.AddObservation(Clamp2(ogRel.x / n));              // [19]
            sensor.AddObservation(Clamp2(ogRel.z / n));              // [20]
            sensor.AddObservation(Mathf.Clamp(ogRel.magnitude / n, 0f, 2f)); // [21]
            sensor.AddObservation(currentRole == Role.Defender ? 1f : 0f);   // [22]
            float bToOwn = (ball != null && ownGoal != null)
                ? LocalDist(ball.localPosition, ownGoal.localPosition) : 2f * n;
            sensor.AddObservation(Mathf.Clamp(bToOwn / n, 0f, 2f));  // [23]
        }
    }

    // ── Actions ──────────────────────────────────────────────────────────────
    public override void OnActionReceived(ActionBuffers actions)
    {
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

        // Wall
        if (IsOutOfBounds())
        {
            AddReward(WALL_HARD); SetColour(C_WALL);
            StopWheels(); EndEpisode(); return;
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

        // ── Update role (Hard, dynamic in 1v1) ─────────────────────────────
        if (trainingMode >= TrainingMode.Hard) UpdateRole();

        // ── Control detection (continuous) ─────────────────────────────────
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
        // This is the fix for "only ever turns RIGHT": turning toward the ball
        // (left OR right, whichever reduces the angle) now pays.
        AddReward((facing - prevFaceDot) * R_FACE_BALL);
        prevFaceDot = facing;

        // ── Approach shaping (pre-control, potential-based) ─────────────────
        if (!inControl)
        {
            AddReward((prevRobotToBall - dBall) * R_APPROACH);
            // Idle penalty: standing still while not on the ball must NOT be safe.
            if (applied == 0) AddReward(R_IDLE);
            SetColour(C_IDLE);
        }
        prevRobotToBall = dBall;

        // ── Ball-to-goal progress (the main signal, potential-based) ────────
        float bToGoal = LocalDist(ball.localPosition, goal.localPosition);
        AddReward((prevBallToGoal - bToGoal) * R_BALL_PROGRESS);
        prevBallToGoal = bToGoal;

        // ── Control + push-alignment bonus ──────────────────────────────────
        if (inControl)
        {
            SetColour(C_CTRL);
            AddReward(R_CONTROL_STEP);
            // Must keep pushing — parking on the ball is penalised.
            if (applied == 0) AddReward(R_STOP_ON_BALL);
            // behind the ball, pushing toward goal: dot(robot->ball, ball->goal)
            Vector3 rToB = (ball.position - transform.position); rToB.y = 0f;
            Vector3 bToG = (goal.position - ball.position); bToG.y = 0f;
            if (rToB.sqrMagnitude > 1e-4f && bToG.sqrMagnitude > 1e-4f)
                AddReward(R_PUSH_ALIGN * Mathf.Max(0f, Vector3.Dot(rToB.normalized, bToG.normalized)));
        }

        // ── Hard: defending, own-goal prevention, clearance ─────────────────
        if (trainingMode >= TrainingMode.Hard && ownGoal != null)
        {
            float bToOwn = LocalDist(ball.localPosition, ownGoal.localPosition);

            if (currentRole == Role.Defender && !inControl)
            {
                SetColour(C_DEFEND);
                float dInt = ComputeInterceptDist();
                AddReward((prevInterceptDist - dInt) * R_DEFEND_SHAPING);
                prevInterceptDist = dInt;
            }
            else prevInterceptDist = ComputeInterceptDist();

            // Own-goal prevention: controlling AND pushing ball toward own goal
            if (inControl && bToOwn < prevBallToOwnGoal)
                AddReward(R_OWNGOAL_PUSH * (prevBallToOwnGoal - bToOwn));
            // Clearance: ball moving AWAY from own goal while we're defending half
            if (transform.localPosition.z < 0f && bToOwn > prevBallToOwnGoal)
                AddReward(R_CLEAR * (bToOwn - prevBallToOwnGoal));
            prevBallToOwnGoal = bToOwn;

            // Correct-zone tiny reward
            bool zoneOK = currentRole == Role.Attacker ? transform.localPosition.z > 0f
                                                       : transform.localPosition.z < 0f;
            if (zoneOK) AddReward(R_CORRECT_ROLE);

            // Concede: ball in own goal
            if (bToOwn < goalRadius)
            {
                AddReward(R_CONCEDE); StopWheels(); EndEpisode(); return;
            }
        }

        // ── Score: BALL in scoring goal ─────────────────────────────────────
        if (bToGoal < goalRadius)
        {
            float timeBonus = 0.5f * (1f - (float)StepCount / MaxStep);
            AddReward(R_SCORE + timeBonus);
            SetColour(C_SCORE); StopWheels(); EndEpisode(); return;
        }

        // ── Anti-stuck ───────────────────────────────────────────────────────
        if (++stuckCheckCounter >= STUCK_CHECK_INTERVAL)
        {
            float moved = LocalDist(transform.localPosition, lastStuckCheckPos);
            if (moved < STUCK_MOVE_MIN) AddReward(-0.05f);  // nudge away from idling
            lastStuckCheckPos = transform.localPosition;
            stuckCheckCounter = 0;
        }

        hadControlLast = inControl;
        oppInControlLast = (trainingMode >= TrainingMode.Medium) && OpponentInControl();
    }

    // ── Role logic (Hard 1v1, dynamic) ────────────────────────────────────────
    private void UpdateRole()
    {
        // Defend when the ball is in our half (negative Z) or the opponent controls it.
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

    private bool OpponentInControl()
    {
        if (opponent == null || ball == null) return false;
        if (opponentAgent != null) return opponentAgent.CurrentInControl;
        var so = opponent.GetComponent<ScriptedOpponent>();
        if (so != null) return so.CurrentHasBall;
        return LocalDist(opponent.localPosition, ball.localPosition) < controlDistance;
    }

    // ── Heuristic (keyboard test) ──────────────────────────────────────────────
    public override void Heuristic(in ActionBuffers actionsOut)
    {
        var d = actionsOut.DiscreteActions;
        d[0] = 0;
        if (Input.GetKey(KeyCode.W)) d[0] = 1;
        if (Input.GetKey(KeyCode.A)) d[0] = 2;
        if (Input.GetKey(KeyCode.D)) d[0] = 3;
    }

    // ── Drive (in-place turns — matches ESP32 firmware) ────────────────────────
    private void ApplyDrive(int act)
    {
        if (frontLeftWheel == null || frontRightWheel == null ||
            rearLeftWheel == null || rearRightWheel == null) return;
        switch (act)
        {
            case 1: SetAllWheels(motorTorque); break;
            case 2:
                frontLeftWheel.motorTorque = -motorTorque; rearLeftWheel.motorTorque = -motorTorque;
                frontRightWheel.motorTorque = motorTorque; rearRightWheel.motorTorque = motorTorque;
                SetAllBrakes(0f); break;
            case 3:
                frontLeftWheel.motorTorque = motorTorque; rearLeftWheel.motorTorque = motorTorque;
                frontRightWheel.motorTorque = -motorTorque; rearRightWheel.motorTorque = -motorTorque;
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

    // ── Helpers ───────────────────────────────────────────────────────────────
    private bool IsOutOfBounds()
    {
        float ax = Mathf.Abs(transform.localPosition.x);
        float az = Mathf.Abs(transform.localPosition.z);
        return ax > arenaHalfSize || az > arenaHalfSize;
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
    private void SetColour(Color c) { if (agentRenderer != null) agentRenderer.material.color = c; }
}