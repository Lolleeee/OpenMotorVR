using UnityEngine;

/// <summary>
/// Added at runtime to the released starting object to report the first collision
/// that looks like it landed on a surface underneath (global +Y normal).
/// </summary>
public class ReleasedCollisionReporter : MonoBehaviour
{
    private TaskManager _manager;
    private float _minUpNormal;
    private bool _armed;

    void OnEnable()
    {
        if (ModeManager.Instance != null && !ModeManager.Instance.IsTaskMode)
            enabled = false;
    }

    public void Arm(TaskManager manager, float minUpNormal)
    {
        _manager = manager;
        _minUpNormal = Mathf.Clamp01(minUpNormal);
        _armed = true;
    }

    void OnCollisionEnter(Collision collision)
    {
        if (!enabled || !_armed || _manager == null)
            return;

        if (ModeManager.Instance != null && !ModeManager.Instance.IsTaskMode)
            return;

        if (collision == null)
            return;

        int count = collision.contactCount;
        if (count <= 0)
            return;

        Vector3 sum = Vector3.zero;
        int used = 0;

        // Average the contact points whose surface normal points upward.
        // That corresponds to the object hitting something "underneath" it.
        for (int i = 0; i < count; i++)
        {
            ContactPoint cp = collision.GetContact(i);
            if (Vector3.Dot(cp.normal, Vector3.up) >= _minUpNormal)
            {
                sum += cp.point;
                used++;
            }
        }

        if (used <= 0)
            return;

        Vector3 centerPoint = sum / used;

        _armed = false;
        _manager.NotifyEndpointFromCollision(centerPoint);

        // Self-destruct to avoid repeated triggers.
        Destroy(this);
    }
}
