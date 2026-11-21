using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// Replays the eight high-level warehouse steps from dgt/twin/report.
// It uses the motion components already included in the Week 9 project, but
// does not publish MQTT commands or device states.
public class Week10TwinPlayback : MonoBehaviour
{
    [Header("Conveyor")]
    [SerializeField] private Transform conveyorStart;
    [SerializeField] private Transform conveyorEnd;
    [SerializeField] private ConveyorBeltMotion conveyorBelt;
    [SerializeField] private float conveyorSpeed = 0.10f;

    [Header("Robot arms and AGV")]
    [SerializeField] private ArmPickPlace conveyorArm;   // Robot Arm 2
    [SerializeField] private AgvShuttle agv;
    [SerializeField] private ArmPickPlace storageArm;    // Robot Arm 1
    [SerializeField] private float agvSpeed = 0.15f;
    [SerializeField] private float agvTurnSpeed = 90f;
    [SerializeField] private float armJointSpeed = 45f;
    [SerializeField] private float gripperSpeed = 0.01f;
    [SerializeField] private float armHoverHeight = 0.05f;
    [SerializeField] private float armCarryRadius = 0.12f;
    [SerializeField] private float armCarryHeight = 0.15f;

    [Header("RAVAN and shelf")]
    [SerializeField] private SmartShelfController shelf;
    [SerializeField] private CartesianAxes cartesianAxes;
    [SerializeField] private Transform ravanInput;
    [SerializeField] private float ravanSpeed = 0.20f;
    [SerializeField] private float ravanFrontDistance = 0.077f;
    [SerializeField] private bool shelfFrontIsNegativeZ = true;
    [SerializeField] private float ravanInsertLift = 0.006f;
    [Range(1, 6)]
    [SerializeField] private int defaultEmptyShelf = 5;

    [Header("Objects")]
    [SerializeField] private GameObject boxPrefab;
    [Tooltip("If this is empty, Box Prefab is used as a temporary pallet for the workshop.")]
    [SerializeField] private GameObject palletPrefab;
    [SerializeField] private Vector3 boxOnAgvDeck = new Vector3(0f, 0.01f, 0.04f);
    [SerializeField] private bool autoCreateEmptyPalletForDemo = true;
    // Leave empty for the current store -> home workflow. For a prepared
    // RAVAN cycle, enter the shelf numbers that initially contain empty pallets.
    [SerializeField] private int[] initialEmptyPalletShelves = new int[0];
    [SerializeField] private bool clearReturnedPalletOnNewCycle = true;

    [Header("Live data - read only")]
    [SerializeField] private bool isPlaying;
    [SerializeField] private int visualCycle;
    [SerializeField] private int visualStep;
    [SerializeField] private string visualState = "Idle";
    [SerializeField] private string currentItemId = "";

    private class StepRequest
    {
        public int cycle;
        public int step;
        public int storeShelf;
        public string cartesianTask;
        public int emptyShelf;
    }

    private readonly Queue<StepRequest> pendingSteps =
        new Queue<StepRequest>();

    private Coroutine queueRunner;
    private Transform currentBox;
    private Transform returnedPallet;
    private int boxCounter = 1;

    private Vector3 ravanHomeX;
    private Vector3 ravanHomeY;
    private Vector3 ravanHomeZ;
    private Vector3 ravanHomeSeatWorld;
    private Vector3 ravanCurrentSeatWorld;
    private List<Vector3> lastRavanPath = new List<Vector3>();
    private bool ravanAtHome = true;
    private bool lastRavanOperationSucceeded;

    public int DefaultEmptyShelf
    {
        get { return defaultEmptyShelf; }
    }

    private void Awake()
    {
        ApplyMotionSettings();
        CaptureRavanHome();

        // CartesianAxes follows SmartShelfController's private placement
        // routine. Week 10 drives both store and pick paths here, so only one
        // component may move the axes.
        if (cartesianAxes != null)
        {
            cartesianAxes.enabled = false;
        }
    }

