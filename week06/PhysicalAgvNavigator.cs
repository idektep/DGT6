using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// Reads the existing Route corners, plans the full path first,
// then sends one rotate/forward command at a time to AIRAV-1.
public class PhysicalAgvNavigator : MonoBehaviour
{
    [System.Serializable]
    private class MotionCommand
    {
        public string command;
        public float value;

        public MotionCommand(string newCommand, float newValue)
        {
            command = newCommand;
            value = newValue;
        }
    }

    [Header("References")]
    public AgvMqttBridge bridge;
    public Transform agvTransform;
    public Transform route;

    [Header("Movement Setting")]
    [Min(5f)] public float stepDistanceMM = 50f;
    [Min(1f)] public float minimumMoveMM = 5f;
    [Min(1f)] public float maximumTurnStepDegree = 90f;
    [Min(0.1f)] public float minimumTurnDegree = 1f;

    [Header("Command Timing")]
    [Min(1f)] public float commandTimeoutSecond = 15f;
    [Min(0f)] public float delayBetweenCommands = 0.15f;

    [Header("Live Data - Read Only")]
    public string navigationState = "IDLE";
    public string currentCommand = "-";
    public int currentCommandNumber;
    public int plannedCommandCount;

    public bool Arrived { get; private set; } = true;
    public bool Faulted { get; private set; }
    public bool IsNavigating { get; private set; }

    private readonly List<MotionCommand> commands =
        new List<MotionCommand>();

    private Coroutine navigationRoutine;

    void Awake()
    {
        if (agvTransform == null)
            agvTransform = transform;
    }

    // Use when the real robot is manually placed at a station before Play.
    // The Unity model is aligned to the clockwise direction of the route.
    public void SetModelAtRoutePoint(Transform routePoint)
    {
        if (routePoint == null || route == null || route.childCount < 2)
            return;

        int side = SideOf(routePoint.position);
        Vector3 direction = Corner(side + 1) - Corner(side);
        direction.y = 0f;

        Vector3 position = routePoint.position;
        position.y = agvTransform.position.y;
        agvTransform.position = position;

        if (direction.sqrMagnitude > 0.0001f)
            agvTransform.rotation = Quaternion.LookRotation(direction);

        Debug.Log("[NAV] Model aligned at " + routePoint.name);
    }

    void OnDisable()
    {
        if (navigationRoutine != null)
            StopCoroutine(navigationRoutine);

        navigationRoutine = null;
        IsNavigating = false;
    }

    public bool GoTo(Transform stop)
    {
        if (stop == null)
        {
            SetFault("Destination is missing");
            return false;
        }

        return GoToPosition(stop.position, stop.name);
    }

    public bool GoToPosition(Vector3 destination, string destinationName)
    {
        if (!ValidateReferences())
            return false;

        if (navigationRoutine != null)
            StopCoroutine(navigationRoutine);

        commands.Clear();
        BuildCommandPlan(destination);

        plannedCommandCount = commands.Count;
        currentCommandNumber = 0;
        currentCommand = "-";
        Faulted = false;

        if (commands.Count == 0)
        {
            Arrived = true;
            IsNavigating = false;
            navigationState = "ARRIVED";
            Debug.Log("[NAV] Already at " + destinationName);
            return true;
        }

        Arrived = false;
        IsNavigating = true;
        navigationState = "PLANNED";

        Debug.Log(
            $"[NAV] Route to {destinationName}: " +
            $"{plannedCommandCount} commands, " +
            $"step {stepDistanceMM:0.#} mm"
        );

        navigationRoutine = StartCoroutine(ExecutePlan(destinationName));
        return true;
    }

    public void EmergencyStop()
    {
        if (navigationRoutine != null)
            StopCoroutine(navigationRoutine);

        navigationRoutine = null;
        commands.Clear();

        if (bridge != null)
            bridge.SendStop();

        IsNavigating = false;
        Arrived = false;
        navigationState = "STOPPED";
        currentCommand = "stop,0";
    }

    public void ClearFault()
    {
        if (IsNavigating)
            return;

        Faulted = false;
        navigationState = "IDLE";
    }

    IEnumerator ExecutePlan(string destinationName)
    {
        navigationState = "MOVING";

        for (int i = 0; i < commands.Count; i++)
        {
            MotionCommand item = commands[i];

            currentCommandNumber = i + 1;
            currentCommand = item.command + "," + item.value.ToString("0.###");

            bool sent = bridge.SendMove(item.command, item.value);

            if (!sent)
            {
                SetFault("Cannot send " + currentCommand);
                yield break;
            }

            float commandStartTime = Time.realtimeSinceStartup;

            while (!bridge.Arrived)
            {
                if (bridge.Faulted)
                {
                    SetFault("ESP32 reported FAULT at " + currentCommand);
                    yield break;
                }

                if (Time.realtimeSinceStartup - commandStartTime >=
                    commandTimeoutSecond)
                {
                    bridge.SendStop();
                    SetFault("Command timeout at " + currentCommand);
                    yield break;
                }

                yield return null;
            }

            if (delayBetweenCommands > 0f)
                yield return new WaitForSeconds(delayBetweenCommands);
        }

        navigationRoutine = null;
        IsNavigating = false;
        Arrived = true;
        Faulted = false;
        navigationState = "ARRIVED";
        currentCommand = "-";

        Debug.Log("[NAV] Arrived at " + destinationName);
    }

