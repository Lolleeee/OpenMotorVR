using TMPro;
using UnityEngine;

/// <summary>
/// Task-mode menu option (similar to InspectorOption): supports highlighting, optional status indicator,
/// and an Execute() that routes to TaskManager APIs (start/stop/clear/etc).
/// Intended to be used with a task menu spawner (see TaskContextMenu).
/// </summary>
public class TaskMenuOption : MonoBehaviour
{
	public enum TaskAction
	{
		StartRecording = 0,
		StopRecording = 1,
		ClearTask = 2,
		RebuildEdits = 3,
	}

	[Header("Option Settings")]
	public string optionName = "Option";
	public TaskAction action = TaskAction.StartRecording;

	[Header("References")]
	[Tooltip("If null, auto-found at runtime.")]
	public TaskManager taskManager;

	[Tooltip("Optional: if assigned, Clear will also remove trajectory edit visuals.")]
	public TaskTrajectoryEditor trajectoryEditor;

	[Header("Status Indicator (optional)")]
	[Tooltip("Optional child object that renders status via TextMeshPro/TextMesh/UI Text. Assign per option.")]
	public GameObject statusTextObject;

	public string activeSymbol = "✔";
	public string inactiveSymbol = "✖";
	public Color activeColor = Color.green;
	public Color inactiveColor = Color.red;

	[Header("Visual Feedback")]
	public MeshRenderer optionRenderer;
	public Color normalColor = Color.white;
	public Color highlightColor = Color.yellow;

	[Header("Auto-find")]
	public bool autoFind = true;

	private IStatusWriter _statusWriter;
	private bool _statusDirty = true;
	private bool _lastStatusActive;
	private bool _isTaskMode = true;

void OnEnable()
    {
        if (ModeManager.Instance != null)
            ModeManager.Instance.OnModeChanged += HandleModeChanged;

		ApplyMode(ModeManager.Instance != null ? ModeManager.Instance.CurrentMode : Mode.Task);
    }

    void OnDisable()
    {
        if (ModeManager.Instance != null)
            ModeManager.Instance.OnModeChanged -= HandleModeChanged;
    }

    void Start()
    {
        if (optionRenderer == null)
            optionRenderer = GetComponent<MeshRenderer>();

        if (autoFind)
        {
            if (taskManager == null)
                taskManager = FindFirstObjectByType<TaskManager>();
            if (trajectoryEditor == null)
                trajectoryEditor = FindFirstObjectByType<TaskTrajectoryEditor>();
        }

        SetHighlighted(false);
    }

    private void HandleModeChanged(Mode newMode)
    {
		ApplyMode(newMode);
	}

	private void ApplyMode(Mode newMode)
	{
		_isTaskMode = newMode == Mode.Task;

		var col = GetComponent<Collider>();
		if (col != null)
			col.enabled = _isTaskMode;

		if (optionRenderer != null)
			optionRenderer.enabled = _isTaskMode;

		if (!_isTaskMode)
			SetHighlighted(false);
	}

	void Update()
	{
		if (statusTextObject == null)
			return;

		SetupStatusWriter();
		if (_statusWriter == null)
			return;

		bool active = EvaluateOptionState();
		if (_statusDirty || active != _lastStatusActive)
		{
			_statusDirty = false;
			_lastStatusActive = active;
			_statusWriter.Set(active ? activeSymbol : inactiveSymbol, active ? activeColor : inactiveColor);
		}
	}

	public void SetHighlighted(bool highlighted)
	{
		if (optionRenderer != null)
			optionRenderer.material.color = highlighted ? highlightColor : normalColor;
	}

	public void Execute()
	{
		if (!_isTaskMode)
			return;

		if (taskManager == null)
		{
			Debug.LogWarning($"TaskMenuOption: No TaskManager for '{optionName}'.");
			return;
		}

		Debug.Log($"Executing TaskMenuOption '{optionName}' ({action})");

		switch (action)
		{
			case TaskAction.StartRecording:
				taskManager.start_rec();
				break;
			case TaskAction.StopRecording:
				taskManager.stop_rec();
				break;
			case TaskAction.ClearTask:
				taskManager.ClearTask();
				if (trajectoryEditor != null) trajectoryEditor.Clear();
				break;
			case TaskAction.RebuildEdits:
				if (trajectoryEditor != null) trajectoryEditor.BuildFromTaskManager();
				break;
		}

		MarkStatusDirty();
	}

	private void MarkStatusDirty() => _statusDirty = true;

	private bool EvaluateOptionState()
	{
		if (taskManager == null)
			return false;

		// "Active" meaning varies per option; this drives the check/cross.
		switch (action)
		{
			case TaskAction.StartRecording:
				return taskManager.IsArmed || taskManager.IsRecordingTrajectory;
			case TaskAction.StopRecording:
				return taskManager.IsRecordingTrajectory;
			case TaskAction.ClearTask:
				return taskManager.HasEndPoint || (taskManager.Trajectory != null && taskManager.Trajectory.Count > 0);
			case TaskAction.RebuildEdits:
				return taskManager.HasEndPoint;
			default:
				return false;
		}
	}

	private void SetupStatusWriter()
	{
		if (_statusWriter != null || statusTextObject == null)
			return;

		var tmpText = statusTextObject.GetComponent<TMP_Text>();
		if (tmpText != null)
		{
			_statusWriter = new TMPTextWriter(tmpText);
			return;
		}

		var textMesh = statusTextObject.GetComponent<TextMesh>();
		if (textMesh != null)
		{
			_statusWriter = new TextMeshWriter(textMesh);
			return;
		}

		var uiText = statusTextObject.GetComponent<UnityEngine.UI.Text>();
		if (uiText != null)
			_statusWriter = new UITextWriter(uiText);
	}

	private interface IStatusWriter
	{
		void Set(string text, Color color);
	}

	private class TextMeshWriter : IStatusWriter
	{
		private readonly TextMesh _textMesh;
		public TextMeshWriter(TextMesh textMesh) { _textMesh = textMesh; }
		public void Set(string text, Color color) { _textMesh.text = text; _textMesh.color = color; }
	}

	private class UITextWriter : IStatusWriter
	{
		private readonly UnityEngine.UI.Text _text;
		public UITextWriter(UnityEngine.UI.Text text) { _text = text; }
		public void Set(string text, Color color) { _text.text = text; _text.color = color; }
	}

	private class TMPTextWriter : IStatusWriter
	{
		private readonly TMP_Text _text;
		public TMPTextWriter(TMP_Text text) { _text = text; }
		public void Set(string text, Color color) { _text.text = text; _text.color = color; }
	}
}