    private void Start()
    {
        if (initialEmptyPalletShelves == null)
        {
            return;
        }

        for (int i = 0; i < initialEmptyPalletShelves.Length; i++)
        {
            int shelfNumber = initialEmptyPalletShelves[i];
            if (shelfNumber >= 1 && shelfNumber <= 6)
            {
                EnsureEmptyPallet(shelfNumber);
            }
        }
    }

    private void ApplyMotionSettings()
    {
        if (agv != null)
        {
            agv.speed = agvSpeed;
            agv.turnSpeed = agvTurnSpeed;
        }

        ApplyArmSettings(conveyorArm);
        ApplyArmSettings(storageArm);
    }

    private void ApplyArmSettings(ArmPickPlace arm)
    {
        if (arm == null)
        {
            return;
        }

        arm.jointSpeed = armJointSpeed;
        arm.fingerSpeed = gripperSpeed;
        arm.hoverHeight = armHoverHeight;
        arm.carryRadius = armCarryRadius;
        arm.carryHeight = armCarryHeight;
    }

    private void CaptureRavanHome()
    {
        if (cartesianAxes == null ||
            cartesianAxes.axisX == null ||
            cartesianAxes.axisY == null ||
            cartesianAxes.axisZ == null ||
            cartesianAxes.seat == null)
        {
            Debug.LogError("[W10 Playback] CartesianAxes references are incomplete");
            return;
        }

        ravanHomeX = cartesianAxes.axisX.position;
        ravanHomeY = cartesianAxes.axisY.position;
        ravanHomeZ = cartesianAxes.axisZ.position;
        ravanHomeSeatWorld = cartesianAxes.seat.position;
        ravanCurrentSeatWorld = ravanHomeSeatWorld;
    }

    public void EnqueueStep(
        int cycle,
        int step,
        int storeShelf,
        string cartesianTask,
        int emptyShelf
    )
    {
        StepRequest request = new StepRequest();
        request.cycle = cycle;
        request.step = step;
        request.storeShelf = Mathf.Clamp(storeShelf, 1, 6);
        request.cartesianTask = string.IsNullOrEmpty(cartesianTask)
            ? "store"
            : cartesianTask.ToLowerInvariant();
        request.emptyShelf = emptyShelf >= 1 && emptyShelf <= 6
            ? emptyShelf
            : defaultEmptyShelf;

        pendingSteps.Enqueue(request);

        if (queueRunner == null)
        {
            queueRunner = StartCoroutine(RunQueue());
        }
    }

    private IEnumerator RunQueue()
    {
        isPlaying = true;

        while (pendingSteps.Count > 0)
        {
            StepRequest request = pendingSteps.Dequeue();
            visualCycle = request.cycle;
            visualStep = request.step;
            visualState = "Playing step " + request.step;

            yield return PlayStep(request);
        }

        visualState = "Waiting for MQTT";
        isPlaying = false;
        queueRunner = null;
    }

    private IEnumerator PlayStep(StepRequest request)
    {
        switch (request.step)
        {
            case 1:
                yield return ConveyorToArm2(request.cycle);
                break;

            case 2:
                if (RequireBox("Robot Arm 2 pick") &&
                    HasReference(conveyorArm, "Robot Arm 2"))
                {
                    yield return conveyorArm.Pick(currentBox);
                }
                break;

            case 3:
                if (RequireBox("Robot Arm 2 place") &&
                    HasReference(conveyorArm, "Robot Arm 2") &&
                    HasReference(agv, "AGV") &&
                    agv.deck != null)
                {
                    Vector3 deckPoint = agv.deck.TransformPoint(boxOnAgvDeck);
                    yield return conveyorArm.Place(deckPoint);
                    currentBox.SetParent(agv.deck, true);
                }
                break;

            case 4:
                if (agv != null)
                {
                    yield return agv.DriveToStop();
                }
                break;

            case 5:
                if (RequireBox("Robot Arm 1 pick") &&
                    HasReference(storageArm, "Robot Arm 1"))
                {
                    yield return storageArm.Pick(currentBox);
                }
                break;

            case 6:
                if (RequireBox("Robot Arm 1 place") &&
                    HasReference(storageArm, "Robot Arm 1"))
                {
                    Vector3 dropPoint = ravanInput != null
                        ? ravanInput.position
                        : ravanHomeSeatWorld;

                    yield return storageArm.Place(dropPoint);
                    currentBox.SetParent(null, true);
                    currentBox.position = ravanHomeSeatWorld;
                }
                break;

            case 7:
                if (request.cartesianTask == "cycle")
                {
                    yield return RavanCycle(request.storeShelf, request.emptyShelf);
                }
                else if (request.cartesianTask == "pick")
                {
                    yield return PickEmptyPallet(request.emptyShelf);
                }
                else
                {
                    yield return StoreCurrentBox(request.storeShelf);
                }
                break;

            case 8:
                yield return ReturnAgvAndRavanHome();
                break;
        }

        visualState = "Step " + request.step + " visual complete";
    }