    void BuildCommandPlan(Vector3 destination)
    {
        List<Vector3> pathPoints = BuildRoutePoints(destination);

        Vector3 planPosition = agvTransform.position;
        float planYaw = agvTransform.eulerAngles.y;

        for (int i = 0; i < pathPoints.Count; i++)
        {
            Vector3 target = pathPoints[i];
            target.y = planPosition.y;

            Vector3 direction = target - planPosition;
            direction.y = 0f;

            float distanceMeter = direction.magnitude;

            if (distanceMeter <= 0.0001f)
            {
                planPosition = target;
                continue;
            }

            float targetYaw = Quaternion.LookRotation(direction).eulerAngles.y;
            float signedTurn = Mathf.DeltaAngle(planYaw, targetYaw);

            AddTurnCommands(signedTurn);
            AddForwardCommands(distanceMeter * 1000f);

            planYaw = targetYaw;
            planPosition = target;
        }
    }

    List<Vector3> BuildRoutePoints(Vector3 destination)
    {
        List<Vector3> points = new List<Vector3>();

        int from = SideOf(agvTransform.position);
        int to = SideOf(destination);

        Vector3 routeForward = Corner(from + 1) - Corner(from);
        bool aheadOnThisSide =
            from == to &&
            Vector3.Dot(destination - agvTransform.position, routeForward) >= 0f;

        if (!aheadOnThisSide)
        {
            int cornerIndex = from;
            int safetyCount = 0;

            do
            {
                cornerIndex = (cornerIndex + 1) % route.childCount;
                points.Add(Corner(cornerIndex));
                safetyCount++;
            }
            while (cornerIndex != to && safetyCount <= route.childCount);
        }

        points.Add(destination);
        return points;
    }

    void AddTurnCommands(float signedTurn)
    {
        float remaining = Mathf.Abs(signedTurn);

        if (remaining < minimumTurnDegree)
            return;

        string turnCommand =
            signedTurn >= 0f ? "rotate_right" : "rotate_left";

        while (remaining >= minimumTurnDegree)
        {
            float turnStep = Mathf.Min(remaining, maximumTurnStepDegree);
            commands.Add(new MotionCommand(turnCommand, turnStep));
            remaining -= turnStep;
        }
    }

    void AddForwardCommands(float totalDistanceMM)
    {
        if (totalDistanceMM < minimumMoveMM)
            return;

        int firstForwardIndex = commands.Count;
        float remaining = totalDistanceMM;

        while (remaining > stepDistanceMM)
        {
            commands.Add(new MotionCommand("forward", stepDistanceMM));
            remaining -= stepDistanceMM;
        }

        if (remaining >= minimumMoveMM)
        {
            commands.Add(new MotionCommand("forward", remaining));
        }
        else if (remaining > 0f && commands.Count > firstForwardIndex)
        {
            commands[commands.Count - 1].value += remaining;
        }
    }

    bool ValidateReferences()
    {
        if (bridge == null)
        {
            SetFault("AgvMqttBridge reference is missing");
            return false;
        }

        if (agvTransform == null)
        {
            SetFault("AGV Transform reference is missing");
            return false;
        }

        if (route == null || route.childCount < 2)
        {
            SetFault("Route needs at least 2 corner children");
            return false;
        }

        if (stepDistanceMM < minimumMoveMM)
        {
            SetFault("Step Distance MM must be >= Minimum Move MM");
            return false;
        }

        return true;
    }

    void SetFault(string message)
    {
        navigationRoutine = null;
        IsNavigating = false;
        Arrived = false;
        Faulted = true;
        navigationState = "FAULT";

        Debug.LogError("[NAV] " + message);
    }

    Vector3 Corner(int index)
    {
        return route.GetChild(index % route.childCount).position;
    }

    int SideOf(Vector3 position)
    {
        int bestSide = 0;
        float bestDistance = float.MaxValue;

        for (int i = 0; i < route.childCount; i++)
        {
            Vector3 start = Corner(i);
            Vector3 line = Corner(i + 1) - start;

            float t = Mathf.Clamp01(
                Vector3.Dot(position - start, line) / line.sqrMagnitude
            );

            float distance = Vector3.Distance(
                position,
                start + t * line
            );

            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestSide = i;
            }
        }

        return bestSide;
    }
}
