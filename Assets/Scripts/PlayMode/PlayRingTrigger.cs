using UnityEngine;

public class PlayRingTrigger : MonoBehaviour
{
    [HideInInspector] public PlayManager manager;
    [HideInInspector] public int ringIndex;

    void OnTriggerEnter(Collider other)
    {
        if (manager == null)
            return;

        if (!manager.IsActive)
            return;

        if (manager.IsStartingObjectCollider(other))
            manager.NotifyRingHit(ringIndex);
    }

    void OnTriggerStay(Collider other)
    {
        if (manager == null)
            return;

        if (!manager.IsActive)
            return;

        // If the ring gets enabled while the object is already overlapping, OnTriggerEnter won't fire.
        if (manager.IsStartingObjectCollider(other))
            manager.NotifyRingHit(ringIndex);
    }
}
