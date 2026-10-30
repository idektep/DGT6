using System;
using System.Collections;
using System.Collections.Concurrent;
using UnityEngine;

[Serializable]
public class ShelfCheckCommand
{
    public string request_id;
    public string slot_id;
}

[Serializable]
public class ShelfCheckFeedback
{
    public string request_id;
    public string shelf_id;
    public string slot_id;
    public bool free;
    public string state;
    public string item_id;
    public string timestamp;
}

[Serializable]
public class ShelfPlaceCommand
{
    public string request_id;
    public string item_id;
    public string slot_id;
}

[Serializable]
public class ShelfPlaceFeedback
{
    public string request_id;
    public string shelf_id;
    public string item_id;
    public string slot_id;
    public string status;
    public string message;
    public string timestamp;
}

[Serializable]
public class ShelfSlotStateData
{
    public string slot_id;
    public string state;
    public string item_id;
    public bool free;
}

[Serializable]
public class ShelfStatePayload
{
    public string shelf_id;
    public ShelfSlotStateData[] slots;
    public string timestamp;
}

public class ShelfMqttBridge : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private MQTTCore mqttCore;
    [SerializeField] private SmartShelfController shelfController;

    [Header("Shelf")]
    [SerializeField] private string shelfId = "SHELF_A";

    [Header("MQTT")]
    [SerializeField] private string baseTopic =
        "dgt/warehouse/shelf/SHELF_A";

    private struct IncomingMessage
    {
        public string topic;
        public string payload;

        public IncomingMessage(
            string newTopic,
            string newPayload
        )
        {
            topic = newTopic;
            payload = newPayload;
        }
    }

    private readonly ConcurrentQueue<IncomingMessage>
        messageQueue =
            new ConcurrentQueue<IncomingMessage>();

    private volatile bool subscribeRequested;

    private string CheckCommandTopic
    {
        get
        {
            return baseTopic + "/command/check";
        }
    }

    private string PlaceCommandTopic
    {
        get
        {
            return baseTopic + "/command/place";
        }
    }

    private string CheckFeedbackTopic
    {
        get
        {
            return baseTopic + "/feedback/check";
        }
    }

    private string PlaceFeedbackTopic
    {
        get
        {
            return baseTopic + "/feedback/place";
        }
    }

    private string StateTopic
    {
        get
        {
            return baseTopic + "/state/slots";
        }
    }

    private void OnEnable()
    {
        MQTTCore.OnMessageReceived += OnMqttMessage;
        MQTTCore.OnConnected += OnMqttConnected;

        if (shelfController != null)
        {
            shelfController.PlacementFinished +=
                OnPlacementFinished;
        }
    }

    private void Start()
    {
        StartCoroutine(DelayedSubscribe());
    }

    private void OnDisable()
    {
        MQTTCore.OnMessageReceived -= OnMqttMessage;
        MQTTCore.OnConnected -= OnMqttConnected;

        if (shelfController != null)
        {
            shelfController.PlacementFinished -=
                OnPlacementFinished;
        }
    }

    private IEnumerator DelayedSubscribe()
    {
        yield return new WaitForSeconds(1f);
        subscribeRequested = true;
    }

    private void OnMqttConnected()
    {
        subscribeRequested = true;
    }

    private void OnMqttMessage(
        string topic,
        string payload
    )
    {
        if (topic == CheckCommandTopic ||
            topic == PlaceCommandTopic)
        {
            messageQueue.Enqueue(
                new IncomingMessage(topic, payload)
            );
        }
    }

    private void Update()
    {
        if (subscribeRequested)
        {
            subscribeRequested = false;
            SubscribeTopics();
        }

        int processed = 0;

        IncomingMessage message;

        while (
            processed < 20 &&
            messageQueue.TryDequeue(out message)
        )
        {
            HandleMessage(
                message.topic,
                message.payload
            );

            processed++;
        }
    }

    private void SubscribeTopics()
    {
        if (mqttCore == null)
        {
            Debug.LogError(
                "ShelfMqttBridge ยังไม่ได้ใส่ MQTTCore"
            );

            return;
        }

        mqttCore.Subscribe(CheckCommandTopic);
        mqttCore.Subscribe(PlaceCommandTopic);

        Debug.Log(
            "Shelf subscribed:\n" +
            CheckCommandTopic + "\n" +
            PlaceCommandTopic
        );

        PublishShelfState();
    }

    private void HandleMessage(
        string topic,
        string payload
    )
    {
        if (topic == CheckCommandTopic)
        {
            HandleCheckCommand(payload);
            return;
        }

        if (topic == PlaceCommandTopic)
        {
            HandlePlaceCommand(payload);
        }
    }

    private void HandleCheckCommand(string payload)
    {
        ShelfCheckCommand command;

        try
        {
            command =
                JsonUtility.FromJson<ShelfCheckCommand>(
                    payload
                );
        }
        catch
        {
            Debug.LogWarning(
                "Check command JSON ไม่ถูกต้อง"
            );

            return;
        }

        ShelfSlot slot =
            shelfController.FindSlot(command.slot_id);

        ShelfCheckFeedback feedback =
            new ShelfCheckFeedback();

        feedback.request_id = command.request_id;
        feedback.shelf_id = shelfId;
        feedback.slot_id = command.slot_id;
        feedback.timestamp = DateTime.UtcNow.ToString("o");

        if (slot == null)
        {
            feedback.free = false;
            feedback.state = "NOT_FOUND";
            feedback.item_id = "";
        }
        else
        {
            feedback.free = slot.IsFree;
            feedback.state = slot.StateName;
            feedback.item_id = slot.ItemId;
        }

        PublishJson(
            CheckFeedbackTopic,
            JsonUtility.ToJson(feedback)
        );
    }

    private void HandlePlaceCommand(string payload)
    {
        ShelfPlaceCommand command;

        try
        {
            command =
                JsonUtility.FromJson<ShelfPlaceCommand>(
                    payload
                );
        }
        catch
        {
            Debug.LogWarning(
                "Place command JSON ไม่ถูกต้อง"
            );

            return;
        }

        shelfController.RequestPlace(
            command.request_id,
            command.item_id,
            command.slot_id
        );
    }

    private void OnPlacementFinished(
        string requestId,
        string itemId,
        string slotId,
        bool success,
        string message
    )
    {
        ShelfPlaceFeedback feedback =
            new ShelfPlaceFeedback();

        feedback.request_id = requestId;
        feedback.shelf_id = shelfId;
        feedback.item_id = itemId;
        feedback.slot_id = slotId;
        feedback.status =
            success ? "PLACED" : "FAILED";

        feedback.message = message;
        feedback.timestamp =
            DateTime.UtcNow.ToString("o");

        PublishJson(
            PlaceFeedbackTopic,
            JsonUtility.ToJson(feedback)
        );

        PublishShelfState();
    }

    public void PublishShelfState()
    {
        if (shelfController == null ||
            shelfController.Slots == null)
        {
            return;
        }

        ShelfSlot[] slots =
            shelfController.Slots;

        ShelfStatePayload payload =
            new ShelfStatePayload();

        payload.shelf_id = shelfId;
        payload.timestamp =
            DateTime.UtcNow.ToString("o");

        payload.slots =
            new ShelfSlotStateData[slots.Length];

        for (int i = 0; i < slots.Length; i++)
        {
            payload.slots[i] =
                new ShelfSlotStateData();

            payload.slots[i].slot_id =
                slots[i].SlotId;

            payload.slots[i].state =
                slots[i].StateName;

            payload.slots[i].item_id =
                slots[i].ItemId;

            payload.slots[i].free =
                slots[i].IsFree;
        }

        PublishJson(
            StateTopic,
            JsonUtility.ToJson(payload)
        );
    }

    private void PublishJson(
        string topic,
        string json
    )
    {
        if (mqttCore == null)
        {
            return;
        }

        mqttCore.Publish(topic, json);

        Debug.Log(
            "MQTT TX\n" +
            topic + "\n" +
            json
        );
    }
}