    private IEnumerator ConveyorToArm2(int cycle)
    {
        if (boxPrefab == null || conveyorStart == null || conveyorEnd == null)
        {
            Debug.LogError("[W10 Playback] Box Prefab or conveyor points are missing");
            yield break;
        }

        if (clearReturnedPalletOnNewCycle && returnedPallet != null)
        {
            Destroy(returnedPallet.gameObject);
            returnedPallet = null;
        }

        if (currentBox != null)
        {
            Destroy(currentBox.gameObject);
            currentBox = null;
        }

        currentItemId = "BOX" + boxCounter.ToString("000");
        boxCounter++;

        currentBox = Instantiate(
            boxPrefab,
            conveyorStart.position,
            Quaternion.identity
        ).transform;

        currentBox.name = currentItemId;
        PrepareMovingObject(currentBox.gameObject);

        while (Vector3.Distance(currentBox.position, conveyorEnd.position) > 0.0005f)
        {
            Vector3 before = currentBox.position;
            currentBox.position = Vector3.MoveTowards(
                currentBox.position,
                conveyorEnd.position,
                conveyorSpeed * Time.deltaTime
            );

            if (conveyorBelt != null)
            {
                conveyorBelt.Carry(currentBox.position - before);
            }

            yield return null;
        }

        currentBox.position = conveyorEnd.position;
    }

    private IEnumerator StoreCurrentBox(int shelfNumber)
    {
        lastRavanOperationSucceeded = false;

        if (!RequireBox("RAVAN store") || !ValidateRavan())
        {
            yield break;
        }

        if (!ravanAtHome)
        {
            yield return ReturnRavanHome();
        }

        ShelfSlot targetSlot = FindSlot(shelfNumber);
        if (targetSlot == null)
        {
            yield break;
        }

        if (!targetSlot.IsFree)
        {
            Debug.LogWarning("[W10 Playback] " + targetSlot.SlotId + " is not empty");
            yield break;
        }

        if (!targetSlot.TryReserve(currentItemId))
        {
            Debug.LogWarning("[W10 Playback] Cannot reserve " + targetSlot.SlotId);
            yield break;
        }

        targetSlot.BeginPlacing();
        currentBox.position = cartesianAxes.seat.position;
        currentBox.SetParent(cartesianAxes.axisZ, true);

        lastRavanPath = BuildShelfPath(targetSlot);
        for (int i = 1; i < lastRavanPath.Count; i++)
        {
            yield return MoveRavanSeatTo(lastRavanPath[i]);
        }

        currentBox.SetParent(targetSlot.transform, false);
        currentBox.localPosition = Vector3.zero;
        currentBox.localRotation = Quaternion.identity;
        targetSlot.CompletePlacement(currentBox.gameObject, currentItemId);

        currentBox = null;
        ravanAtHome = false;
        lastRavanOperationSucceeded = true;
    }

