using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

public class InspectorOption : MonoBehaviour
{
    [Header("Option Settings")]
    public string optionName = "Option";
    public UnityEvent<GameObject> onExecuteWithTarget; // target-aware action
    
    [Header("Status Indicator")]
    [Tooltip("Optional child object that renders check/cross via Text/TextMesh. Assign in the inspector for each option.")]
    public GameObject statusTextObject;

    [Tooltip("Symbol shown when the option's property is active.")]
    public string activeSymbol = "✔";

    [Tooltip("Symbol shown when the option's property is inactive.")]
    public string inactiveSymbol = "✖";

    public Color activeColor = Color.green;
    public Color inactiveColor = Color.red;

    [Header("Visual Feedback")]
    public MeshRenderer optionRenderer;
    public Color normalColor = Color.white;
    public Color highlightColor = Color.yellow;

    private GameObject targetObject;
    private IStatusWriter _statusWriter;
    private bool _statusDirty = true;
    private bool _lastStatusActive;

    void Start()
    {
        if (optionRenderer == null)
            optionRenderer = GetComponent<MeshRenderer>();
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

    public void Initialize(GameObject target)
    {
        targetObject = target;
        MarkStatusDirty();
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

        // Wiped options: only support the build-mode toggles below.
        string key = optionName.ToLower().Trim();
        switch (key)
        {
            case "kinematic":
            case "kinematics":
                {
                    var spawned = targetObject.GetComponent<SpawnedObject>();
                    if (spawned != null)
                    {
                        spawned.ToggleKinematic();
                    }
                    else
                    {
                        var rb = targetObject.GetComponent<Rigidbody>();
                        if (rb != null) rb.isKinematic = !rb.isKinematic;
                    }
                    break;
                }

            case "snap rotation":
            case "snap rotations":
            case "snaprotation":
                {
                    var spawned = targetObject.GetComponent<SpawnedObject>();
                    if (spawned != null)
                        spawned.ToggleSnapRotation();
                    else
                        Debug.LogWarning($"{targetObject.name} has no SpawnedObject component for snap rotation.");
                    break;
                }

            case "freeze rotation":
            case "freezerotation":
                {
                    var spawned = targetObject.GetComponent<SpawnedObject>();
                    if (spawned != null)
                        spawned.ToggleFreezeRotation();
                    else
                    {
                        var rb = targetObject.GetComponent<Rigidbody>();
                        if (rb != null)
                        {
                            bool freeze = (rb.constraints & RigidbodyConstraints.FreezeRotation) == 0;
                            rb.constraints = freeze ? (rb.constraints | RigidbodyConstraints.FreezeRotation) : (rb.constraints & ~RigidbodyConstraints.FreezeRotation);
                        }
                    }
                    break;
                }

            case "deactivate":
            case "disable":
                {
                    targetObject.SetActive(false);
                    Debug.Log($"Deactivated {targetObject.name}");
                    break;
                }

            default:
                Debug.LogWarning($"Unknown InspectorOption: '{optionName}'. Supported: kinematic, snap rotation, freeze rotation, deactivate.");
                break;
        }

        MarkStatusDirty();
    }

    private void MarkStatusDirty()
    {
        _statusDirty = true;
    }

    private bool EvaluateOptionState()
    {
        if (targetObject == null)
            return false;

        var spawned = targetObject.GetComponent<SpawnedObject>();
        var rb = targetObject.GetComponent<Rigidbody>();
        string key = optionName.ToLower().Trim();

        switch (key)
        {
            case "kinematic":
            case "kinematics":
                if (spawned != null)
                    return !spawned.kinematic;
                return rb != null && !rb.isKinematic;
            case "snap rotation":
            case "snap rotations":
            case "snaprotation":
                if (spawned != null)
                    return spawned.snapRotationEnabled;
                return false;
            case "freeze rotation":
            case "freezerotation":
                if (spawned != null)
                    return spawned.freezeRotationEnabled;
                return false;
            case "deactivate":
            case "disable":
                return !targetObject.activeSelf;
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

        var uiText = statusTextObject.GetComponent<Text>();
        if (uiText != null)
        {
            _statusWriter = new UITextWriter(uiText);
        }
    }

    private interface IStatusWriter
    {
        void Set(string text, Color color);
    }

    private class TextMeshWriter : IStatusWriter
    {
        private readonly TextMesh _textMesh;

        public TextMeshWriter(TextMesh textMesh)
        {
            _textMesh = textMesh;
        }

        public void Set(string text, Color color)
        {
            _textMesh.text = text;
            _textMesh.color = color;
        }
    }

    private class UITextWriter : IStatusWriter
    {
        private readonly Text _text;

        public UITextWriter(Text text)
        {
            _text = text;
        }

        public void Set(string text, Color color)
        {
            _text.text = text;
            _text.color = color;
        }
    }

    private class TMPTextWriter : IStatusWriter
    {
        private readonly TMP_Text _text;

        public TMPTextWriter(TMP_Text text)
        {
            _text = text;
        }

        public void Set(string text, Color color)
        {
            _text.text = text;
            _text.color = color;
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
