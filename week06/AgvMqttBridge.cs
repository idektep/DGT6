using System.Globalization;
using UnityEngine;

// Low-level MQTT communication with AIRAV-1.
// This class sends one command and updates the Unity model from ESP32 feedback.
public class AgvMqttBridge : MonoBehaviour
{
    [Header("References")]
    public MQTTCore mqtt;
    public Transform agvTransform;

    [Header("MQTT Topics")]
    public string moveTopic = "dgt/airav/control/move";
    public string stateTopic = "dgt/airav/monitor/state";
    public string directionTopic = "dgt/airav/monitor/direction";
    public string distanceTopic = "dgt/airav/monitor/distance_mm";
    public string angleTopic = "dgt/airav/monitor/angle_deg";
    public string progressTopic = "dgt/airav/monitor/progress";

    [Header("Live Data - Read Only")]
    public string robotState = "IDLE";
    public string activeCommand = "stop";
    public float distanceMM;
    public float angleDegree;
    public float progress;
    public bool commandPending;

    public bool Arrived { get; private set; } = true;
    public bool Faulted { get; private set; }

    private Vector3 commandStartPosition;
    private Quaternion commandStartRotation;

    void Awake()
    {
        if (agvTransform == null)
            agvTransform = transform;

        CaptureCommandStartPose();
    }

    void OnEnable()
    {
        MQTTCore.OnConnected += OnMQTTConnected;
        MQTTCore.OnMessageReceived += OnMQTTMessage;
    }

    void OnDisable()
    {
        MQTTCore.OnConnected -= OnMQTTConnected;
        MQTTCore.OnMessageReceived -= OnMQTTMessage;
    }

    void OnMQTTConnected()
    {
        if (mqtt == null)
        {
            Debug.LogError("[AIRAV] MQTTCore reference is missing");
            return;
        }

        mqtt.Subscribe(stateTopic);
        mqtt.Subscribe(directionTopic);
        mqtt.Subscribe(distanceTopic);
        mqtt.Subscribe(angleTopic);
        mqtt.Subscribe(progressTopic);

        Debug.Log("[AIRAV] Feedback topics subscribed");
    }

    void OnMQTTMessage(string topic, string message)
    {
        bool isAiravTopic =
            topic == stateTopic ||
            topic == directionTopic ||
            topic == distanceTopic ||
            topic == angleTopic ||
            topic == progressTopic;

        if (!isAiravTopic)
            return;

        if (UnityMainThreadDispatcher.Instance == null)
        {
            Debug.LogError("[AIRAV] UnityMainThreadDispatcher is missing");
            return;
        }

        UnityMainThreadDispatcher.Instance.Enqueue(
            () => HandleFeedback(topic, message)
        );
    }

    void HandleFeedback(string topic, string message)
    {
        string cleanMessage = message.Trim();

        if (topic == stateTopic)
        {
            HandleState(cleanMessage);
            return;
        }

        if (topic == directionTopic)
        {
            HandleDirection(cleanMessage);
            return;
        }

        if (topic == distanceTopic)
        {
            float value;

            if (TryReadNumber(cleanMessage, out value))
            {
                distanceMM = value;
                ApplyDistanceToModel();
            }

            return;
        }

        if (topic == angleTopic)
        {
            float value;

            if (TryReadNumber(cleanMessage, out value))
            {
                angleDegree = value;
                ApplyAngleToModel();
            }

            return;
        }

        if (topic == progressTopic)
        {
            float value;

            if (TryReadNumber(cleanMessage, out value))
                progress = value;
        }
    }

    void HandleState(string value)
    {
        robotState = value.ToUpperInvariant();

        if (robotState == "MOVING")
        {
            Arrived = false;
            Faulted = false;
            commandPending = true;
        }
        else if (robotState == "ARRIVED")
        {
            Arrived = true;
            Faulted = false;
            commandPending = false;
            progress = 100f;
        }
        else if (robotState == "FAULT")
        {
            Arrived = false;
            Faulted = true;
            commandPending = false;
            Debug.LogWarning("[AIRAV] ESP32 reported FAULT");
        }
        else if (robotState == "IDLE")
        {
            Arrived = true;
            commandPending = false;
        }
    }

