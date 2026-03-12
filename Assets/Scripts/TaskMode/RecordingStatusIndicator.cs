using TMPro;
using UnityEngine;

/// <summary>
/// On-screen indicator that displays the current recording state of TaskManager.
/// Add this to a UI Canvas with a TextMeshProUGUI child to display live status.
/// </summary>
public class RecordingStatusIndicator : MonoBehaviour
{
    [Header("References")]
    public TaskManager taskManager;

    [Tooltip("TextMeshProUGUI component to display status (or assign child with TMP_Text).")]
    public TMP_Text statusText;

    [Header("Display")]
    public bool showIndicator = true;

    [Tooltip("Color when armed (waiting for grab).")]
    public Color armedColor = Color.cyan;

    [Tooltip("Color when actively recording.")]
    public Color recordingColor = Color.red;

    [Tooltip("Color when waiting for collision/endpoint.")]
    public Color waitingColor = Color.yellow;

    [Tooltip("Color when idle.")]
    public Color idleColor = Color.gray;

    private bool _shouldShowInCurrentMode = true;

    void OnEnable()
    {
        if (ModeManager.Instance != null)
            ModeManager.Instance.OnModeChanged += HandleModeChanged;
    }

    void OnDisable()
    {
        if (ModeManager.Instance != null)
            ModeManager.Instance.OnModeChanged -= HandleModeChanged;
    }

    void Start()
    {
        if (taskManager == null)
            taskManager = FindFirstObjectByType<TaskManager>();

        if (statusText == null)
            statusText = GetComponentInChildren<TMP_Text>();

        if (statusText == null && transform.childCount == 0)
        {
            Debug.LogWarning("RecordingStatusIndicator: No TMP_Text found as child or assigned. Creating one.");
            var child = new GameObject("StatusText");
            child.transform.SetParent(transform);
            statusText = child.AddComponent<TextMeshProUGUI>();
            statusText.text = "Recording Status";
        }

        _shouldShowInCurrentMode = ModeManager.Instance == null || ModeManager.Instance.IsTaskMode;
    }

    private void HandleModeChanged(Mode newMode)
    {
        _shouldShowInCurrentMode = newMode == Mode.Task;
        if (!_shouldShowInCurrentMode && statusText != null)
            statusText.text = "";
    }

    void Update()
    {
        if (!showIndicator || !_shouldShowInCurrentMode || statusText == null || taskManager == null)
            return;

        // Determine current state and display accordingly
        string status = "";
        Color color = idleColor;

        if (taskManager.IsRecordingTrajectory)
        {
            status = "● RECORDING";
            color = recordingColor;
        }
        else if (taskManager.IsArmed)
        {
            status = "◐ ARMED (grab object)";
            color = armedColor;
        }
        else if (taskManager.IsWaitingForEndpoint)
        {
            status = "◑ WAITING FOR COLLISION";
            color = waitingColor;
        }
        else if (taskManager.HasEndPoint)
        {
            status = "✓ COMPLETE";
            color = Color.green;
        }
        else
        {
            status = "○ Ready";
            color = idleColor;
        }

        statusText.text = status;
        statusText.color = color;
    }
}