    private IEnumerator RavanCycle(int storeShelf, int emptyShelf)
    {
        if (storeShelf == emptyShelf)
        {
            Debug.LogWarning("[W10 Playback] RAVAN cycle needs different store and empty-pallet shelves");
            yield break;
        }

        yield return StoreCurrentBox(storeShelf);
        if (!lastRavanOperationSucceeded)
        {
            yield break;
        }

        yield return ReturnRavanHome();
        yield return PickEmptyPallet(emptyShelf);
    }

    private IEnumerator PickEmptyPallet(int shelfNumber)
    {
        if (!ValidateRavan())
        {
            yield break;
        }

        if (!ravanAtHome)
        {
            yield return ReturnRavanHome();
        }

        ShelfSlot sourceSlot = FindSlot(shelfNumber);
        if (sourceSlot == null)
        {
            yield break;
        }

        if ((sourceSlot.IsFree || sourceSlot.StoredBox == null) &&
            autoCreateEmptyPalletForDemo)
        {
            EnsureEmptyPallet(shelfNumber);
        }

        GameObject palletObject = sourceSlot.StoredBox;
        if (palletObject == null)
        {
            Debug.LogWarning(
                "[W10 Playback] No empty pallet is stored in " + sourceSlot.SlotId
            );
            yield break;
        }

        List<Vector3> path = BuildShelfPath(sourceSlot);

        // Move the empty fork from Ready Point to the pallet.
        for (int i = 1; i < path.Count; i++)
        {
            yield return MoveRavanSeatTo(path[i]);
        }

        // The pallet is now on the fork. Remove it from shelf inventory without
        // destroying the GameObject, then retrace the safe approach path.
        palletObject.transform.position =ravanCurrentSeatWorld;
        palletObject.transform.SetParent(cartesianAxes.axisZ, true);
        sourceSlot.ResetSlot(false);

        for (int i = path.Count - 2; i >= 0; i--)
        {
            yield return MoveRavanSeatTo(path[i]);
        }

        returnedPallet = palletObject.transform;
        lastRavanPath.Clear();
        ravanAtHome = true;
    }

    private IEnumerator ReturnAgvAndRavanHome()
    {
        int remaining = 0;

        if (agv != null)
        {
            remaining++;
            StartCoroutine(RunAndSignal(agv.DriveHome(), delegate { remaining--; }));
        }

        if (ValidateRavan(false))
        {
            remaining++;
            StartCoroutine(RunAndSignal(ReturnRavanHome(), delegate { remaining--; }));
        }

        while (remaining > 0)
        {
            yield return null;
        }
    }

    private IEnumerator RunAndSignal(IEnumerator routine, Action finished)
    {
        yield return routine;
        finished();
    }

    private IEnumerator ReturnRavanHome()
    {
        if (!ValidateRavan(false) || ravanAtHome)
        {
            yield break;
        }

        if (lastRavanPath != null && lastRavanPath.Count > 0)
        {
            for (int i = lastRavanPath.Count - 2; i >= 0; i--)
            {
                yield return MoveRavanSeatTo(lastRavanPath[i]);
            }
        }
        else
        {
            yield return MoveRavanSeatTo(ravanHomeSeatWorld);
        }

        lastRavanPath.Clear();
        ravanAtHome = true;
    }

    private List<Vector3> BuildShelfPath(ShelfSlot targetSlot)
    {
        Transform root = shelf.transform;
        Vector3 homeLocal = root.InverseTransformPoint(ravanHomeSeatWorld);
        Vector3 targetLocal = root.InverseTransformPoint(targetSlot.transform.position);
        float frontZ = shelfFrontIsNegativeZ
            ? -ravanFrontDistance
            : ravanFrontDistance;

        Vector3 frontStart = new Vector3(
            homeLocal.x,
            homeLocal.y,
            frontZ
        );

        Vector3 alignColumn = new Vector3(
            targetLocal.x,
            homeLocal.y,
            frontZ
        );

        Vector3 alignRow = new Vector3(
            targetLocal.x,
            targetLocal.y + ravanInsertLift,
            frontZ
        );

        Vector3 insertPosition = new Vector3(
            targetLocal.x,
            targetLocal.y + ravanInsertLift,
            targetLocal.z
        );

        List<Vector3> path = new List<Vector3>();
        path.Add(ravanHomeSeatWorld);
        path.Add(root.TransformPoint(frontStart));
        path.Add(root.TransformPoint(alignColumn));
        path.Add(root.TransformPoint(alignRow));
        path.Add(root.TransformPoint(insertPosition));
        path.Add(targetSlot.transform.position);
        return path;
    }

