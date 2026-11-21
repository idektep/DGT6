using System;
using System.Collections;
using System.Collections.Concurrent;
using UnityEngine;

// Reads the retained report produced by Coordinator/twin_coordinator.py.
// Unity is a passive digital-twin viewer: this component never publishes a
// device status and never competes with the Python coordinator for control.

[Serializable]
public class W10TwinReport
{
    public string coordinator;
    public W10Devices devices;
    public W10Work work;
}

[Serializable]
public class W10Devices
{
    public W10Device agv;
    public W10Device arm1;
    public W10Device arm2;
    public W10Device conveyor;
    public W10Device cartesian;
}

[Serializable]
public class W10Device
{
    public string state;
    public string seen;
    public string check;
    public W10LastStatus last;
}

[Serializable]
public class W10LastStatus
{
    public string state;
    public string task;
    public string phase;
    public string msg;
    public int shelf;
    public int store_shelf;
    public int empty_shelf;
    public bool ready_point;
}

[Serializable]
public class W10Work
{
    public string waitingFor;
    public string mode;
    public int shelf;
    public int emptyShelf;       // Optional future dashboard field.
    public int step;
    public int cycles;
    public W10WorkItem item;
    public W10Message message;
    public W10StepState[] steps;
}

[Serializable]
public class W10WorkItem
{
    public string to;
    public float wait;
    public W10Command cmd;
}

[Serializable]
public class W10Command
{
    public string cmd;
    public int shelf;
    public int store_shelf;
    public int empty_shelf;
}

[Serializable]
public class W10Message
{
    public string time;
    public string text;
    public string level;
}

[Serializable]
public class W10StepState
{
    public string name;
    public string status;
}

