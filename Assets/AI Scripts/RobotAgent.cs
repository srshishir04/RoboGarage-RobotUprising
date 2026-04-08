using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;
public class RobotAgent : Agent
{
    [SerializeField] private Transform _goal;
    [SerializeField] private float _moveSpeed = 1.5f;
    [SerializeField] private float _rotationSpeed = 180f;

    private Renderer _renderer; // It will change the color of the robot when it will collide

    [HideInInspector] public int CurrentEpisode = 0;
    [HideInInspector] public float CumulativeReward = 0f; // Track the total reawrd the robot has earned in each episode

    public override void Initialize()
    {
        Debug.Log("Initialize(");
        _renderer = GetComponent<Renderer>();
        CurrentEpisode = 0;
        CumulativeReward = 0f;
    }
    public override void OnEpisodeBegin()
    {
        Debug.Log("OnEpisodeBegin()");
        CurrentEpisode++;
        CumulativeReward = 0f;
        _renderer.material.color = Color.blue;

        SpawnObjects(); // Reposition the robot and goal so that each episode start with a slightly different arrangements
    }
    private void SpawnObjects()
    {
        transform.localRotation = Quaternion.identity;
        transform.localPosition = new Vector3(0f, 0.07f, 0f);

        // Randomize the direction on the Y-axis (angle in degrees) of the goal
        float randomAngle = Random.Range(0f, 360f);
        Vector3 randomDirection = Quaternion.Euler(0f, randomAngle, 0f) * Vector3.forward;

        // Randomize the distance within the range [1, 2.5] of the goal
        float randomDistance = Random.Range(0.05f, 0.12f);

        // Calculate the goal's position,
        Vector3 goalPosition = transform.localPosition + randomDirection * randomDistance;

        // Apply the calculated position to the goal, place the goal in the randomized position
        _goal.localPosition = new Vector3(goalPosition.x, 0.01f, goalPosition.z);
    }
    public override void CollectObservations(VectorSensor sensor)
    {
        // The Goal's position
        float goalPosX_normalized = _goal.localPosition.x / 0.15f;
        float goalPosZ_normalized = _goal.localPosition.z / 0.15f;
        // The Turtle's position
        float turtlePosX_normalized = transform.localPosition.x / 0.15f;
        //float turtlePosY_normalized = transform.localPosition.y / 0.15f;
        float turtlePosZ_normalized = transform.localPosition.z / 0.15f;
        // The turtle's direction
        float turtleRotation_normalized = (transform.localRotation.eulerAngles.y / 360f) * 2f - 1f;
        sensor.AddObservation(goalPosX_normalized);
        sensor.AddObservation(goalPosZ_normalized);
        sensor.AddObservation(turtlePosX_normalized);
        sensor.AddObservation(turtlePosZ_normalized);
        sensor.AddObservation(turtleRotation_normalized);
    }

    /*public override void Heuristic(in ActionBuffers actionsOut)
    {
        var discreteActionsOut = actionsOut.DiscreteActions;

        discreteActionsOut[0] = 0;

        if (Input.GetKey(KeyCode.UpArrow))
        {
            discreteActionsOut[0] = 1;
        }
        else if (Input.GetKey(KeyCode.DownArrow))
        {
            discreteActionsOut[0] = -1;
        }
        else if (Input.GetKey(KeyCode.LeftArrow))
        {
            discreteActionsOut[0] = 2;
        }
        else if (Input.GetKey(KeyCode.RightArrow))
        {
            discreteActionsOut[0] = 3;
        }
    }*/
    public override void OnActionReceived(ActionBuffers actions)
    {
        // Move the agent using the action
        MoveAgent(actions.DiscreteActions);
        // Penalty given each step to encourage agent to finish task quickly
        AddReward(-2f / MaxStep);
        // Update the cumulative reward after adding the step penalty
        CumulativeReward = GetCumulativeReward();
    }
    public void MoveAgent(ActionSegment<int> act)
    {
        var action = act[0];
        switch (action)
        {
            case 1: // Move forward
                transform.position += transform.forward * _moveSpeed * Time.deltaTime;
                break;
            case 2: // Rotate left
                transform.Rotate(0f, -_rotationSpeed * Time.deltaTime, 0f);
                break;
            case 3: // Rotate right
                transform.Rotate(0f, _rotationSpeed * Time.deltaTime, 0f);
                break;
        }
    }
    private void OnTriggerEnter(Collider other)
    {
        if (other.gameObject.CompareTag("Goal"))
        {
            GoalReached();
        }
    }
    private void GoalReached()
    {
        AddReward(1.0f); // Large reward for reaching the goal
        CumulativeReward = GetCumulativeReward();
        EndEpisode();
    }
    private void OnCollisionEnter(Collision collision)
    {
        if (collision.gameObject.CompareTag("Wall"))
        {
            // Apply a small negative reward
            AddReward(-0.05f);
            // Change the color of the TurtleAgent to red
            if (_renderer != null)
            {
                _renderer.material.color = Color.red;
            }
        }
    }
    private void OnCollisionStay(Collision collision)
    {
        if (collision.gameObject.CompareTag("Wall"))
        {
            // Continually penalize the agent while it is in contact with the wall
            AddReward(-0.01f * Time.fixedDeltaTime);
        }
    }
    private void OnCollisionExit(Collision collision)
    {
        if (collision.gameObject.CompareTag("Wall"))
        {
            // Reset the color when collision ends
            if (_renderer != null)
            {
                // Blue is the default color
                _renderer.material.color = Color.blue;
            }
        }
    }
}