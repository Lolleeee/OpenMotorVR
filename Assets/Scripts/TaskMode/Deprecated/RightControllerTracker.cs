using UnityEngine;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System;
using System.Globalization;

public class RightControllerTracker : MonoBehaviour
{
    [Header("Tracking Target")]
    [Tooltip("If null, this GameObject's transform will be tracked.")]
    public Transform trackedTransform;

    [Header("Tracking Settings")]
    public float trackingDuration = 30f;
    public float samplingRate = 4f;
    public string outputFilePath = "Assets/Tracking/right_controller_trajectory.csv";

    private List<ControllerSample> samples = new List<ControllerSample>();
    private float elapsedTime = 0f;
    private float sampleInterval;
    private float timeSinceLastSample = 0f;
    public float ElapsedTimeSeconds => elapsedTime;
    public float RemainingTimeSeconds => Mathf.Max(0f, trackingDuration - elapsedTime);
    public float TrackingDurationSeconds => trackingDuration;
    public bool IsTrackingActive => isTracking;
    public bool IsPaused => isPaused;
    public bool IsComplete => trackingComplete;
    public bool IsRecordingInProgress => isTracking && !trackingComplete;


    public int SampleCount => samples != null ? samples.Count : 0;

    private bool isTracking = false;
    private bool isPaused = false;
    private bool trackingComplete = false;
    private bool _trackingEnabled;              // Task OR Play
    private GoalVolume _stopOnGoalVolume;
    private bool _taskModeEnabled;

    [System.Serializable]
    public struct ControllerSample
    {
        public float timestamp;
        public float posX;
        public float posY;
        public float posZ;

        public ControllerSample(float time, Vector3 position)
        {
            timestamp = time;
            posX = position.x;
            posY = position.y;
            posZ = position.z;
        }

        public Vector3 GetPosition() => new Vector3(posX, posY, posZ);
    }

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
    }

    void Start()
    {
        if (trackedTransform == null)
            trackedTransform = transform;

        sampleInterval = 1f / samplingRate;

        string fullPath = Path.Combine(Application.dataPath, "..", outputFilePath);
        string directory = Path.GetDirectoryName(fullPath);
        if (!Directory.Exists(directory))
            Directory.CreateDirectory(directory);
    }

    void HandleModeChanged(Mode newMode)
    {
        _trackingEnabled = (newMode == Mode.Task || newMode == Mode.Play);

        // Leaving Task/Play modes: stop + save if currently tracking
        if (!_trackingEnabled && isTracking && !trackingComplete)
            StopTracking();
    }

    void Update()
    {
        // Task/Play only
        if (!_trackingEnabled)
            return;

        if (!isTracking || isPaused || trackingComplete)
            return;

        elapsedTime += Time.deltaTime;
        timeSinceLastSample += Time.deltaTime;

        if (timeSinceLastSample >= sampleInterval)
        {
            SampleControllerPosition();
            timeSinceLastSample -= sampleInterval;
        }

        // If we are armed to stop on goal completion, do NOT stop on duration.
        if (_stopOnGoalVolume == null && elapsedTime >= trackingDuration)
            StopTracking();
    }

    public void StartTracking()
    {
        if (!_trackingEnabled)
            return;
            
        if (isTracking)
            return;

        samples.Clear();
        elapsedTime = 0f;
        timeSinceLastSample = 0f;

        isTracking = true;
        isPaused = false;
        trackingComplete = false;

        Debug.Log("RightControllerTracker: Started tracking.");
    }

    public void PauseTracking()
    {
        if (!_taskModeEnabled)
            return;

        if (!isTracking || trackingComplete || isPaused)
            return;

        isPaused = true;
        Debug.Log("RightControllerTracker: Paused tracking.");
    }

    public void ResumeTracking()
    {
        if (!_taskModeEnabled)
            return;

        if (!isTracking || trackingComplete || !isPaused)
            return;

        isPaused = false;
        Debug.Log("RightControllerTracker: Resumed tracking.");
    }

    public void StopTracking()
    {
        if (!isTracking)
            return;

        isTracking = false;
        isPaused = false;
        trackingComplete = true;

        SaveToCSV();
        Debug.Log("RightControllerTracker: Stopped tracking (saved CSV).");
    }

    private void SampleControllerPosition()
    {
        if (trackedTransform == null)
            return;

        Vector3 position = trackedTransform.position;

        if (position != Vector3.zero || samples.Count > 0)
            samples.Add(new ControllerSample(elapsedTime, position));
    }

    private void SaveToCSV()
    {
        try
        {
            var ci = CultureInfo.InvariantCulture;

            // outputFilePath is relative to project root (e.g. "Assets/Tracking/...")
            string fullPath = Path.Combine(Application.dataPath, "..", outputFilePath);

            using (StreamWriter writer = new StreamWriter(fullPath, false, Encoding.UTF8))
            {
                writer.WriteLine("timestamp,pos_x,pos_y,pos_z");
                foreach (var sample in samples)
                {
                    writer.WriteLine(
                        $"{sample.timestamp.ToString("F6", ci)}," +
                        $"{sample.posX.ToString("F6", ci)}," +
                        $"{sample.posY.ToString("F6", ci)}," +
                        $"{sample.posZ.ToString("F6", ci)}");
                }
            }

            Debug.Log($"RightControllerTracker: Saved {samples.Count} samples to {outputFilePath}");

            // Remember last recording path for the visualizer
            PlayerPrefs.SetString("LastTrajectoryCsvPath", fullPath);
            PlayerPrefs.Save();
        }
        catch (Exception e)
        {
            Debug.LogError($"RightControllerTracker: Failed to save CSV: {e.Message}");
        }
    }

    public void ArmStopOnGoalComplete(GoalVolume goalVolume)
    {
        if (_stopOnGoalVolume != null)
            _stopOnGoalVolume.Completed -= HandleGoalCompleted;

        _stopOnGoalVolume = goalVolume;

        if (_stopOnGoalVolume != null)
            _stopOnGoalVolume.Completed += HandleGoalCompleted;
    }

    private void HandleGoalCompleted()
    {
        // Stop ONLY when goal completes
        if (isTracking && !trackingComplete)
            StopTracking();
    }
}