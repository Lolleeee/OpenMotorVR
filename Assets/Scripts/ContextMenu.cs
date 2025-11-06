using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

public class VRContextMenu : MonoBehaviour
{
    [Header("References")]
    public GameObject menuPrefab;
    public NearFarInteractor nearFarInteractor;
    public InputActionProperty menuButtonAction;
    
    [Header("Settings")]
    public Vector3 menuOffset = Vector3.zero;
    
    private GameObject activeMenu;
    private MenuOption currentHighlightedOption;
    
    void OnEnable()
    {
        menuButtonAction.action.Enable();
    }

    void Update()
    {
        bool isPressed = menuButtonAction.action.ReadValue<float>() > 0.5f;

        if (isPressed && activeMenu == null)
        {
            // Button just pressed - spawn menu
            OpenMenu();
        }
        else if (!isPressed && activeMenu != null)
        {
            // Button released - execute and close
            ExecuteMenuOption();
            CloseMenu();
        }
        else if (isPressed && activeMenu != null)
        {
            // Button held - only check hover, don't move menu
            CheckHoveredOption();
        }
    }

    void ConfigureMenuOptions()
    {
        // Find all MenuOption components in the spawned menu
        MenuOption[] options = activeMenu.GetComponentsInChildren<MenuOption>();

        foreach (MenuOption option in options)
        {
            // Clear any existing listeners
            option.onExecute.RemoveAllListeners();

            // Route to correct function based on name
            switch (option.optionName.ToLower())
            {
                case "build":
                    option.onExecute.AddListener(OnBuildSelected);
                    break;

                default:
                    Debug.LogWarning($"No handler for menu option: {option.optionName}");
                    break;
            }
        }
    }
    
    void OnBuildSelected()
    {
        Debug.Log("Opening build menu!");
    }
    
    void OnQuitSelected()
    {
        Debug.Log("Quitting application!");
        Application.Quit();
        #if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
        #endif
    }
    void OpenMenu()
    {
        // Get controller position/rotation at spawn time
        Transform attachTransform = nearFarInteractor.attachTransform;

        // Calculate spawn position with offset
        Vector3 menuPosition = attachTransform.position + attachTransform.TransformDirection(menuOffset);

        // Menu faces same direction as controller at spawn time
        Quaternion menuRotation = attachTransform.rotation;

        // Spawn and leave it there!
        activeMenu = Instantiate(menuPrefab, menuPosition, menuRotation);

        ConfigureMenuOptions();
        Debug.Log("Menu spawned and locked in world space");
    }
    
    void CheckHoveredOption()
    {   
        if (nearFarInteractor.hasHover)
        {
            Debug.Log($"Hovering something! Count: {nearFarInteractor.interactablesHovered.Count}");
            
            foreach (var interactable in nearFarInteractor.interactablesHovered)
            {
                Debug.Log($"Interactable: {interactable.transform.name}");
            }
        }
        else
        {
            Debug.Log("Not hovering anything");
        }
        // Check what's being hovered
        if (nearFarInteractor.hasHover && nearFarInteractor.interactablesHovered.Count > 0)
        {
            var hoveredInteractable = nearFarInteractor.interactablesHovered[0];
            MenuOption option = hoveredInteractable.transform.GetComponent<MenuOption>();
            
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
    
    void ExecuteMenuOption()
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
    
    void OnDisable()
    {
        CloseMenu();
    }
}
