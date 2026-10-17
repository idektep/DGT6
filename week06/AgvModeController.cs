using UnityEngine;

// Select the mode before pressing Play.
// OFF = original two-AGV simulation.
// ON  = one physical AIRAV-1 controlled through MQTT.
[DefaultExecutionOrder(-100)]
public class AgvModeController : MonoBehaviour
{
    [Header("Select Before Play")]
    public bool usePhysicalAGV;

    [Header("Original Simulation")]
    public LoopController simulationLoop;
    public LoadCommandListener simulationLoadListener;
    public AgvMover agv1Mover;
    public AgvMover agv2Mover;
    public GameObject agv1Object;
    public GameObject agv2Object;

    [Header("Physical AIRAV-1")]
    [Tooltip("Choose AGV_1 or AGV_2 to represent the real robot")]
    public GameObject physicalAgvObject;
    public AgvMqttBridge mqttBridge;
    public PhysicalAgvNavigator navigator;
    public PhysicalLoopController physicalLoop;
    public PhysicalLoadCommandListener physicalLoadListener;

    void Awake()
    {
        ApplySelectedMode();
    }

    void ApplySelectedMode()
    {
        if (usePhysicalAGV)
            EnablePhysicalMode();
        else
            EnableSimulationMode();
    }

    void EnableSimulationMode()
    {
        SetPhysicalComponents(false);

        if (agv1Object != null)
            agv1Object.SetActive(true);

        if (agv2Object != null)
            agv2Object.SetActive(true);

        if (agv1Mover != null)
            agv1Mover.enabled = true;

        if (agv2Mover != null)
            agv2Mover.enabled = true;

        if (simulationLoop != null)
            simulationLoop.enabled = true;

        if (simulationLoadListener != null)
            simulationLoadListener.enabled = true;

        Debug.Log("[AIRAV MODE] Original Simulation Mode");
    }

    void EnablePhysicalMode()
    {
        if (simulationLoadListener != null)
            simulationLoadListener.enabled = false;

        if (simulationLoop != null)
            simulationLoop.enabled = false;

        if (agv1Mover != null)
            agv1Mover.enabled = false;

        if (agv2Mover != null)
            agv2Mover.enabled = false;

        if (agv1Object != null)
            agv1Object.SetActive(agv1Object == physicalAgvObject);

        if (agv2Object != null)
            agv2Object.SetActive(agv2Object == physicalAgvObject);

        if (physicalAgvObject != null)
            physicalAgvObject.SetActive(true);

        SetPhysicalComponents(true);

        Debug.Log(
            "[AIRAV MODE] Physical Mode - Unity plans the route for AIRAV-1"
        );
    }

    void SetPhysicalComponents(bool value)
    {
        if (mqttBridge != null)
            mqttBridge.enabled = value;

        if (navigator != null)
            navigator.enabled = value;

        if (physicalLoop != null)
            physicalLoop.enabled = value;

        if (physicalLoadListener != null)
            physicalLoadListener.enabled = value;
    }
}
