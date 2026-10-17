using UnityEngine;

// Receives the same "load" command as the old simulation listener,
// but sends it to PhysicalLoopController.
public class PhysicalLoadCommandListener : MonoBehaviour
{
    [Header("MQTT")]
    public MQTTCore mqtt;
    public string topic = "dgt_agv/loop/command";

    [Header("Physical Workflow")]
    public PhysicalLoopController physicalLoop;

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
        if (mqtt != null)
            mqtt.Subscribe(topic);
    }

    void OnMQTTMessage(string receivedTopic, string message)
    {
        if (receivedTopic != topic)
            return;

        if (message.Trim().ToLowerInvariant() != "load")
        {
            Debug.Log(
                $"[PHYSICAL LOAD] ignored '{message}' (only load is accepted)"
            );
            return;
        }

        if (UnityMainThreadDispatcher.Instance == null)
        {
            Debug.LogError("[PHYSICAL LOAD] Dispatcher is missing");
            return;
        }

        UnityMainThreadDispatcher.Instance.Enqueue(
            () => physicalLoop.RequestLoad()
        );
    }
}
