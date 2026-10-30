using System;
using System.Collections;
using UnityEngine;

public class SmartShelfController : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private Transform cartesianInput;
    [SerializeField] private GameObject boxPrefab;

    [Header("Movement")]
    [SerializeField] private float moveSpeed = 0.200f;

    [Tooltip("ระยะจุดรอด้านหน้าชั้น วัดจากจุดกึ่งกลาง Shelf")]
    [SerializeField] private float frontDistance = 0.075f;

    [Tooltip("เปิดเมื่อด้านหน้าของ Shelf อยู่ทางแกน -Z")]
    [SerializeField] private bool frontIsNegativeZ = true;

    [Header("Standalone Test")]
    [Tooltip("เปิดสำหรับ Week 7 เพื่อให้ MQTT สร้างกล่องทดสอบได้")]
    [SerializeField] private bool autoCreateBoxWhenMissing = true;

    [Header("Runtime")]
    [SerializeField] private ShelfSlot[] slots;
    [SerializeField] private GameObject waitingBox;
    [SerializeField] private string waitingItemId = "";
    [SerializeField] private bool isBusy;

    private int testBoxCounter = 1;

    public event Action<
        string,
        string,
        string,
        bool,
        string
    > PlacementFinished;

    public ShelfSlot[] Slots
    {
        get { return slots; }
    }

    public bool IsBusy
    {
        get { return isBusy; }
    }

    public GameObject WaitingBox
    {
        get { return waitingBox; }
    }

    public string WaitingItemId
    {
        get { return waitingItemId; }
    }

    private void Awake()
    {
        RefreshSlots();
    }

    [ContextMenu("Refresh Slots")]
    public void RefreshSlots()
    {
        slots = GetComponentsInChildren<ShelfSlot>(true);

        Array.Sort(
            slots,
            delegate (ShelfSlot a, ShelfSlot b)
            {
                return string.Compare(
                    a.SlotId,
                    b.SlotId,
                    StringComparison.Ordinal
                );
            }
        );

        Debug.Log(
            "SmartShelf พบช่องทั้งหมด " +
            slots.Length + " ช่อง"
        );
    }

    public ShelfSlot FindSlot(string slotId)
    {
        if (string.IsNullOrWhiteSpace(slotId))
        {
            return null;
        }

        if (slots == null || slots.Length == 0)
        {
            RefreshSlots();
        }

        string wantedId =
            slotId.Trim().ToUpper();

        foreach (ShelfSlot slot in slots)
        {
            if (slot != null &&
                slot.SlotId == wantedId)
            {
                return slot;
            }
        }

        return null;
    }

    // =========================================================
    // สร้างกล่องทดสอบที่ CartesianInput
    // =========================================================

    public GameObject CreateTestBox(
        string requestedItemId = ""
    )
    {
        if (waitingBox != null)
        {
            Debug.LogWarning(
                "ยังมีกล่อง " + waitingItemId +
                " รออยู่ที่ CartesianInput"
            );

            return waitingBox;
        }

        if (boxPrefab == null)
        {
            Debug.LogError(
                "ยังไม่ได้ใส่ Box Prefab " +
                "ใน SmartShelfController"
            );

            return null;
        }

        if (cartesianInput == null)
        {
            Debug.LogError(
                "ยังไม่ได้ใส่ CartesianInput " +
                "ใน SmartShelfController"
            );

            return null;
        }

        if (string.IsNullOrWhiteSpace(requestedItemId))
        {
            requestedItemId =
                "BOX" + testBoxCounter.ToString("000");

            testBoxCounter++;
        }

        waitingBox = Instantiate(
            boxPrefab,
            cartesianInput.position,
            transform.rotation
        );

        waitingBox.name = requestedItemId;

        // ให้กล่องอยู่ภายใต้ ShelfSystem
        // แต่คงตำแหน่ง World Position เดิมไว้
        waitingBox.transform.SetParent(
            transform,
            true
        );

        waitingItemId = requestedItemId;

        PrepareBoxForScriptedMovement(
            waitingBox
        );

        Debug.Log(
            "สร้างกล่อง " + waitingItemId +
            " ที่ CartesianInput"
        );

        return waitingBox;
    }

    // =========================================================
    // รับกล่องจาก Robot Arm ในอนาคต
    // =========================================================

    public bool AcceptIncomingBox(
        GameObject incomingBox,
        string itemId
    )
    {
        if (incomingBox == null)
        {
            Debug.LogWarning(
                "ไม่สามารถรับกล่องได้ เพราะ incomingBox เป็น null"
            );

            return false;
        }

        if (isBusy)
        {
            Debug.LogWarning(
                "Shelf กำลังทำงานอยู่"
            );

            return false;
        }

        if (waitingBox != null)
        {
            Debug.LogWarning(
                "ยังมีกล่องรออยู่ที่ CartesianInput"
            );

            return false;
        }

        if (string.IsNullOrWhiteSpace(itemId))
        {
            itemId =
                "BOX" + testBoxCounter.ToString("000");

            testBoxCounter++;
        }

        waitingBox = incomingBox;
        waitingItemId = itemId;

        waitingBox.name = itemId;

        // คงตำแหน่งที่ Robot Arm วางไว้
        waitingBox.transform.SetParent(
            transform,
            true
        );

        PrepareBoxForScriptedMovement(
            waitingBox
        );

        Debug.Log(
            "รับกล่อง " + itemId +
            " จาก Cartesian แล้ว"
        );

        return true;
    }

    private void PrepareBoxForScriptedMovement(
        GameObject boxObject
    )
    {
        if (boxObject == null)
        {
            return;
        }

        Rigidbody rb =
            boxObject.GetComponent<Rigidbody>();

        if (rb != null)
        {
            rb.useGravity = false;
            rb.isKinematic = true;
        }
    }

    // =========================================================
    // คำสั่งจาก Keyboard
    // =========================================================

    public void RequestPlaceWaiting(
        string slotId
    )
    {
        string requestId =
            "LOCAL-" + Time.frameCount;

        RequestPlace(
            requestId,
            waitingItemId,
            slotId
        );
    }

    // =========================================================
    // คำสั่งหลักสำหรับจัดเก็บกล่อง
    // เรียกใช้ได้ทั้ง Keyboard และ MQTT
    // =========================================================

    public bool RequestPlace(
        string requestId,
        string itemId,
        string slotId
    )
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            requestId =
                "REQ-" + Time.frameCount;
        }

        if (isBusy)
        {
            SendFailure(
                requestId,
                itemId,
                slotId,
                "SHELF_BUSY"
            );

            return false;
        }

        ShelfSlot targetSlot =
            FindSlot(slotId);

        if (targetSlot == null)
        {
            SendFailure(
                requestId,
                itemId,
                slotId,
                "SLOT_NOT_FOUND"
            );

            return false;
        }

        if (!targetSlot.IsFree)
        {
            SendFailure(
                requestId,
                itemId,
                slotId,
                "SLOT_NOT_EMPTY"
            );

            return false;
        }

        // สำหรับ Standalone Test:
        // ถ้ายังไม่มีกล่อง ให้สร้างกล่องให้อัตโนมัติ
        if (waitingBox == null)
        {
            if (autoCreateBoxWhenMissing)
            {
                CreateTestBox(itemId);
            }
            else
            {
                SendFailure(
                    requestId,
                    itemId,
                    slotId,
                    "NO_BOX_AT_CARTESIAN"
                );

                return false;
            }
        }

        if (waitingBox == null)
        {
            SendFailure(
                requestId,
                itemId,
                slotId,
                "BOX_CREATE_FAILED"
            );

            return false;
        }

        // กรณี Keyboard ไม่ได้ส่ง Item ID มา
        if (string.IsNullOrWhiteSpace(itemId))
        {
            itemId = waitingItemId;
        }

        // ป้องกัน MQTT สั่ง Item ID
        // ไม่ตรงกับกล่องที่รออยู่
        if (!string.IsNullOrWhiteSpace(waitingItemId) &&
            waitingItemId != itemId)
        {
            SendFailure(
                requestId,
                itemId,
                slotId,
                "ITEM_ID_MISMATCH"
            );

            return false;
        }

        // จองช่องก่อนเริ่มเคลื่อนที่
        if (!targetSlot.TryReserve(itemId))
        {
            SendFailure(
                requestId,
                itemId,
                slotId,
                "RESERVE_FAILED"
            );

            return false;
        }

        isBusy = true;

        StartCoroutine(
            PlaceBoxRoutine(
                requestId,
                itemId,
                targetSlot
            )
        );

        return true;
    }

    // =========================================================
    // Movement แบบเข้าจากด้านหน้า Shelf
    // =========================================================

    private IEnumerator PlaceBoxRoutine(
        string requestId,
        string itemId,
        ShelfSlot targetSlot
    )
    {
        targetSlot.BeginPlacing();

        GameObject movingBox = waitingBox;

        if (movingBox == null)
        {
            targetSlot.ResetSlot(false);
            isBusy = false;

            SendFailure(
                requestId,
                itemId,
                targetSlot.SlotId,
                "BOX_MISSING"
            );

            yield break;
        }

        movingBox.transform.SetParent(
            transform,
            true
        );

        Vector3 startLocal =
            movingBox.transform.localPosition;

        Vector3 targetLocal =
            transform.InverseTransformPoint(
                targetSlot.transform.position
            );

        // กำหนดตำแหน่งระนาบด้านหน้าชั้น
        float frontZ = frontIsNegativeZ
            ? -frontDistance
            : frontDistance;

        // จุดที่ 1:
        // นำกล่องมาอยู่ในระนาบด้านหน้าชั้น
        Vector3 frontStart = new Vector3(
            startLocal.x,
            startLocal.y,
            frontZ
        );

        // จุดที่ 2:
        // เลื่อนซ้ายหรือขวาให้ตรงคอลัมน์
        Vector3 alignColumn = new Vector3(
            targetLocal.x,
            startLocal.y,
            frontZ
        );

        // จุดที่ 3:
        // เลื่อนขึ้นหรือลงให้ตรงแถว
        Vector3 alignRow = new Vector3(
            targetLocal.x,
            targetLocal.y,
            frontZ
        );

        // จุดที่ 4:
        // ดันกล่องจากหน้าชั้นเข้าไปยังจุดกึ่งกลางช่อง
        Vector3 insertPosition = new Vector3(
            targetLocal.x,
            targetLocal.y,
            targetLocal.z
        );

        Debug.Log(
            "เริ่มจัดเก็บ " + itemId +
            " ไปยัง " + targetSlot.SlotId
        );

        // มายังด้านหน้าของ Shelf
        yield return MoveBoxLocal(
            movingBox.transform,
            frontStart
        );

        // จัดตำแหน่งคอลัมน์
        yield return MoveBoxLocal(
            movingBox.transform,
            alignColumn
        );

        // จัดตำแหน่งแถว
        yield return MoveBoxLocal(
            movingBox.transform,
            alignRow
        );

        // ดันเข้า Shelf
        yield return MoveBoxLocal(
            movingBox.transform,
            insertPosition
        );

        // เมื่อถึงจุดหมายแล้ว
        // ให้กล่องเป็นลูกของ Slot
        movingBox.transform.SetParent(
            targetSlot.transform,
            false
        );

        movingBox.transform.localPosition =
            Vector3.zero;

        movingBox.transform.localRotation =
            Quaternion.identity;

        targetSlot.CompletePlacement(
            movingBox,
            itemId
        );

        waitingBox = null;
        waitingItemId = "";
        isBusy = false;

        Debug.Log(
            itemId +
            " วางเข้าด้านหน้าช่อง " +
            targetSlot.SlotId +
            " สำเร็จ"
        );

        if (PlacementFinished != null)
        {
            PlacementFinished.Invoke(
                requestId,
                itemId,
                targetSlot.SlotId,
                true,
                "PLACED"
            );
        }
    }

    // =========================================================
    // ฟังก์ชันเคลื่อนที่
    // =========================================================

    private IEnumerator MoveBoxLocal(
        Transform boxTransform,
        Vector3 destination
    )
    {
        if (boxTransform == null)
        {
            yield break;
        }

        while (
            Vector3.Distance(
                boxTransform.localPosition,
                destination
            ) > 0.0005f
        )
        {
            boxTransform.localPosition =
                Vector3.MoveTowards(
                    boxTransform.localPosition,
                    destination,
                    moveSpeed * Time.deltaTime
                );

            yield return null;
        }

        boxTransform.localPosition =
            destination;
    }

    // =========================================================
    // ส่งผลล้มเหลวกลับไปยัง MQTT Bridge
    // =========================================================

    private void SendFailure(
        string requestId,
        string itemId,
        string slotId,
        string message
    )
    {
        Debug.LogWarning(
            "Placement Failed: " + message
        );

        if (PlacementFinished != null)
        {
            PlacementFinished.Invoke(
                requestId,
                itemId,
                slotId,
                false,
                message
            );
        }
    }

    // =========================================================
    // Reset
    // =========================================================

    [ContextMenu("Reset All Slots")]
    public void ResetAll()
    {
        StopAllCoroutines();

        isBusy = false;

        if (waitingBox != null)
        {
            Destroy(waitingBox);
        }

        waitingBox = null;
        waitingItemId = "";

        if (slots == null ||
            slots.Length == 0)
        {
            RefreshSlots();
        }

        foreach (ShelfSlot slot in slots)
        {
            if (slot != null)
            {
                slot.ResetSlot(true);
            }
        }

        Debug.Log(
            "Reset Shelf และกล่องทั้งหมดแล้ว"
        );
    }
}