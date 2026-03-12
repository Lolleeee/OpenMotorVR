using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

/// <summary>
/// Task-mode context menu spawner/executor (modeled after ContextMenu.cs).
/// Spawns a menu prefab at the controller, highlights hovered TaskMenuOption,
/// and executes on button release.
/// </summary>
public class TaskContextMenu : MonoBehaviour
{
    [Header("References")]
    public GameObject menuPrefab;
    public NearFarInteractor nearFarInteractor;
    public InputActionProperty menuButtonAction;

    [Header("Settings")]
    public Vector3 menuOffset = Vector3.zero;

    [Tooltip("If true, menu will only work in Task mode.")]
    public bool taskModeOnly = true;

    private GameObject _activeMenu;
    private TaskMenuOption _currentHighlightedOption;

    void OnEnable()
    {
        if (ModeManager.Instance != null)
            ModeManager.Instance.OnModeChanged += HandleModeChanged;

        if (menuButtonAction.action != null)
            menuButtonAction.action.Enable();
    }

    void Update()
    {
        if (taskModeOnly && ModeManager.Instance != null && !ModeManager.Instance.IsTaskMode)
        {
            CloseMenu();
            return;
        }

        float v = menuButtonAction.action != null ? menuButtonAction.action.ReadValue<float>() : 0f;
        bool isPressed = v > 0.5f;

        if (isPressed && _activeMenu == null)
        {
            OpenMenu();
        }
        else if (!isPressed && _activeMenu != null)
        {
            ExecuteMenuOption();
            CloseMenu();
        }
        else if (isPressed && _activeMenu != null)
        {
            CheckHoveredOption();
        }
    }

    void OpenMenu()
    {
        if (menuPrefab == null || nearFarInteractor == null)
            return;

        Transform attachTransform = nearFarInteractor.attachTransform != null ? nearFarInteractor.attachTransform : nearFarInteractor.transform;
        Vector3 menuPosition = attachTransform.position + attachTransform.TransformDirection(menuOffset);
        Quaternion menuRotation = attachTransform.rotation;

        _activeMenu = Instantiate(menuPrefab, menuPosition, menuRotation);
        _currentHighlightedOption = null;
    }

    void CheckHoveredOption()
    {
        if (nearFarInteractor == null)
            return;

        if (nearFarInteractor.hasHover && nearFarInteractor.interactablesHovered.Count > 0)
        {
            var hoveredInteractable = nearFarInteractor.interactablesHovered[0];
            TaskMenuOption option = hoveredInteractable.transform.GetComponent<TaskMenuOption>();

            if (option != _currentHighlightedOption)
            {
                if (_currentHighlightedOption != null)
                    _currentHighlightedOption.SetHighlighted(false);

                _currentHighlightedOption = option;

                if (_currentHighlightedOption != null)
                    _currentHighlightedOption.SetHighlighted(true);
            }
        }
        else
        {
            if (_currentHighlightedOption != null)
            {
                _currentHighlightedOption.SetHighlighted(false);
                _currentHighlightedOption = null;
            }
        }
    }

    void ExecuteMenuOption()
    {
        if (_currentHighlightedOption != null)
        {
            _currentHighlightedOption.Execute();
            Debug.Log($"Task menu executed: {_currentHighlightedOption.optionName}");
        }
        else
        {
            Debug.Log("Task menu closed - no option selected");
        }
    }

    void CloseMenu()
    {
        if (_activeMenu != null)
        {
            Destroy(_activeMenu);
            _activeMenu = null;
        }

        _currentHighlightedOption = null;
    }

    void OnDisable()
    {
        if (ModeManager.Instance != null)
            ModeManager.Instance.OnModeChanged -= HandleModeChanged;

        CloseMenu();

        if (menuButtonAction.action != null)
            menuButtonAction.action.Disable();
    }

    private void HandleModeChanged(Mode newMode)
    {
        bool isTaskMode = newMode == Mode.Task;
        if (!isTaskMode)
            CloseMenu();

        if (menuButtonAction.action != null)
        {
            if (isTaskMode) menuButtonAction.action.Enable();
            else menuButtonAction.action.Disable();
        }
    }
}
