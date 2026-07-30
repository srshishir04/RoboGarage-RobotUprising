using UnityEngine;
using UnityEngine.Serialization;

/* ── Scripted opponent — progressive sparring partner ─────────────────────
 * The robot behaves like:
 * Go to ball (or its predicted intercept point, at higher skill)
 * Pick ball
 * Go to goal and score (aim gets more precise at higher skill)
 * Repeat
 *
 * REDESIGN vs the previous version — WHY:
 *   Diagnosed earlier: the previous "wrong goal" report was NOT a code bug —
 *   `targetGoal` (renamed from `scoringGoal`, see FormerlySerializedAs below
 *   so existing Inspector wiring migrates automatically) was and is correctly
 *   wired to the AI's DEFENDED goal, which is exactly where this opponent is
 *   supposed to score (that's how it scores AGAINST the AI). Renamed only for
 *   clarity; the wiring/behaviour is unchanged.
 *
 *   What DID change: difficulty used to be 3 fixed presets (HESITATION_CHANCE /
 *   SPEED_MULT indexed by enum) — a training partner that's either a pushover
 *   or a wall, with nothing in between. Added a continuous `skillLevel` (0..1)
 *   axis, orthogonal to the existing speed multiplier (curriculum's
 *   "opponent_speed" is unchanged), driven by a new "opponent_skill" curriculum
 *   parameter (see RobotAgent.ApplyOpponentSkillCurriculum). Skill blends THREE
 *   behaviours continuously instead of jumping between 3 presets:
 *     - hesitation chance (unchanged mechanic, now continuous)
 *     - interception: at low skill, chases the ball's CURRENT position; at high
 *       skill, leads it — aims at a predicted point ahead of the ball's motion,
 *       so a fast learner faces genuine anticipation, not just a faster chaser.
 *     - aim precision: at low skill, its shot has a lateral jitter (rolled once
 *       per possession, not per-frame, so it doesn't look twitchy); at high
 *       skill the jitter shrinks to ~0, i.e. it shoots straight.
 *   This gives the RL agent a partner that can be curriculum-ramped smoothly
 *   just above its current skill, instead of stepping between 3 discrete rungs.
 * ───────────────────────────────────────────────────────────────────────── */

// This tells unity that this gameobject must have rigidbody. If missing, unity automatically adds one
[RequireComponent(typeof(Rigidbody))]
public class ScriptedOpponent : MonoBehaviour
{
    // =========================================================================
    //  ENUMS
    // =========================================================================

    public enum Difficulty
    {
        Easy = 0,
        Medium = 1,
        Hard = 2,
    }

    // This stores current robot behaviour
    private enum State { SeekBall, CarryBall, Cooldown }

    // =========================================================================
    //  INSPECTOR
    // =========================================================================

    [Header("Arena references — drag from hierarchy")]
    [SerializeField] private Transform ballA;
    [SerializeField] private Transform ballB;

    [Tooltip("The goal this opponent scores IN — the AI's DEFENDED goal (OwnGoal). " +
             "This is correct and intentional: the opponent scoring here IS it scoring " +
             "against the AI. Renamed from 'scoringGoal' for clarity only — existing " +
             "Inspector references migrate automatically via FormerlySerializedAs.")]
    [FormerlySerializedAs("scoringGoal")]
    [SerializeField] private Transform targetGoal;

    [Tooltip("Parent arena object. Used for local-space position resets.")]
    [SerializeField] private Transform arenaRoot;

    [Header("Arena size — must match RobotAgent exactly")]
    [SerializeField] private float arenaHalfSize = 0.75f;

    [Header("Interaction thresholds (metres — match RobotAgent)")]
    [SerializeField] private float pickupDistance = 0.25f;
    [SerializeField] private float goalDistance = 0.30f;

    [Header("Movement")]
    [Tooltip("Base movement speed in m/s. Scaled by difficulty tier and the speed multiplier.")]
    [SerializeField] private float baseSpeed = 1.2f;

    [Tooltip("How fast the opponent turns toward its target (degrees/second).")]
    [SerializeField] private float turnSpeed = 180f;

