using UnityEngine;
using UnityEngine.Events;

public class MenuOption : MonoBehaviour
{
    [Header("Option Settings")]
    public string optionName = "Option";
    public UnityEvent onExecute;

    [Header("Visual Feedback")]
    public MeshRenderer optionRenderer;
    public Color normalColor = Color.white;
    public Color highlightColor = Color.yellow;

    void Start()
    {
        if (optionRenderer == null)
            optionRenderer = GetComponent<MeshRenderer>();

        SetHighlighted(false);
    }

    public void SetHighlighted(bool highlighted)
    {
        if (optionRenderer != null)
        {
            optionRenderer.material.color = highlighted ? highlightColor : normalColor;
        }
    }

    public void Execute()
    {
        Debug.Log($"Executing {optionName}");

        // If a custom UnityEvent is wired up in the Inspector, prefer it.
        // This makes MenuOption reusable for Task-mode actions without hardcoding strings here.
        if (onExecute != null && onExecute.GetPersistentEventCount() > 0)
        {
            onExecute.Invoke();
            return;
        }

        // Built-in option routing
        switch (optionName.ToLower())
        {
            case "build":
                ModeManager.Instance.SetMode(Mode.Build);
                break;
            case "task":
                ModeManager.Instance.SetMode(Mode.Task);
                break;
            case "play":
                ModeManager.Instance.SetMode(Mode.Play);
                break;
            case "save":
                Debug.Log("Save action triggered");
                SaveContentScene();
                break;
            case "load":
                Debug.Log("Load action triggered");
                LoadLastSave();
                break;
            default:
                Debug.LogWarning($"Unknown option: {optionName} (no onExecute configured)");
                break;
        }
    }

    // Public API for wiring in the Inspector (UnityEvent)
    public void SaveContentScene()
    {
        TriggerSave();
    }

    public void LoadLastSave()
    {
        TriggerLoad();
    }

    private void TriggerSave()
    {
        var loader = ResolveSceneLoader();
        if (loader != null)
        {
            loader.SaveContentScene();
            return;
        }

        var saveSystem = ResolveSaveSystem();
        if (saveSystem != null)
        {
            saveSystem.SaveScene();
            return;
        }

        Debug.LogWarning("No SceneLoader or SaveSystem available to perform save.");
    }

    private void TriggerLoad()
    {
        var loader = ResolveSceneLoader();
        if (loader != null)
        {
            loader.LoadLastSave();
            return;
        }

        var saveSystem = ResolveSaveSystem();
        if (saveSystem != null)
        {
            saveSystem.LoadScene();
            return;
        }

        Debug.LogWarning("No SceneLoader or SaveSystem available to perform load.");
    }

    private SceneLoader ResolveSceneLoader()
    {
        if (SceneLoader.Instance != null)
            return SceneLoader.Instance;

        return FindFirstObjectByType<SceneLoader>();
    }

    private SaveSystem ResolveSaveSystem()
    {
        return FindFirstObjectByType<SaveSystem>();
    }

    // Example menu actions
    public void OpenSubMenu()
    {
        Debug.Log($"Opening submenu from {optionName}");
        // Implement submenu logic
    }

    public void PerformAction()
    {
        Debug.Log($"Performing action: {optionName}");
        // Your custom action here
    }
}