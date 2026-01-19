using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

[RequireComponent(typeof(SphereCollider))]
public class GoalVolume : MonoBehaviour
{
    [Header("Filtering")]
    public LayerMask includedLayers = ~0;
    public bool ignoreTriggerColliders = true;

    [Header("Task Mode Update Behavior")]
    public float holdSecondsToSetGoal = 5f;
    public bool showProgressUI = true;

    [Header("Visuals")]
    public Renderer volumeRenderer;
    public Color idleColor = new Color(1f, 1f, 1f, 0.2f);
    public Color armedColor = new Color(1f, 0.9f, 0.2f, 0.25f);
    public Color completeColor = new Color(0.2f, 1f, 0.2f, 0.35f);
    public bool driveRendererColor = true;

    [Header("UI (optional, auto-created if missing)")]
    public Transform uiRoot;
    public Canvas worldCanvas;
    public Image progressBG;
    public Image progressFill;
    public TMP_Text statusText;

    private readonly HashSet<GameObject> _inside = new();
    private HashSet<GameObject> _required = new();

    private HashSet<GameObject> _pendingCandidate;
    private float _pendingHeldTime;
    private bool _pendingActive;

    private SphereCollider _sphere;
    private SphereCollider _triggerCollider; 
    private bool _taskMode;
    private bool _playMode;
    private Camera _mainCamera;
    public event Action Completed;

    public bool IsCompleted { get; private set; }
    private Sprite _whiteSprite;
    // ---- Debug ----
    [Header("Debug")]
    public bool debugLogs = true;

    void Log(string msg)
    {
        if (!debugLogs) return;
        Debug.Log($"[GoalVolume:{name}] {msg}", this);
    }

    void Awake()
    {
        // 1. Auto-configure layers to "Interactable" and "InteractableNoCollision"
        int l1 = LayerMask.NameToLayer("Interactable");
        int l2 = LayerMask.NameToLayer("InteractableNoCollision");
        
        if (l1 != -1 || l2 != -1)
        {
            includedLayers = 0;
            if (l1 != -1) includedLayers |= (1 << l1);
            if (l2 != -1) includedLayers |= (1 << l2);
            Log($"Auto-set includedLayers mask: {includedLayers.value}");
        }

        // Find the trigger collider (search children first, then self)
        _triggerCollider = GetComponentInChildren<SphereCollider>();

        _sphere = _triggerCollider; // Keep reference

        var rb = GetComponent<Rigidbody>();
        if (rb == null) rb = gameObject.AddComponent<Rigidbody>();
        rb.isKinematic = true;
        rb.useGravity = false;

        if (volumeRenderer == null)
            volumeRenderer = GetComponentInChildren<Renderer>();

        Log($"Awake. triggerCollider='{_triggerCollider.gameObject.name}' isTrigger={_triggerCollider.isTrigger} rbKinematic={rb.isKinematic}");

        if (showProgressUI)
            EnsureUI();
    }

    void OnEnable()
    {
        Log("OnEnable.");

        if (ModeManager.Instance != null)
        {
            ModeManager.Instance.OnModeChanged += HandleModeChanged;
            Log($"Subscribed to ModeManager. CurrentMode={ModeManager.Instance.CurrentMode}");
            HandleModeChanged(ModeManager.Instance.CurrentMode);
        }
        else
        {
            Log("WARNING: ModeManager.Instance is null (no mode events will fire).");
        }
    }

    void OnDisable()
    {
        Log("OnDisable.");

        if (ModeManager.Instance != null)
            ModeManager.Instance.OnModeChanged -= HandleModeChanged;
    }

    void HandleModeChanged(Mode newMode)
    {
        _taskMode = newMode == Mode.Task;
        _playMode = newMode == Mode.Play;

        // Reset completion when leaving/entering modes
        if (!_playMode)
            IsCompleted = false;
            
        Log($"ModeChanged -> {newMode} (task={_taskMode}, play={_playMode})");
        
        if (_playMode)
        {
            Log("Disabled Interactions while in Play mode.");
            gameObject.layer = LayerMask.NameToLayer("InteractableNoCollision");
        }
        else if (_taskMode)
        {   
            Log("Disabled Interactions while in Task mode.");
            gameObject.layer = LayerMask.NameToLayer("InteractableNoCollision");
            RefreshInsideFromOverlap();
            _required = new HashSet<GameObject>(_inside);
            ClearPending();
            SetStatus("Goal snapshot saved");

            Log($"Task snapshot saved. requiredCount={_required.Count}, insideCount={_inside.Count}");
            if (debugLogs && _required.Count > 0)
                Log("Required: " + string.Join(", ", _required.Where(x => x != null).Select(x => x.name)));
        }
        else
        {
            ClearPending();
            SetStatus("");
        }

        UpdateVisualState();
        UpdateUI();
    }

    void Update()
    {
        if (ModeManager.Instance == null)
            return;

        if (_taskMode) TickTaskMode();
        else if (_playMode) TickPlayMode();

        UpdateVisualState();
        UpdateUI();
        UpdateBillboard();
    }