    [Header("Opponent difficulty (speed tier + default skill baseline)")]
    [SerializeField] public Difficulty opponentDifficulty = Difficulty.Medium;

    [Header("Skill (0=pushover, 1=sharp) — continuous, curriculum-ramped via SetSkill()")]
    [Tooltip("Orthogonal to speed. Blends hesitation, ball-interception lead, and shot " +
             "aim precision. Defaults from opponentDifficulty; overridden at runtime by " +
             "RobotAgent's 'opponent_skill' curriculum parameter, if present.")]
    [SerializeField, Range(0f, 1f)] private float skillLevel = 0.5f;

    [Header("Colour feedback (optional — assign a Renderer child)")]
    [SerializeField] private Renderer bodyRenderer;

    // =========================================================================
    //  CONSTANTS
    // =========================================================================

    // Continuous skill blending ranges (replaces the old fixed 3-value arrays).
    private const float HESITATION_MAX = 0.45f;   // skill = 0
    private const float HESITATION_MIN = 0.05f;   // skill = 1
    private const float LEAD_TIME_MAX = 0.45f;   // seconds of ball-velocity lookahead at skill = 1
    private const float AIM_JITTER_MAX = 0.22f;   // metres of lateral shot jitter at skill = 0

    // Default skill baseline per difficulty tier, used when nothing has called
    // SetSkill() yet (e.g. manual Play-mode testing without a curriculum).
    private static readonly float[] DEFAULT_SKILL_BY_DIFFICULTY = { 0.15f, 0.50f, 0.85f };

    // Speed multiplier per difficulty (unchanged mechanic — orthogonal to skill)
    private static readonly float[] SPEED_MULT = { 0.70f, 1.00f, 1.30f };
    private float _speedMul = 1f;
    public void SetSpeedMultiplier(float m) { _speedMul = Mathf.Max(0.1f, m); }

    // Continuous skill setter — called by RobotAgent's opponent_skill curriculum.
    // Clamped defensively since a bad/missing curriculum value must never crash training.
    public void SetSkill(float s) { skillLevel = Mathf.Clamp01(s); }

    // Wait after scoring (seconds)
    private const float COOLDOWN_DURATION = 1.2f;

    // Colours
    private static readonly Color C_SEEK = new Color(0.8f, 0.2f, 0.2f);   // red  — seeking
    private static readonly Color C_CARRY = new Color(1.0f, 0.6f, 0.0f);   // amber — carrying
    private static readonly Color C_COOL = new Color(0.5f, 0.5f, 0.5f);   // grey  — cooldown

    // =========================================================================
    //  RUNTIME STATE
    // =========================================================================

    private Rigidbody rb;
    private State state = State.SeekBall; // Robot starts by searching ball
    private Transform carriedBall = null; // Stores which ball robot currently holds
    private bool hasBall = false;
    private float cooldownTimer = 0f; // Counts waiting time
    private bool hesitatingNow = false;   // Stores whether robot pauses this frame
    private Vector3 aimJitterOffset = Vector3.zero;   // rolled once per possession (see PickUp)

    // =========================================================================
    //  PUBLIC API  (read by MatchManager)
    // =========================================================================

    // True when the opponent is carrying a ball
    public bool CurrentHasBall => hasBall;

    // =========================================================================
    //  LIFECYCLE
    // =========================================================================

    // Runs before start
    private void Awake()
    {
        rb = GetComponent<Rigidbody>();
        rb.constraints = RigidbodyConstraints.FreezeRotationX
                       | RigidbodyConstraints.FreezeRotationZ
                       | RigidbodyConstraints.FreezePositionY;
        // Seed skill from the difficulty tier so manual testing without a
        // curriculum still gets a sensible default (curriculum overrides via SetSkill).
        skillLevel = DEFAULT_SKILL_BY_DIFFICULTY[(int)opponentDifficulty];
    }

    private void Start()
    {
        ValidateSetup(); // Check if inspector variable ae assigned
        SetColour(C_SEEK); // Robot starts red
    }

