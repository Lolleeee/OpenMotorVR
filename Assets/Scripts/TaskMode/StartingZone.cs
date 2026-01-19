using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

[RequireComponent(typeof(BoxCollider))]
public class StartingZone : MonoBehaviour
{
    [Header("References")]
    public GoalVolume goalVolume;
    public RightControllerTracker rightControllerTracker;

    [Header("Filtering")]
    public LayerMask includedLayers = ~0;
    public bool ignoreTriggerColliders = true;

    [Header("Player (optional include)")]
    public bool includePlayerTag = true;
    public string playerTag = "Player";

    [Header("Task Mode Update Behavior")]
    public float holdSecondsToSetStart = 5f;

    [Header("Teleport")]
    [Tooltip("Extra padding inward from the boundary when placing objects.")]
    public float teleportPadding = 0.05f;

    [Header("UI (optional)")]
    public Transform uiRoot;
    public Canvas worldCanvas;
    public Image progressBG;
    public Image progressFill;
    public TMP_Text statusText;

    private Sprite _whiteSprite;

    private readonly HashSet<GameObject> _inside = new();
    private HashSet<GameObject> _required = new();

    private HashSet<GameObject> _pendingCandidate;
    private float _pendingHeldTime;
    private bool _pendingActive;

    private BoxCollider _triggerCollider;
    private bool _taskMode;
    private bool _playMode;
    private Camera _mainCamera;

    private bool _recordingStarted;

    [Header("Debug")]
    public bool debugLogs = true;

    void Log(string msg)
    {
        if (!debugLogs) return;
        Debug.Log($"[StartingZone:{name}] {msg}", this);
    }

    void Awake()
    {
        // Auto-configure layers to "Interactable" and "InteractableNoCollision"
        int l1 = LayerMask.NameToLayer("Interactable");
        int l2 = LayerMask.NameToLayer("InteractableNoCollision");

        if (l1 != -1 || l2 != -1)
        {
            includedLayers = 0;
            if (l1 != -1) includedLayers |= (1 << l1);
            if (l2 != -1) includedLayers |= (1 << l2);
        }

        _triggerCollider = GetComponentInChildren<BoxCollider>();

        if (_triggerCollider == null)
            _triggerCollider = gameObject.AddComponent<BoxCollider>();

        var rb = GetComponent<Rigidbody>();
        if (rb == null) rb = gameObject.AddComponent<Rigidbody>();
        rb.isKinematic = true;
        rb.useGravity = false;

        EnsureUI();

        if (goalVolume == null)
            goalVolume = FindFirstObjectByType<GoalVolume>();
        if (rightControllerTracker == null)
            rightControllerTracker = FindFirstObjectByType<RightControllerTracker>();
        var grab = GetComponent<UnityEngine.XR.Interaction.Toolkit.Interactables.XRGrabInteractable>();
        if (grab != null)
        {
            grab.trackRotation = false;
        }
    }

    void OnEnable()
    {
        if (ModeManager.Instance != null)
        {
            ModeManager.Instance.OnModeChanged += HandleModeChanged;
            HandleModeChanged(ModeManager.Instance.CurrentMode);
        }
    }

    void OnDisable()
    {
        if (ModeManager.Instance != null)
            ModeManager.Instance.OnModeChanged -= HandleModeChanged;
    }

    void HandleModeChanged(Mode newMode)
    {
        _taskMode = newMode == Mode.Task;
        _playMode = newMode == Mode.Play;

        _recordingStarted = false;
        ClearPending();

        if (_taskMode)
        {   
            Log("Disabled Interactions while in Task mode.");
            SetLayerRecursively(gameObject, LayerMask.NameToLayer("Default"));
            RefreshInsideFromOverlap();
            _required = new HashSet<GameObject>(_inside);
            SetStatus("Start snapshot saved");
            Log($"Task snapshot saved. requiredCount={_required.Count}");
        }
        else if (_playMode)
        {   
            Log("Disabled Interactions while in Play mode.");
            SetLayerRecursively(gameObject, LayerMask.NameToLayer("Default"));
            TeleportRequiredIntoZone();
            RefreshInsideFromOverlap(); // ensure _inside matches teleported state
            SetStatus("Leave start zone to begin");
            Log("Entered Play mode: teleported required objects into zone.");

            if (rightControllerTracker != null)
                rightControllerTracker.ArmStopOnGoalComplete(goalVolume);
        }
        else
        {   
            Log("Enabled Interactions while in Build mode.");
            SetLayerRecursively(gameObject, LayerMask.NameToLayer("InteractableNoCollision"));
            SetStatus("");
        }

        UpdateUI();
    }

    void Update()
    {
        if (ModeManager.Instance == null)
            return;

        if (_taskMode)
            TickTaskMode();
        else if (_playMode)
            TickPlayMode();

        UpdateBillboard();
        UpdateUI();
    }

