using UnityEngine;

public enum Mode
{
    Play,
    Build,
    Task
}

public class ModeManager : MonoBehaviour
{
    public static ModeManager Instance { get; private set; }

    public Mode CurrentMode { get; private set; } = Mode.Build;

    public delegate void ModeChanged(Mode newMode);
    public event ModeChanged OnModeChanged;

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    public void SetMode(Mode newMode)
    {
        if (CurrentMode != newMode)
        {
            CurrentMode = newMode;
            OnModeChanged?.Invoke(newMode);
            Debug.Log($"Switched to mode: {newMode}");
        }
    }

    public bool IsBuildMode => CurrentMode == Mode.Build;
    public bool IsTaskMode => CurrentMode == Mode.Task;
    public bool IsPlayMode => CurrentMode == Mode.Play;
}