    private void ValidateSetup()
    {
        if (ballA == null || ballB == null)
            Debug.LogError("[ScriptedOpponent] BallA or BallB not assigned.");
        if (targetGoal == null)
            Debug.LogError("[ScriptedOpponent] targetGoal not assigned. " +
                           "Drag OwnGoal (the AI's defended goal) here.");
        if (arenaRoot == null)
        {
            arenaRoot = transform.parent;
            Debug.LogWarning("[ScriptedOpponent] arenaRoot not assigned — using parent.");
        }
    }

    // =========================================================================
    //  FIXED UPDATE — Physics update loop
    // =========================================================================

    private void FixedUpdate()
    {
        // If hesitation chance is greater than random valye, then robot pauses. This creates imperfect human like movement
        hesitatingNow = Random.value < HesitationChance();

        switch (state)
        {
            case State.SeekBall: TickSeek(); break;
            case State.CarryBall: TickCarry(); break;
            case State.Cooldown: TickCooldown(); break;
        }
    }

    // ── State: Robot searching for ball ───────────────────────────────────────────────────────

    private void TickSeek()
    {
        Transform target = NearestActiveBall();

        // No active balls — wait (this happens briefly after scoring while
        // RobotAgent's episode resets and balls reactivate)
        if (target == null) return;

        if (!hesitatingNow)
            MoveToward(InterceptPoint(target));

        // Pickup check (if close enuogh, pick ball) — always against the ball's REAL
        // position, never the predicted point (prediction only steers movement).
        float dist = Vector3.Distance(transform.position, target.position);
        if (dist < pickupDistance)
        {
            PickUp(target);
        }
    }

    // Predicted lead point for the ball, blended by skill. At skill=0 this is
    // just the ball's current position (old behaviour, chases where it IS);
    // at skill=1 it leads by up to LEAD_TIME_MAX seconds of current velocity,
    // so a sharp opponent cuts off a moving ball instead of trailing it.
    private Vector3 InterceptPoint(Transform ballTf)
    {
        var ballRb = ballTf.GetComponent<Rigidbody>();
        if (ballRb == null) return ballTf.position;
        float leadTime = LEAD_TIME_MAX * skillLevel;
        Vector3 predicted = ballTf.position + ballRb.linearVelocity * leadTime;
        predicted.y = ballTf.position.y;
        return predicted;
    }

    // ── State: CarryBall ─────────────────────────────────────────────────────

    private void TickCarry()
    {
        if (targetGoal == null) return;

        if (!hesitatingNow)
            MoveToward(targetGoal.position + aimJitterOffset);

        // Score check — against the REAL goal position, jitter only steers aim.
        float dist = Vector3.Distance(transform.position, targetGoal.position);
        if (dist < goalDistance)
        {
            Score();
        }
    }

    // ── State: Cooldown ───────────────────────────────────────────────────────

    private void TickCooldown()
    {
        cooldownTimer -= Time.fixedDeltaTime;
        if (cooldownTimer <= 0f)
        {
            state = State.SeekBall;
            SetColour(C_SEEK);
        }
    }

    // =========================================================================
    //  ACTIONS
    // =========================================================================

    private void PickUp(Transform ball)
    {
        carriedBall = ball;
        hasBall = true;

        // Deactivate the ball — exactly what RobotAgent does on pickup.
        // This means RobotAgent's NearerBall() will skip this ball,
        // forcing the AI to either go for the other ball or defend.
        carriedBall.gameObject.SetActive(false);

        // Roll this possession's aim jitter ONCE (not per-frame — a jittery
        // target every physics step looks twitchy and doesn't teach anything).
        // Shrinks to ~0 as skill approaches 1 (Lerp naturally handles skill=1 => 0).
        float jitterMag = Mathf.Lerp(AIM_JITTER_MAX, 0f, skillLevel);
        Vector3 lateral = Vector3.Cross(Vector3.up, (targetGoal != null
            ? (targetGoal.position - transform.position).normalized : transform.forward));
        aimJitterOffset = lateral * Random.Range(-jitterMag, jitterMag);

        state = State.CarryBall;
        SetColour(C_CARRY);
    }

