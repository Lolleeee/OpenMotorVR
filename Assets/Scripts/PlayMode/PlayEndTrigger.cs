using UnityEngine;

public class PlayEndTrigger : MonoBehaviour
{
    [HideInInspector] public PlayManager manager;

    void OnTriggerEnter(Collider other)
    {
        if (manager == null)
            return;

        if (!manager.IsActive)
            return;

        if (manager.IsStartingObjectCollider(other))
            manager.NotifyEndReached();
    }

    void OnTriggerStay(Collider other)
    {
        if (manager == null)
            return;

        if (!manager.IsActive)
            return;

        // If the end trigger gets enabled while the object is already overlapping, OnTriggerEnter won't fire.
        if (manager.IsStartingObjectCollider(other))
            manager.NotifyEndReached();
    }
}
