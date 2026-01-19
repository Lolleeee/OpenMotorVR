using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

public class ObjectInspector : MonoBehaviour
{
    [Header("References")]
    public GameObject menuPrefab;
    public NearFarInteractor interactor;
    public InputActionProperty menuButtonAction;

    [Header("Settings")]
    public Vector3 menuOffset = Vector3.zero;
    private Transform target;
    private GameObject activeMenu;
    private InspectorOption currentHighlightedOption;
    

    void Update()
    {
        bool isPressed = menuButtonAction.action.ReadValue<float>() > 0.5f;

        if (isPressed && activeMenu == null)
        {
            // Button just pressed - spawn menu
            TrySpawnMenu();
        }
        else if (!isPressed && activeMenu != null)
        {
            // Button released - execute and close
            ExecuteInspectorOption();
            CloseMenu();
        }
        else if (isPressed && activeMenu != null)
        {
            // Button held - only check hover, don't move menu
            CheckHoveredOption();
        }
    }
    
    void OnBuildSelected(GameObject target)
    {
        Debug.Log("Opening build menu!");
    }

    void OnQuitSelected(GameObject target)
    {
        Debug.Log("Quitting application!");
        Application.Quit();
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#endif
    }
    void TrySpawnMenu()
    {
        if (interactor.hasHover && interactor.interactablesHovered.Count > 0)
        {
            target = interactor.interactablesHovered[0].transform;
            OpenMenu(target);
            
        }
        else
        {
            Debug.Log("No object hovered to attach menu to");
        }
    }
    void OpenMenu(Transform target)
    {
        // Get controller position/rotation at spawn time
        Transform attachTransform = interactor.attachTransform;

        // Calculate spawn position with offset
        Vector3 menuPosition = attachTransform.position + attachTransform.TransformDirection(menuOffset);

        // Menu faces same direction as controller at spawn time
        Quaternion menuRotation = attachTransform.rotation;

        // Spawn and leave it there!
        activeMenu = Instantiate(menuPrefab, menuPosition, menuRotation);

        

        // Pass the context (hovered object) to each option
        foreach (var option in activeMenu.GetComponentsInChildren<InspectorOption>())
        {
            option.Initialize(target.gameObject);
        }
        Debug.Log($"Menu spawned between controller and {target.name}");
    }
    
    void CheckHoveredOption()
    {   
        if (interactor.hasHover)
        {
            Debug.Log($"Hovering something! Count: {interactor.interactablesHovered.Count}");
            
            foreach (var interactable in interactor.interactablesHovered)
            {
                Debug.Log($"Interactable: {interactable.transform.name}");
            }
        }
        else
        {
            Debug.Log("Not hovering anything");
        }
        // Check what's being hovered
        if (interactor.hasHover && interactor.interactablesHovered.Count > 0)
        {
            var hoveredInteractable = interactor.interactablesHovered[0];
            InspectorOption option = hoveredInteractable.transform.GetComponent<InspectorOption>();
            
            if (option != currentHighlightedOption)
            {
                if (currentHighlightedOption != null)
                    currentHighlightedOption.SetHighlighted(false);
                
                currentHighlightedOption = option;
                if (currentHighlightedOption != null)
                {
                    currentHighlightedOption.SetHighlighted(true);
                    Debug.Log($"Hovering: {currentHighlightedOption.optionName}");
                }
            }
        }
        else
        {
            if (currentHighlightedOption != null)
            {
                currentHighlightedOption.SetHighlighted(false);
                currentHighlightedOption = null;
            }
        }
    }
    
    void ExecuteInspectorOption()
    {
        if (currentHighlightedOption != null)
        {
            currentHighlightedOption.Execute();
            Debug.Log($"Executed: {currentHighlightedOption.optionName}");
        }
        else
        {
            Debug.Log("Menu closed - no option selected");
        }
    }
    
    void CloseMenu()
    {
        if (activeMenu != null)
        {
            Destroy(activeMenu);
            activeMenu = null;
        }
        
        currentHighlightedOption = null;
    }
    void OnEnable()
    {
        if (ModeManager.Instance != null)
        {
            ModeManager.Instance.OnModeChanged += HandleModeChanged;
        }

        if (ModeManager.Instance.IsBuildMode)
        menuButtonAction.action.Enable();
        else
        menuButtonAction.action.Disable();
    }

    void OnDisable()
    {
        CloseMenu();
        if (ModeManager.Instance != null)
        {
            ModeManager.Instance.OnModeChanged -= HandleModeChanged;
        }
        menuButtonAction.action.Disable();

    }

        private void HandleModeChanged(Mode newMode)
    {
        Debug.Log($"ContextMenu detected mode change: {newMode}");

        if (ModeManager.Instance.IsBuildMode)
        menuButtonAction.action.Enable();
        else
        {
            CloseMenu();
            menuButtonAction.action.Disable();
        }
        
    }
}