    void TickTaskMode()
    {
        var candidate = new HashSet<GameObject>(_inside);

        // Only respond to new objects entering (not leaving)
        bool hasNew = candidate.Except(_required).Any();
        if (!hasNew)
        {
            ClearPending();
            return;
        }

        if (!_pendingActive || !candidate.SetEquals(_pendingCandidate))
        {
            _pendingActive = true;
            _pendingCandidate = candidate;
            _pendingHeldTime = 0f;
            SetStatus("Hold to set start...");
            return;
        }

        _pendingHeldTime += Time.deltaTime;

        if (_pendingHeldTime >= holdSecondsToSetStart)
        {
            _required = new HashSet<GameObject>(_pendingCandidate);
            ClearPending();
            SetStatus("Start set!");
            Log($"Start set. requiredCount={_required.Count}");
        }
    }

    void TickPlayMode()
    {
        if (_recordingStarted)
            return;

        // Start recording when ALL required objects have left the zone
        if (_required.Count == 0)
            return;

        bool anyRequiredStillInside = _required.Any(go => go != null && _inside.Contains(go));
        if (!anyRequiredStillInside)
        {
            _recordingStarted = true;
            SetStatus(""); // optional: hide text once started

            if (rightControllerTracker != null)
                rightControllerTracker.StartTracking();

            Log("All required objects left the start zone -> recording started.");
        }
    }

    void OnTriggerEnter(Collider other)
    {
        if (!PassesFilter(other))
            return;

        _inside.Add(other.gameObject);
    }

    void OnTriggerExit(Collider other)
    {
        if (!PassesFilter(other))
            return;

        _inside.Remove(other.gameObject);
    }

    bool PassesFilter(Collider c)
    {
        if (c == null) return false;
        if (ignoreTriggerColliders && c.isTrigger) return false;
        if (c.transform.IsChildOf(transform)) return false;

        var go = c.gameObject;

        if (includePlayerTag && !string.IsNullOrWhiteSpace(playerTag) && go.CompareTag(playerTag))
            return true;

        int layerBit = 1 << go.layer;
        return (includedLayers.value & layerBit) != 0;
    }

        void RefreshInsideFromOverlap()
    {
        _inside.Clear();
        if (_triggerCollider == null) return;

        // FIX: Use OverlapBox instead of Sphere to match the BoxCollider exactly
        Vector3 center = _triggerCollider.transform.TransformPoint(_triggerCollider.center);
        Vector3 halfExtents = _triggerCollider.size * 0.5f;
        Quaternion rotation = _triggerCollider.transform.rotation;

        var hits = Physics.OverlapBox(center, halfExtents, rotation, includedLayers, QueryTriggerInteraction.Ignore);
        
        foreach (var h in hits)
        {
            if (h == null) continue;
            if (!PassesFilter(h)) continue;
            _inside.Add(h.gameObject);
        }

        if (includePlayerTag && !string.IsNullOrWhiteSpace(playerTag))
        {
            foreach (var p in GameObject.FindGameObjectsWithTag(playerTag))
            {
                if (p == null) continue;
                
                // FIX: Check if player point is inside the OBB (Oriented Bounding Box)
                Vector3 localP = _triggerCollider.transform.InverseTransformPoint(p.transform.position) - _triggerCollider.center;
                if (Mathf.Abs(localP.x) <= halfExtents.x && 
                    Mathf.Abs(localP.y) <= halfExtents.y && 
                    Mathf.Abs(localP.z) <= halfExtents.z)
                {
                    _inside.Add(p);
                }
            }
        }
    }

        void TeleportRequiredIntoZone()
    {
        if (_triggerCollider == null) return;

        var list = _required.Where(go => go != null && go != gameObject).ToList();
        if (list.Count == 0) return;

        // FIX: Use local size/center to handle rotation correctly
        Vector3 localCenter = _triggerCollider.center;
        // Calculate local half-extents with padding
        Vector3 localHalfSize = (_triggerCollider.size * 0.5f) - (Vector3.one * teleportPadding);
        // Ensure we don't invert dimensions if padding is too large
        localHalfSize = Vector3.Max(localHalfSize, Vector3.zero);

        int grid = Mathf.CeilToInt(Mathf.Pow(list.Count, 1f / 3f));
        for (int i = 0; i < list.Count; i++)
        {
            int x = i % grid;
            int y = (i / grid) % grid;
            int z = (i / (grid * grid)) % grid;

            // Calculate position in local space relative to the collider center
            Vector3 localPos = new Vector3(
                Mathf.Lerp(-localHalfSize.x, localHalfSize.x, (x + 0.5f) / grid),
                Mathf.Lerp(-localHalfSize.y, localHalfSize.y, (y + 0.5f) / grid),
                Mathf.Lerp(-localHalfSize.z, localHalfSize.z, (z + 0.5f) / grid)
            );

            // Transform local position to world space
            Vector3 targetPos = _triggerCollider.transform.TransformPoint(localCenter + localPos);
            Teleport(list[i], targetPos);
            Log($"Teleported '{list[i].name}' to {targetPos}");
}
    }

    static float ApproxObjectRadius(GameObject go)
    {
        if (go == null) return 0.25f;

        // Prefer non-trigger colliders
        var cols = go.GetComponentsInChildren<Collider>(true);
        foreach (var c in cols)
        {
            if (c == null || c.isTrigger) continue;
            var e = c.bounds.extents;
            return Mathf.Max(e.x, e.y, e.z);
        }

        // Fallback
        return 0.25f;
    }