    private IEnumerator MoveRavanSeatTo(Vector3 targetWorld)
{
    while (Vector3.Distance(
        ravanCurrentSeatWorld,
        targetWorld
    ) > 0.0005f)
    {
        ravanCurrentSeatWorld = Vector3.MoveTowards(
            ravanCurrentSeatWorld,
            targetWorld,
            ravanSpeed * Time.deltaTime
        );

        ApplyRavanOffset(
            ravanCurrentSeatWorld - ravanHomeSeatWorld
        );

        yield return null;
    }

    ravanCurrentSeatWorld = targetWorld;

    ApplyRavanOffset(
        targetWorld - ravanHomeSeatWorld
    );
}

    private void ApplyRavanOffset(Vector3 offset)
    {
        cartesianAxes.axisY.position =
            ravanHomeY + new Vector3(0f, 0f, offset.z);

        cartesianAxes.axisX.position =
            ravanHomeX + new Vector3(offset.x, 0f, offset.z);

        cartesianAxes.axisZ.position =
            ravanHomeZ + offset;
    }

    private ShelfSlot FindSlot(int shelfNumber)
    {
        if (shelf == null)
        {
            Debug.LogError("[W10 Playback] SmartShelfController reference is missing");
            return null;
        }

        string slotId = "S" + Mathf.Clamp(shelfNumber, 1, 6).ToString("00");
        ShelfSlot slot = shelf.FindSlot(slotId);

        if (slot == null)
        {
            Debug.LogError("[W10 Playback] Shelf slot not found: " + slotId);
        }

        return slot;
    }

    private void EnsureEmptyPallet(int shelfNumber)
    {
        ShelfSlot slot = FindSlot(shelfNumber);
        if (slot == null || !slot.IsFree)
        {
            return;
        }

        GameObject prefab = palletPrefab != null ? palletPrefab : boxPrefab;
        if (prefab == null)
        {
            Debug.LogWarning("[W10 Playback] No Pallet Prefab or Box Prefab is assigned");
            return;
        }

        GameObject pallet = Instantiate(prefab, slot.transform);
        pallet.name = "EMPTY_PALLET_" + slot.SlotId;
        pallet.transform.localPosition = Vector3.zero;
        pallet.transform.localRotation = Quaternion.identity;
        PrepareMovingObject(pallet);
        slot.CompletePlacement(pallet, pallet.name);
    }

    private static void PrepareMovingObject(GameObject target)
    {
        if (target == null)
        {
            return;
        }

        Rigidbody body = target.GetComponent<Rigidbody>();
        if (body != null)
        {
            body.useGravity = false;
            body.isKinematic = true;
        }
    }

    private bool RequireBox(string action)
    {
        if (currentBox != null)
        {
            return true;
        }

        Debug.LogWarning("[W10 Playback] " + action + ": current box is missing");
        return false;
    }

    private bool HasReference(UnityEngine.Object component, string label)
    {
        if (component != null)
        {
            return true;
        }

        Debug.LogError("[W10 Playback] " + label + " reference is missing");
        return false;
    }

    private bool ValidateRavan(bool logError = true)
    {
        bool valid = shelf != null &&
            cartesianAxes != null &&
            cartesianAxes.axisX != null &&
            cartesianAxes.axisY != null &&
            cartesianAxes.axisZ != null &&
            cartesianAxes.seat != null;

        if (!valid && logError)
        {
            Debug.LogError("[W10 Playback] RAVAN/Shelf references are incomplete");
        }

        return valid;
    }
}
