using UnityEngine;
using UnityEngine.Events;

public class InspectorOption : MonoBehaviour
{
    [Header("Option Settings")]
    public string optionName = "Option";
    public UnityEvent<GameObject> onExecuteWithTarget; // target-aware action
    
    [Header("Visual Feedback")]
    public MeshRenderer optionRenderer;
    public Color normalColor = Color.white;
    public Color highlightColor = Color.yellow;

    private GameObject targetObject;

    void Start()
    {
        if (optionRenderer == null)
            optionRenderer = GetComponent<MeshRenderer>();
        SetHighlighted(false);
    }

    public void Initialize(GameObject target)
    {
        targetObject = target;
    }

    public void SetHighlighted(bool highlighted)
    {
        if (optionRenderer != null)
            optionRenderer.material.color = highlighted ? highlightColor : normalColor;
    }

    public void Execute()
    {
        if (targetObject == null)
        {
            Debug.LogWarning($"No target for {optionName}");
            return;
        }

        Debug.Log($"Executing {optionName} on {targetObject.name}");

        // Handle built-in option routing here
        switch (optionName.ToLower())
        {
            case "grabbable":
                var spawned = targetObject.GetComponent<SpawnedObject>();
                if (spawned != null)
                    spawned.ToggleGrabbable();
                else
                    Debug.LogWarning($"{targetObject.name} has no SpawnedObject component!");
                break;

            case "gravity":
                var spawnedObj = targetObject.GetComponent<SpawnedObject>();
                if (spawnedObj != null)
                    spawnedObj.ToggleGravity();
                else
                    Debug.LogWarning($"{targetObject.name} has no SpawnedObject component!");
                break;

            case "collisions":
                var spawnedCol = targetObject.GetComponent<SpawnedObject>();
                if (spawnedCol != null)
                    spawnedCol.ToggleCollision();
                else
                    Debug.LogWarning($"{targetObject.name} has no SpawnedObject component!");
                break;

            case "kinematics":

                var spawnedKin = targetObject.GetComponent<SpawnedObject>();
                if (spawnedKin != null)
                    spawnedKin.ToggleKinematic();
                else
                    Debug.LogWarning($"{targetObject.name} has no SpawnedObject component!");
                break;

            default:
                Debug.LogWarning($"Unknown option: {optionName}");
                break;
        }
    }

    // Example menu actions
    public void OpenSubMenu()
    {
        Debug.Log($"Opening submenu from {optionName}");
    }

    public void PerformAction()
    {
        Debug.Log($"Performing action: {optionName}");
    }
}