    static void Teleport(GameObject go, Vector3 targetPos)
    {
        if (go == null) return;

        // CharacterController needs to be disabled to move safely
        var cc = go.GetComponent<CharacterController>();
        if (cc != null) cc.enabled = false;

        var rb = go.GetComponent<Rigidbody>();
        if (rb != null)
        {
            rb.position = targetPos;
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            rb.Sleep();
        }
        else
        {
            go.transform.position = targetPos;
        }

        if (cc != null) cc.enabled = true;
    }

    void ClearPending()
    {
        _pendingActive = false;
        _pendingCandidate = null;
        _pendingHeldTime = 0f;
    }

    void SetStatus(string s)
    {
        if (statusText != null)
            statusText.text = s;
    }

    void UpdateBillboard()
    {
        if (uiRoot == null) return;
        if (_mainCamera == null) _mainCamera = Camera.main;
        if (_mainCamera == null) return;

        uiRoot.rotation = Quaternion.LookRotation(uiRoot.position - _mainCamera.transform.position);
    }

    void EnsureUI()
    {
        if (uiRoot == null)
        {
            var go = new GameObject("StartingZoneUI");
            go.transform.SetParent(transform, false);
            go.transform.localPosition = new Vector3(0f, 0.6f, 0f);
            uiRoot = go.transform;
        }

        if (worldCanvas == null)
        {
            var cgo = new GameObject("Canvas");
            cgo.transform.SetParent(uiRoot, false);
            worldCanvas = cgo.AddComponent<Canvas>();
            worldCanvas.renderMode = RenderMode.WorldSpace;
        }

        // Helper to ensure we have a sprite (Filled mode requires it)
        if (_whiteSprite == null)
        {
            Texture2D tex = new Texture2D(2, 2);
            Color[] cols = new Color[4];
            for (int i = 0; i < 4; i++) cols[i] = Color.white;
            tex.SetPixels(cols);
            tex.Apply();
            _whiteSprite = Sprite.Create(tex, new Rect(0, 0, 2, 2), new Vector2(0.5f, 0.5f));
        }

        if (progressFill == null)
        {
            var bg = new GameObject("ProgressBG");
            bg.transform.SetParent(worldCanvas.transform, false);
            var bgImg = bg.AddComponent<Image>();
            progressBG = bgImg;
            bgImg.sprite = _whiteSprite;
            bgImg.color = new Color(0f, 0f, 0f, 0.35f);
            var bgRt = (RectTransform)bg.transform;
            bgRt.anchorMin = new Vector2(0.05f, 0.15f);
            bgRt.anchorMax = new Vector2(0.95f, 0.45f);
            bgRt.offsetMin = bgRt.offsetMax = Vector2.zero;

            var fill = new GameObject("ProgressFill");
            fill.transform.SetParent(bg.transform, false);
            progressFill = fill.AddComponent<Image>();
            progressFill.sprite = _whiteSprite;
            progressFill.color = new Color(1f, 0.9f, 0.2f, 0.9f);
            progressFill.type = Image.Type.Filled;
            progressFill.fillMethod = Image.FillMethod.Horizontal;
            progressFill.fillOrigin = 0;
            progressFill.fillAmount = 0f;

            var fillRt = (RectTransform)fill.transform;
            fillRt.anchorMin = Vector2.zero;
            fillRt.anchorMax = Vector2.one;
            fillRt.offsetMin = fillRt.offsetMax = Vector2.zero;
        }
        else if (progressFill.sprite == null)
        {
            progressFill.sprite = _whiteSprite;
            progressFill.type = Image.Type.Filled;
        }

        if (statusText == null)
        {
            var tgo = new GameObject("StatusText");
            tgo.transform.SetParent(worldCanvas.transform, false);
            statusText = tgo.AddComponent<TextMeshProUGUI>();
            statusText.fontSize = 3.5f;
            statusText.alignment = TextAlignmentOptions.Center;
            statusText.color = Color.white;
        }
    }

    void UpdateUI()
    {
        if (worldCanvas == null || statusText == null)
            return;

        // Show canvas if in Task or Play mode
        bool shouldShow = _taskMode || _playMode;
        worldCanvas.enabled = shouldShow;

        // Always show the text
        statusText.enabled = true;

        // Progress bar: only show in Task Mode when actively holding a candidate
        if (progressFill != null && progressBG != null)
        {
            if (_taskMode && _pendingActive)
            {
                progressBG.enabled = true;
                progressFill.enabled = true;
                float duration = Mathf.Max(0.001f, holdSecondsToSetStart);
                float p = Mathf.Clamp01(_pendingHeldTime / duration);
                progressFill.fillAmount = p;
            }
            else
            {
                progressBG.enabled = false;
                progressFill.enabled = false;
                progressFill.fillAmount = 0f;
            }
        }
    }

    void SetLayerRecursively(GameObject root, int layer)
    {
        if (root == null || layer < 0) return;

        foreach (var t in root.GetComponentsInChildren<Transform>(true))
        {
            t.gameObject.layer = layer;
        }
    }
}