public class Week10TwinBridge : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private MQTTCore mqttCore;
    [SerializeField] private Week10TwinPlayback playback;

    [Header("MQTT")]
    [SerializeField] private string reportTopic = "dgt/twin/report";
    [Tooltip("Normally keep this off. If Unity starts in the middle of a cycle, the earlier visual steps do not exist.")]
    [SerializeField] private bool replayCurrentStepOnFirstReport = false;

    [Header("Live data - read only")]
    [SerializeField] private string coordinatorState = "unknown";
    [SerializeField] private string waitingFor = "";
    [SerializeField] private string mode = "";
    [SerializeField] private int currentCycle;
    [SerializeField] private int currentStep;
    [SerializeField] private int selectedShelf = 1;
    [SerializeField] private string latestMessage = "";
    [SerializeField] private string lastJson = "";

    private readonly ConcurrentQueue<string> messageQueue =
        new ConcurrentQueue<string>();

    private bool subscribeRequested;
    private bool subscribed;
    private bool firstReportReceived;
    private int lastTriggeredCycle = int.MinValue;
    private int lastTriggeredStep = -1;
    private string previousWaitingFor = "";

    private void OnEnable()
    {
        MQTTCore.OnConnected += OnMqttConnected;
        MQTTCore.OnMessageReceived += OnMqttMessage;
        StartCoroutine(SubscribeWhenReady());
    }

    private void OnDisable()
    {
        MQTTCore.OnConnected -= OnMqttConnected;
        MQTTCore.OnMessageReceived -= OnMqttMessage;
    }

    private IEnumerator SubscribeWhenReady()
    {
        // MQTTCore and this component can start in either order.
        while (enabled && (mqttCore == null || !mqttCore.IsConnected))
        {
            yield return new WaitForSeconds(0.25f);
        }

        if (enabled)
        {
            subscribeRequested = true;
        }
    }

    private void OnMqttConnected()
    {
        subscribed = false;
        subscribeRequested = true;
    }

    // M2Mqtt calls this event from its receive thread. Only queue text here.
    private void OnMqttMessage(string topic, string payload)
    {
        if (topic == reportTopic)
        {
            messageQueue.Enqueue(payload);
        }
    }

    private void Update()
    {
        if (subscribeRequested && mqttCore != null && mqttCore.IsConnected)
        {
            subscribeRequested = false;
            mqttCore.Subscribe(reportTopic);
            subscribed = true;
            Debug.Log("[W10 Twin] Subscribed: " + reportTopic);
        }

        if (!subscribed && mqttCore != null && mqttCore.IsConnected)
        {
            subscribeRequested = true;
        }

        int processed = 0;
        string json;
        while (processed < 20 && messageQueue.TryDequeue(out json))
        {
            HandleReport(json);
            processed++;
        }
    }

    private void HandleReport(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        W10TwinReport report;
        try
        {
            report = JsonUtility.FromJson<W10TwinReport>(json);
        }
        catch (Exception exception)
        {
            Debug.LogWarning("[W10 Twin] Invalid report JSON: " + exception.Message);
            return;
        }

        if (report == null)
        {
            return;
        }

        lastJson = json;
        coordinatorState = string.IsNullOrEmpty(report.coordinator)
            ? "unknown"
            : report.coordinator;

        if (coordinatorState == "offline")
        {
            latestMessage = "Python coordinator is offline";
            return;
        }

        if (report.work == null)
        {
            return;
        }

        W10Work work = report.work;
        string waitingBeforeThisReport = previousWaitingFor;

        waitingFor = work.waitingFor;
        mode = work.mode;
        currentCycle = work.cycles;
        currentStep = work.step;
        selectedShelf = Mathf.Clamp(work.shelf, 1, 6);
        latestMessage = work.message != null ? work.message.text : "";

        string stepStatus = GetCurrentStepStatus(work);
        bool stepIsRunning = stepStatus == "running" ||
            (string.IsNullOrEmpty(stepStatus) && work.waitingFor == "device");

        bool firstReport = !firstReportReceived;
        if (firstReport)
        {
            firstReportReceived = true;

            if (!replayCurrentStepOnFirstReport && work.step > 1)
            {
                // The scene has not replayed steps 1..N-1, so starting at N would
                // produce impossible parent/box states. Wait for the next cycle.
                lastTriggeredCycle = work.cycles;
                lastTriggeredStep = work.step;
                previousWaitingFor = work.waitingFor;
                Debug.LogWarning(
                    "[W10 Twin] Connected during step " + work.step +
                    ". Start a new cycle to synchronize the visual model."
                );
                return;
            }
        }

        bool newStep = work.cycles != lastTriggeredCycle ||
            work.step != lastTriggeredStep;

        bool restartedStepOne = work.step == 1 &&
            (waitingBeforeThisReport == "stopped" ||
             waitingBeforeThisReport == "error" ||
             waitingBeforeThisReport == "start");

        if (stepIsRunning && work.step >= 1 && work.step <= 8 &&
            (newStep || restartedStepOne))
        {
            int targetShelf;
            int emptyShelf;
            string cartesianTask;
            ReadCartesianInstruction(report, out cartesianTask, out targetShelf, out emptyShelf);

            if (playback == null)
            {
                Debug.LogError("[W10 Twin] Week10TwinPlayback reference is missing");
            }
            else
            {
                playback.EnqueueStep(
                    work.cycles,
                    work.step,
                    targetShelf,
                    cartesianTask,
                    emptyShelf
                );
            }

            lastTriggeredCycle = work.cycles;
            lastTriggeredStep = work.step;
        }

        previousWaitingFor = work.waitingFor;
    }

    private static string GetCurrentStepStatus(W10Work work)
    {
        if (work.steps == null || work.step < 1 || work.step > work.steps.Length)
        {
            return "";
        }

        W10StepState step = work.steps[work.step - 1];
        return step == null || step.status == null ? "" : step.status.ToLowerInvariant();
    }

    private static void ReadCartesianInstruction(
        W10TwinReport report,
        out string task,
        out int storeShelf,
        out int emptyShelf
    )
    {
        task = "store";
        storeShelf = Mathf.Clamp(report.work.shelf, 1, 6);
        emptyShelf = report.work.emptyShelf;

        // The current work item is authoritative. It prevents an old retained
        // Cartesian status from changing a new command.
        if (report.work.item != null &&
            report.work.item.to == "cartesian" &&
            report.work.item.cmd != null)
        {
            W10Command command = report.work.item.cmd;
            if (!string.IsNullOrEmpty(command.cmd))
            {
                task = command.cmd.ToLowerInvariant();
            }

            if (command.shelf >= 1 && command.shelf <= 6)
            {
                storeShelf = command.shelf;
            }

            if (command.store_shelf >= 1 && command.store_shelf <= 6)
            {
                storeShelf = command.store_shelf;
            }

            if (command.empty_shelf >= 1 && command.empty_shelf <= 6)
            {
                emptyShelf = command.empty_shelf;
            }

            return;
        }

        // Fallback for a future coordinator report that only exposes the last
        // RAVAN status rather than the command in work.item.
        W10LastStatus last = report.devices != null &&
            report.devices.cartesian != null
                ? report.devices.cartesian.last
                : null;

        if (last == null)
        {
            return;
        }

        if (!string.IsNullOrEmpty(last.task))
        {
            task = last.task.ToLowerInvariant();
        }

        if (last.shelf >= 1 && last.shelf <= 6)
        {
            storeShelf = last.shelf;
        }

        if (last.store_shelf >= 1 && last.store_shelf <= 6)
        {
            storeShelf = last.store_shelf;
        }

        if (last.empty_shelf >= 1 && last.empty_shelf <= 6)
        {
            emptyShelf = last.empty_shelf;
        }
    }
}