    private void Score()
    {
        hasBall = false;

        // Reactivate the carried ball at a random arena position.
        // Do NOT reactivate it at the goal — spawn it somewhere neutral.
        if (carriedBall != null)
        {
            carriedBall.localPosition = RandomArenaPos(avoidPos: transform.localPosition);
            carriedBall.gameObject.SetActive(true);
            carriedBall = null;
        }

        // Reactivate the other ball too if it somehow got deactivated
        // (edge case: AI picked it up and scored in the same tick)
        Transform other = NearestActiveBall();
        if (other == null)
        {
            // Both balls inactive — reactivate the other one
            ReactivateOtherBall();
        }

        state = State.Cooldown;
        cooldownTimer = COOLDOWN_DURATION;
        SetColour(C_COOL);
    }

    // =========================================================================
    //  MOVEMENT
    // =========================================================================

    private void MoveToward(Vector3 worldTarget)
    {
        // Rotate to face target
        Vector3 dir = (worldTarget - transform.position);
        dir.y = 0f;
        if (dir.sqrMagnitude < 0.001f) return;

        Quaternion targetRot = Quaternion.LookRotation(dir.normalized);
        transform.rotation = Quaternion.RotateTowards(
            transform.rotation, targetRot,
            turnSpeed * Time.fixedDeltaTime);

        // Move forward in current facing direction
        float speed = baseSpeed * SpeedMult();
        rb.MovePosition(transform.position + transform.forward * speed * Time.fixedDeltaTime);
    }

    // =========================================================================
    //  HELPERS
    // =========================================================================

    // Chooses closest active ball
    private Transform NearestActiveBall()
    {
        bool aOn = ballA != null && ballA.gameObject.activeSelf;
        bool bOn = ballB != null && ballB.gameObject.activeSelf;

        if (!aOn && !bOn) return null;
        if (!aOn) return ballB;
        if (!bOn) return ballA;

        float dA = Vector3.Distance(transform.position, ballA.position);
        float dB = Vector3.Distance(transform.position, ballB.position);
        return dA <= dB ? ballA : ballB;
    }

    private void ReactivateOtherBall()
    {
        // Find whichever ball is not the one we just scored with
        Transform other = (carriedBall == ballA) ? ballB : ballA;
        if (other != null && !other.gameObject.activeSelf)
        {
            other.localPosition = RandomArenaPos(avoidPos: transform.localPosition);
            other.gameObject.SetActive(true);
        }
    }

    // Returns a random local-space position within the arena,
    // ensuring minimum clearance from avoidPos.
    private Vector3 RandomArenaPos(Vector3 avoidPos, float minClearance = 0.3f)
    {
        float y = transform.localPosition.y;

        for (int i = 0; i < 100; i++)
        {
            float x = Random.Range(-arenaHalfSize * 0.8f, arenaHalfSize * 0.8f);
            float z = Random.Range(-arenaHalfSize * 0.8f, arenaHalfSize * 0.8f);
            Vector3 candidate = new Vector3(x, y, z);
            if (Vector3.Distance(candidate, avoidPos) >= minClearance)
                return candidate;
        }

        // Fallback: arena centre
        return new Vector3(0f, y, 0f);
    }

    private float HesitationChance() => Mathf.Lerp(HESITATION_MAX, HESITATION_MIN, skillLevel);
    private float SpeedMult() => SPEED_MULT[(int)opponentDifficulty] * _speedMul;

    private void SetColour(Color c)
    {
        if (bodyRenderer != null)
            bodyRenderer.material.color = c;
    }

    // =========================================================================
    //  GIZMOS — visualise pickup / goal ranges in Scene view (Debugging)
    // =========================================================================

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        // Pickup range
        Gizmos.color = new Color(1f, 0.5f, 0f, 0.3f);
        Gizmos.DrawSphere(transform.position, pickupDistance);

        // Goal range (shown at target-goal position) — labelled so it's unmistakable
        // in the Scene view which goal this opponent is driving toward.
        if (targetGoal != null)
        {
            Gizmos.color = new Color(0.8f, 0.1f, 0.1f, 0.3f);
            Gizmos.DrawSphere(targetGoal.position, goalDistance);
            UnityEditor.Handles.Label(targetGoal.position + Vector3.up * 0.1f, "opponent scores here");
        }
    }
#endif
}
