using UnityEngine;
using System;
using System.Text;
using System.Collections;
using uPLibrary.Networking.M2Mqtt;
using uPLibrary.Networking.M2Mqtt.Messages;


// ======================================================
// MQTT QoS OPTIONS
// จะปรากฏเป็น Dropdown ใน Inspector
// ======================================================

public enum MQTTQoS
{
    QoS0 = 0,
    QoS1 = 1,
    QoS2 = 2
}


public class MQTTCore : MonoBehaviour
{
    // ==================================================
    // MQTT SETTINGS
    // ==================================================

    [Header("MQTT Settings")]

    public string broker =
        "192.168.1.200";

    public int port = 1883;

    public bool autoReconnect = true;

    public float reconnectDelay = 3f;


    // เลือก QoS จาก Inspector
    public MQTTQoS qos = MQTTQoS.QoS0;


    // ==================================================
    // PRIVATE VARIABLES
    // ==================================================

    private MqttClient client;

    private string clientId;

    private bool isConnecting = false;


    // ==================================================
    // EVENTS
    // ==================================================

    public static event Action<
        string,
        string
    > OnMessageReceived;

    public static event Action OnConnected;


    // ==================================================
    // START
    // ==================================================

    void Start()
    {
        Connect();
    }


    // ==================================================
    // CONNECT MQTT
    // ==================================================

    void Connect()
    {
        if (isConnecting)
        {
            return;
        }

        isConnecting = true;


        try
        {
            clientId =
                "unity_" +
                Guid.NewGuid()
                    .ToString("N")
                    .Substring(0, 8);


            // ใช้ Broker และ Port จาก Inspector
            client = new MqttClient(
                broker,
                port,
                false,
                null,
                null,
                MqttSslProtocols.None
            );


            client.MqttMsgPublishReceived +=
                OnMQTTMessage;

            client.ConnectionClosed +=
                OnConnectionClosed;


            client.Connect(clientId);


            Debug.Log(
                $"🟢 MQTT Connected: {broker}:{port}"
            );

            Debug.Log(
                $"MQTT Client ID: {clientId}"
            );

            Debug.Log(
                $"MQTT QoS: {(int)qos}"
            );


            isConnecting = false;


            // แจ้ง Controller อื่นว่า MQTT พร้อมแล้ว
            OnConnected?.Invoke();
        }
        catch (Exception ex)
        {
            Debug.LogWarning(
                $"⚠️ MQTT Connect failed: {ex.Message}"
            );


            isConnecting = false;


            if (autoReconnect)
            {
                StartCoroutine(
                    Reconnect()
                );
            }
        }
    }


    // ==================================================
    // CONNECTION CLOSED
    // ==================================================

    void OnConnectionClosed(
        object sender,
        EventArgs e
    )
    {
        Debug.LogWarning(
            "❌ MQTT Disconnected!"
        );


        if (autoReconnect)
        {
            StartCoroutine(
                Reconnect()
            );
        }
    }


    // ==================================================
    // RECONNECT
    // ==================================================

    IEnumerator Reconnect()
    {
        yield return new WaitForSeconds(
            reconnectDelay
        );


        Connect();
    }


    // ==================================================
    // RECEIVE MQTT MESSAGE
    // ==================================================

    void OnMQTTMessage(
        object sender,
        MqttMsgPublishEventArgs e
    )
    {
        string topic =
            e.Topic;


        string message =
            Encoding.UTF8.GetString(
                e.Message
            );


        Debug.Log(
            $"📥 MQTT Message: [{topic}] {message}"
        );


        OnMessageReceived?.Invoke(
            topic,
            message
        );
    }


    // ==================================================
    // CONVERT QoS ENUM TO M2MQTT QoS
    // ==================================================

    byte GetQoSLevel()
    {
        if (qos == MQTTQoS.QoS1)
        {
            return
                MqttMsgBase
                    .QOS_LEVEL_AT_LEAST_ONCE;
        }


        if (qos == MQTTQoS.QoS2)
        {
            return
                MqttMsgBase
                    .QOS_LEVEL_EXACTLY_ONCE;
        }


        return
            MqttMsgBase
                .QOS_LEVEL_AT_MOST_ONCE;
    }


    // ==================================================
    // SUBSCRIBE
    // ==================================================

    public void Subscribe(
        string topic
    )
    {
        if (
            client != null &&
            client.IsConnected
        )
        {
            client.Subscribe(
                new string[]
                {
                    topic
                },
                new byte[]
                {
                    GetQoSLevel()
                }
            );


            Debug.Log(
                $"📡 Subscribed: {topic} | QoS {(int)qos}"
            );
        }
        else
        {
            Debug.LogWarning(
                "❌ MQTT not connected (Subscribe failed)"
            );
        }
    }


    // ==================================================
    // PUBLISH
    // ==================================================

    public void Publish(
        string topic,
        string message
    )
    {
        if (
            client != null &&
            client.IsConnected
        )
        {
            client.Publish(
                topic,
                Encoding.UTF8.GetBytes(
                    message
                ),
                GetQoSLevel(),
                false
            );


            Debug.Log(
                $"📤 Published: [{topic}] {message} | QoS {(int)qos}"
            );
        }
        else
        {
            Debug.LogWarning(
                "❌ MQTT not connected (Publish failed)"
            );
        }
    }
}