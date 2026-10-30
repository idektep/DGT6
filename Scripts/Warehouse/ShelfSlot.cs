using UnityEngine;

public enum ShelfSlotState
{
    EMPTY,
    RESERVED,
    PLACING,
    OCCUPIED,
    FAULT
}

public class ShelfSlot : MonoBehaviour
{
    [Header("Slot Identity")]
    [SerializeField] private string slotId = "S01";

    [Header("Runtime State")]
    [SerializeField] private ShelfSlotState state = ShelfSlotState.EMPTY;
    [SerializeField] private string itemId = "";
    [SerializeField] private GameObject storedBox;

    [Header("Gizmo")]
    [SerializeField] private Vector3 gizmoSize =
        new Vector3(0.020f, 0.020f, 0.020f);

    public string SlotId
    {
        get { return slotId; }
    }

    public ShelfSlotState State
    {
        get { return state; }
    }

    public string StateName
    {
        get { return state.ToString(); }
    }

    public string ItemId
    {
        get { return itemId; }
    }

    public GameObject StoredBox
    {
        get { return storedBox; }
    }

    public bool IsFree
    {
        get { return state == ShelfSlotState.EMPTY; }
    }

    public bool TryReserve(string newItemId)
    {
        if (state != ShelfSlotState.EMPTY)
        {
            return false;
        }

        itemId = newItemId;
        state = ShelfSlotState.RESERVED;
        return true;
    }

    public void BeginPlacing()
    {
        if (state == ShelfSlotState.RESERVED)
        {
            state = ShelfSlotState.PLACING;
        }
    }

    public void CompletePlacement(
        GameObject boxObject,
        string newItemId
    )
    {
        storedBox = boxObject;
        itemId = newItemId;
        state = ShelfSlotState.OCCUPIED;
    }

    public void SetFault()
    {
        state = ShelfSlotState.FAULT;
    }

    public void ResetSlot(bool destroyStoredBox)
    {
        if (destroyStoredBox && storedBox != null)
        {
            Destroy(storedBox);
        }

        storedBox = null;
        itemId = "";
        state = ShelfSlotState.EMPTY;
    }

    private void OnValidate()
    {
        slotId = slotId.Trim().ToUpper();
    }

    private void OnDrawGizmos()
    {
        switch (state)
        {
            case ShelfSlotState.EMPTY:
                Gizmos.color = Color.green;
                break;

            case ShelfSlotState.RESERVED:
                Gizmos.color = Color.yellow;
                break;

            case ShelfSlotState.PLACING:
                Gizmos.color = Color.cyan;
                break;

            case ShelfSlotState.OCCUPIED:
                Gizmos.color = Color.red;
                break;

            default:
                Gizmos.color = Color.magenta;
                break;
        }

        Matrix4x4 oldMatrix = Gizmos.matrix;
        Gizmos.matrix = transform.localToWorldMatrix;
        Gizmos.DrawWireCube(Vector3.zero, gizmoSize);
        Gizmos.matrix = oldMatrix;
    }
}