    void HandleDirection(string value)
    {
        string newCommand = value.ToLowerInvariant();

        // ESP32 publishes "stop" immediately before ARRIVED.
        // Keep the previous command until all distance/angle data is applied.
        if (newCommand == "stop")
            return;

        // Supports commands sent from MQTTX or another program.
        if (newCommand != activeCommand)
        {
            activeCommand = newCommand;
            CaptureCommandStartPose();
            distanceMM = 0f;
            angleDegree = 0f;
            progress = 0f;
        }
    }

    public bool SendMove(string command, float targetValue)
    {
        if (mqtt == null)
        {
            Debug.LogError("[AIRAV] MQTTCore reference is missing");
            return false;
        }

        command = command.Trim().ToLowerInvariant();

        if (!IsMovementCommand(command))
        {
            Debug.LogError("[AIRAV] Unknown command: " + command);
            return false;
        }

        if (targetValue <= 0f)
        {
            Debug.LogError("[AIRAV] Target must be greater than zero");
            return false;
        }

        activeCommand = command;
        robotState = "COMMAND_SENT";
        distanceMM = 0f;
        angleDegree = 0f;
        progress = 0f;
        Arrived = false;
        Faulted = false;
        commandPending = true;

        CaptureCommandStartPose();

        string payload = command + "," +
            targetValue.ToString("0.###", CultureInfo.InvariantCulture);

        mqtt.Publish(moveTopic, payload);
        Debug.Log("[AIRAV] Command sent: " + payload);
        return true;
    }

    public void SendCommand(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            Debug.LogError("[AIRAV] Empty command");
            return;
        }

        string cleanPayload = payload.Trim();
        string lowerPayload = cleanPayload.ToLowerInvariant();

        if (lowerPayload == "stop" || lowerPayload == "stop,0")
        {
            SendStop();
            return;
        }

        int commaPosition = cleanPayload.IndexOf(',');

        if (commaPosition < 0)
        {
            Debug.LogError("[AIRAV] Correct format: forward,500");
            return;
        }

        string command = cleanPayload.Substring(0, commaPosition).Trim();
        string targetText = cleanPayload.Substring(commaPosition + 1).Trim();
        float target;

        if (!TryReadNumber(targetText, out target))
        {
            Debug.LogError("[AIRAV] Invalid target: " + targetText);
            return;
        }

        SendMove(command, target);
    }

    public void StopCommand()
    {
        SendStop();
    }

    public bool SendStop()
    {
        if (mqtt == null)
            return false;

        mqtt.Publish(moveTopic, "stop,0");

        activeCommand = "stop";
        robotState = "STOP_COMMAND_SENT";
        commandPending = false;
        Arrived = true;

        Debug.Log("[AIRAV] Stop command sent");
        return true;
    }

    void CaptureCommandStartPose()
    {
        if (agvTransform == null)
            return;

        commandStartPosition = agvTransform.position;
        commandStartRotation = agvTransform.rotation;
    }

    void ApplyDistanceToModel()
    {
        if (agvTransform == null)
            return;

        float distanceMeter = distanceMM / 1000f;
        Vector3 startForward = commandStartRotation * Vector3.forward;
        Vector3 startRight = commandStartRotation * Vector3.right;

        if (activeCommand == "forward")
        {
            agvTransform.position =
                commandStartPosition + startForward * distanceMeter;
        }
        else if (activeCommand == "backward")
        {
            agvTransform.position =
                commandStartPosition - startForward * distanceMeter;
        }
        else if (activeCommand == "right")
        {
            agvTransform.position =
                commandStartPosition + startRight * distanceMeter;
        }
        else if (activeCommand == "left")
        {
            agvTransform.position =
                commandStartPosition - startRight * distanceMeter;
        }
    }

    void ApplyAngleToModel()
    {
        if (agvTransform == null)
            return;

        if (activeCommand == "rotate_right")
        {
            agvTransform.rotation = commandStartRotation *
                Quaternion.Euler(0f, angleDegree, 0f);
        }
        else if (activeCommand == "rotate_left")
        {
            agvTransform.rotation = commandStartRotation *
                Quaternion.Euler(0f, -angleDegree, 0f);
        }
    }

    bool TryReadNumber(string text, out float value)
    {
        return float.TryParse(
            text,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out value
        );
    }

    bool IsMovementCommand(string command)
    {
        return
            command == "forward" ||
            command == "backward" ||
            command == "left" ||
            command == "right" ||
            command == "rotate_left" ||
            command == "rotate_right";
    }
}
