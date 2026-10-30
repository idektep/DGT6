using UnityEngine;

public class ShelfKeyboardTest : MonoBehaviour
{
    [SerializeField]
    private SmartShelfController shelfController;

    private void Update()
    {
        if (shelfController == null)
        {
            return;
        }

        if (Input.GetKeyDown(KeyCode.B))
        {
            shelfController.CreateTestBox();
        }

        if (Input.GetKeyDown(KeyCode.Alpha1))
        {
            shelfController.RequestPlaceWaiting("S01");
        }

        if (Input.GetKeyDown(KeyCode.Alpha2))
        {
            shelfController.RequestPlaceWaiting("S02");
        }

        if (Input.GetKeyDown(KeyCode.Alpha3))
        {
            shelfController.RequestPlaceWaiting("S03");
        }

        if (Input.GetKeyDown(KeyCode.Alpha4))
        {
            shelfController.RequestPlaceWaiting("S04");
        }

        if (Input.GetKeyDown(KeyCode.Alpha5))
        {
            shelfController.RequestPlaceWaiting("S05");
        }

        if (Input.GetKeyDown(KeyCode.Alpha6))
        {
            shelfController.RequestPlaceWaiting("S06");
        }

        if (Input.GetKeyDown(KeyCode.R))
        {
            shelfController.ResetAll();
        }
    }
}