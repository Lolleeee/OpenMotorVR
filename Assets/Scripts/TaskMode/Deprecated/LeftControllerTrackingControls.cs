using UnityEngine;
using UnityEngine.InputSystem;

public class LeftControllerTrackingControls : MonoBehaviour
{
    [Header("References")]
    public RightControllerTracker rightControllerTracker;

    [Header("Input Actions (Left Controller)")]
    [Tooltip("Button to Start/Pause/Resume (e.g., X button).")]
    public InputActionProperty startPauseAction;

    [Tooltip("Button to Stop (e.g., Y button).")]
    public InputActionProperty stopAction;

    [Header("Threshold")]
    public float pressThreshold = 0.5f;

    private bool _startPauseWasPressed;
    private bool _stopWasPressed;

    void OnEnable()
    {
        if (startPauseAction.action != null) startPauseAction.action.Enable();
        if (stopAction.action != null) stopAction.action.Enable();
    }

    void OnDisable()
    {
        if (startPauseAction.action != null) startPauseAction.action.Disable();
        if (stopAction.action != null) stopAction.action.Disable();
    }

    void Update()
    {
        if (rightControllerTracker == null)
            return;

        bool startPausePressed = (startPauseAction.action?.ReadValue<float>() ?? 0f) > pressThreshold;
        bool stopPressed = (stopAction.action?.ReadValue<float>() ?? 0f) > pressThreshold;

        if (startPausePressed && !_startPauseWasPressed)
        {
            if (!rightControllerTracker.IsTrackingActive && !rightControllerTracker.IsComplete)
            {
                rightControllerTracker.StartTracking();
            }
            else if (rightControllerTracker.IsTrackingActive && !rightControllerTracker.IsPaused)
            {
                rightControllerTracker.PauseTracking();
            }
            else if (rightControllerTracker.IsTrackingActive && rightControllerTracker.IsPaused)
            {
                rightControllerTracker.ResumeTracking();
            }
            else if (rightControllerTracker.IsComplete)
            {
                rightControllerTracker.StartTracking();
            }
        }

        if (stopPressed && !_stopWasPressed)
        {
            rightControllerTracker.StopTracking();
        }

        _startPauseWasPressed = startPausePressed;
        _stopWasPressed = stopPressed;
    }
}