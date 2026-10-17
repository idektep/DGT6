using System.Collections;
using UnityEngine;

// Physical workflow for one AIRAV-1:
// load command -> conveyor -> arm 1 -> AIRAV route -> arm 2 -> shelf
// -> AIRAV returns to the load station.
public class PhysicalLoopController : MonoBehaviour
{
    [Header("Physical AIRAV-1")]
    public PhysicalAgvNavigator navigator;
    public Transform agvDeck;
    public Transform loadStop;
    public Transform unloadStop;

    [Header("Start Alignment")]
    [Tooltip("Real AIRAV must be placed at LOAD and face clockwise before Play")]
    public bool startAtLoadStation = true;

    [Header("Existing Equipment")]
    public ConveyorBelt conveyor;
    public RobotArm arm1;
    public RobotArm arm2;
    public ShelfStorage shelf;
    public GameObject boxPrefab;

    [Header("Status - Read Only")]
    public string state = "IDLE";
    public int pendingLoads;
    public bool waitingForRetry;

    private bool retryRequested;

    public void RequestLoad()
    {
        pendingLoads++;
        Debug.Log($"[PHYSICAL LOOP] load received, {pendingLoads} waiting");
    }

    public void RetryAfterFault()
    {
        retryRequested = true;
    }

    public void EmergencyStop()
    {
        StopAllCoroutines();

        if (navigator != null)
            navigator.EmergencyStop();

        SetState("EMERGENCY STOP");
    }

    IEnumerator Start()
    {
        if (!ValidateReferences())
            yield break;

        if (startAtLoadStation)
            navigator.SetModelAtRoutePoint(loadStop);

        SetState("Waiting for load command");

        while (true)
        {
            yield return new WaitUntil(
                () => pendingLoads > 0 && navigator.Arrived
            );

            pendingLoads--;

            if (shelf.IsFull)
            {
                SetState("Shelf full - clearing shelf");
                shelf.Clear();
                yield return new WaitForSeconds(0.5f);
            }

            SetState("Conveyor carrying box to Robot Arm 1");
            Transform box = Instantiate(boxPrefab).transform;
            yield return conveyor.Carry(box);

            SetState("Robot Arm 1 loading AIRAV-1");
            yield return arm1.Transfer(
                box,
                agvDeck.position,
                agvDeck
            );

            SetState("Unity planning route to UNLOAD");
            yield return MoveAGVAndWait(unloadStop);

            SetState("Robot Arm 2 moving box to shelf");
            yield return arm2.Transfer(
                box,
                shelf.NextSlot(),
                null
            );

            shelf.Store(box);

            Debug.Log(
                $"[PHYSICAL LOOP] box stored, " +
                $"shelf {shelf.Count}/{shelf.Capacity}"
            );

            SetState("Unity planning return route to LOAD");
            yield return MoveAGVAndWait(loadStop);

            SetState("Waiting for load command");
        }
    }

    IEnumerator MoveAGVAndWait(Transform destination)
    {
        bool movementFinished = false;

        while (!movementFinished)
        {
            retryRequested = false;
            waitingForRetry = false;

            navigator.GoTo(destination);

            yield return new WaitUntil(
                () => navigator.Arrived || navigator.Faulted
            );

            if (navigator.Arrived)
            {
                movementFinished = true;
            }
            else
            {
                waitingForRetry = true;
                SetState("FAULT - check AIRAV then call Retry After Fault");

                yield return new WaitUntil(() => retryRequested);

                waitingForRetry = false;
                navigator.ClearFault();
                SetState("Retrying route to " + destination.name);
            }
        }
    }

    bool ValidateReferences()
    {
        bool ready = true;

        if (navigator == null)
        {
            Debug.LogError("[PHYSICAL LOOP] Navigator is missing");
            ready = false;
        }

        if (agvDeck == null || loadStop == null || unloadStop == null)
        {
            Debug.LogError("[PHYSICAL LOOP] AGV deck or station is missing");
            ready = false;
        }

        if (conveyor == null || arm1 == null || arm2 == null || shelf == null)
        {
            Debug.LogError("[PHYSICAL LOOP] Existing equipment is missing");
            ready = false;
        }

        if (boxPrefab == null)
        {
            Debug.LogError("[PHYSICAL LOOP] Box Prefab is missing");
            ready = false;
        }

        if (!ready)
            SetState("SETUP ERROR");

        return ready;
    }

    void SetState(string newState)
    {
        state = newState;
        Debug.Log("[PHYSICAL LOOP] " + newState);
    }
}
