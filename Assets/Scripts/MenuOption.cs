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
        onExecute?.Invoke();
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