    void UpdateBillboard()
    {
        if (uiRoot == null) return;
        
        if (_mainCamera == null) _mainCamera = Camera.main;
        if (_mainCamera == null) return;

        // Rotate the UI root to face the camera.
        // Pointing the Z-axis away from the camera (pos - camPos) makes standard UI readable.
        uiRoot.rotation = Quaternion.LookRotation(uiRoot.position - _mainCamera.transform.position);
    }
    void TickTaskMode()
{
    var candidate = new HashSet<GameObject>(_inside);

    // Only start progress if something NEW entered (candidate is a strict superset of required)
    bool hasNew = candidate.Except(_required).Any();

    if (!hasNew)
    {
        if (_pendingActive)
            Log("No new objects entered; clearing pending.");
        ClearPending();
        return;
    }

    // If pending is not active or candidate changed, start/restart progress
    if (!_pendingActive || !SetsEqual(candidate, _pendingCandidate))
    {
        _pendingActive = true;
        _pendingCandidate = candidate;
        _pendingHeldTime = 0f;
        SetStatus("Hold to set goal...");
        Log($"Pending started/reset. candidateCount={candidate.Count}");
        return;
    }

    // Otherwise, accumulate progress
    _pendingHeldTime += Time.deltaTime;

    if (debugLogs && Mathf.Abs((_pendingHeldTime % 1f) - 0f) < 0.02f)
        Log($"Holding... {(_pendingHeldTime):0.00}/{holdSecondsToSetGoal:0.00}s");

    if (_pendingHeldTime >= holdSecondsToSetGoal)
    {
        _required = new HashSet<GameObject>(_pendingCandidate);
        ClearPending();
        SetStatus("Task goal set!");
        Log($"Task goal set. requiredCount={_required.Count}");
    }
}

    void TickPlayMode()
    {
        bool completeNow = _required.Count > 0 && _required.All(go => go != null && _inside.Contains(go));

        if (completeNow)
        {
            SetStatus("Complete");

            if (!IsCompleted)
            {
                IsCompleted = true;
                Completed?.Invoke();
                Log("Completed event fired.");
            }
        }
        else
        {
            SetStatus("");
            IsCompleted = false;
        }
    }

    void OnTriggerEnter(Collider other)
    {
        if (!PassesFilter(other))
            return;

        bool added = _inside.Add(other.gameObject);
        if (added)
            Log($"Enter: {other.gameObject.name}  insideCount={_inside.Count}");
    }

    void OnTriggerExit(Collider other)
    {
        if (!PassesFilter(other))
            return;

        bool removed = _inside.Remove(other.gameObject);
        if (removed)
            Log($"Exit: {other.gameObject.name}  insideCount={_inside.Count}");
    }

    bool PassesFilter(Collider c)
    {
        if (c == null) return false;
        if (ignoreTriggerColliders && c.isTrigger) return false;

        int layerBit = 1 << c.gameObject.layer;
        if ((includedLayers.value & layerBit) == 0) return false;

        if (c.transform.IsChildOf(transform)) return false;

        return true;
    }

        void RefreshInsideFromOverlap()
    {
        _inside.Clear();

        // Use the trigger collider (which might be on a child)
        if (_triggerCollider == null)
        {
            Log("ERROR: No trigger collider found. Skipping overlap snapshot.");
            return;
        }

        Vector3 center = _triggerCollider.bounds.center;
        // Use bounds.extents.x as the world-space radius (handles scaling)
        float radius = _triggerCollider.bounds.extents.x;

        var hits = Physics.OverlapSphere(center, radius, includedLayers, QueryTriggerInteraction.Ignore);

        foreach (var h in hits)
        {
            if (h == null) continue;
            if (!PassesFilter(h)) continue;
            _inside.Add(h.gameObject);
        }

        Log($"Overlap snapshot. hits={hits.Length}, insideCount={_inside.Count}");
    }

    static bool SetsEqual(HashSet<GameObject> a, HashSet<GameObject> b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a == null || b == null) return false;
        if (a.Count != b.Count) return false;
        return a.SetEquals(b);
    }

    void ClearPending()
    {
        _pendingActive = false;
        _pendingCandidate = null;
        _pendingHeldTime = 0f;
    }

    void UpdateVisualState()
    {
        if (!driveRendererColor || volumeRenderer == null)
            return;

        Color c = idleColor;

        if (_taskMode)
            c = _pendingActive ? armedColor : idleColor;
        else if (_playMode)
            c = (_required.Count > 0 && _required.All(go => go != null && _inside.Contains(go))) ? completeColor : idleColor;

        var mat = volumeRenderer.material;
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", c);
        else if (mat.HasProperty("_Color")) mat.SetColor("_Color", c);
    }

    void EnsureUI()
    {
        Log("EnsureUI (auto-create).");

        if (uiRoot == null)
        {
            var go = new GameObject("GoalVolumeUI");
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

            var scaler = cgo.AddComponent<CanvasScaler>();
            scaler.dynamicPixelsPerUnit = 200f;

            cgo.AddComponent<GraphicRaycaster>();

            var rt = (RectTransform)cgo.transform;
            rt.sizeDelta = new Vector2(0.35f, 0.12f);
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
            progressFill.sprite = _whiteSprite; // Assign sprite (CRITICAL for Filled type)
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
            // Fix existing instances that might be missing the sprite
            progressFill.sprite = _whiteSprite;
            progressFill.type = Image.Type.Filled;
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
                float duration = Mathf.Max(0.001f, holdSecondsToSetGoal);
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
    void SetStatus(string s)
    {
        if (statusText != null)
            statusText.text = s;
    }

    public int RequiredCount => _required.Count;
    public int InsideCount => _inside.Count;
}