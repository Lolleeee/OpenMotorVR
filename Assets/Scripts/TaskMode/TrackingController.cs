using UnityEngine;
using UnityEngine.InputSystem;

public class TaskModeActionGate : MonoBehaviour
{
    [Header("Input Actions to enable only in Task mode")]
    public InputActionProperty[] actions;

    void OnEnable()
    {
        if (ModeManager.Instance != null)
            ModeManager.Instance.OnModeChanged += HandleModeChanged;

        HandleModeChanged(ModeManager.Instance != null ? ModeManager.Instance.CurrentMode : Mode.Build);
    }

    void OnDisable()
    {
        if (ModeManager.Instance != null)
            ModeManager.Instance.OnModeChanged -= HandleModeChanged;

        SetEnabled(false);
    }

    void HandleModeChanged(Mode newMode)
    {
        SetEnabled(newMode == Mode.Task);
    }

    void SetEnabled(bool enabled)
    {
        if (actions == null) return;

        foreach (var a in actions)
        {
            var act = a.action;
            if (act == null) continue;

            if (enabled) act.Enable();
            else act.Disable();
        }
    }
}