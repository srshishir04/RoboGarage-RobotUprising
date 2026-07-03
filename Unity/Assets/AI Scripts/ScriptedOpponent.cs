using UnityEngine;

/* ── Fake AI opponent robot ────────-
 * The robot behaves like:
 * Go to ball
 * Pick ball
 * Go to goal and score
 * Repeat
 * ───────────────────────────────────── */

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

    [Tooltip("The goal this opponent scores IN — which is the AI's defended goal (OwnGoal).")]
    [SerializeField] private Transform scoringGoal;

    [Tooltip("Parent arena object. Used for local-space position resets.")]
    [SerializeField] private Transform arenaRoot;

    [Header("Arena size — must match RobotAgent exactly")]
    [SerializeField] private float arenaHalfSize = 0.75f;

    [Header("Interaction thresholds (metres — match RobotAgent)")]
    [SerializeField] private float pickupDistance = 0.25f;
    [SerializeField] private float goalDistance = 0.30f;

    [Header("Movement")]
    [Tooltip("Base movement speed in m/s. Scaled by difficulty.")]
    [SerializeField] private float baseSpeed = 1.2f;

    [Tooltip("How fast the opponent turns toward its target (degrees/second).")]
    [SerializeField] private float turnSpeed = 180f;

    [Header("Opponent difficulty")]
    [SerializeField] public Difficulty opponentDifficulty = Difficulty.Medium;

    [Header("Colour feedback (optional — assign a Renderer child)")]
    [SerializeField] private Renderer bodyRenderer;

    // =========================================================================
    //  CONSTANTS
    // =========================================================================

    // This array stores pause chance
    private static readonly float[] HESITATION_CHANCE = { 0.40f, 0.25f, 0.10f };

    // Speed multiplier per difficulty
    private static readonly float[] SPEED_MULT = { 0.70f, 1.00f, 1.30f };
    private float _speedMul = 1f;
    public void SetSpeedMultiplier(float m) { _speedMul = Mathf.Max(0.1f, m); }

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
        if (scoringGoal == null)
            Debug.LogError("[ScriptedOpponent] scoringGoal not assigned. " +
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
            MoveToward(target.position);

        // Pickup check (if close enuogh, pick ball)
        float dist = Vector3.Distance(transform.position, target.position);
        if (dist < pickupDistance)
        {
            PickUp(target);
        }
    }

    // ── State: CarryBall ─────────────────────────────────────────────────────

    private void TickCarry()
    {
        if (scoringGoal == null) return;

        if (!hesitatingNow)
            MoveToward(scoringGoal.position);

        // Score check
        float dist = Vector3.Distance(transform.position, scoringGoal.position);
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

        state = State.CarryBall;
        SetColour(C_CARRY);

        Debug.Log("[ScriptedOpponent] Picked up ball.");
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

        Debug.Log("[ScriptedOpponent] Scored!");
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

    private float HesitationChance() => HESITATION_CHANCE[(int)opponentDifficulty]; // Returns hesitation based on difficulty
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

        // Goal range (shown at scoring goal position)
        if (scoringGoal != null)
        {
            Gizmos.color = new Color(0.8f, 0.1f, 0.1f, 0.3f);
            Gizmos.DrawSphere(scoringGoal.position, goalDistance);
        }
    }
#endif
}