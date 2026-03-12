using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

public class PlayManager : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Source of starting object + fallback raw trajectory.")]
    public TaskManager taskManager;

    [Tooltip("Source of trajectory visualization settings (ring radius/stride/material) and play trigger sizes.")]
    public TaskTrajectoryEditor trajectoryEditor;

	[Tooltip("Optional: records/replays HMD + controller motion as a ghost rig.")]
	public ShadowManager shadowManager;

    [Header("PlayMode Object")]
    [Tooltip("The object the player will grab and move through the rings. If empty, will use TaskManager.StartingObject.")]
    public GameObject startingObject;

    [Tooltip("Optional override. If null, will record startingObject.transform.")]
    public Transform trackedTransform;

    [Header("Teleport")]
    public bool teleportStartingObjectOnEnterPlayMode = true;

    [Header("PlayMode Runtime Root")]
    [Tooltip("Root object used for PlayMode-only runtime guides/triggers.")]
    public string playWorldRootName = "PlayTrajectoryReplay";

    [Header("Recording (PlayMode)")]
    [Tooltip("Samples per second while the run is active.")]
    public float samplingRate = 30f;

    [Tooltip("Subfolder under persistentDataPath.")]
    public string outputFolderName = "Tracking";

    public string outputFilePrefix = "playmode_trajectory";

    [Header("State (read-only)")]
    [SerializeField] private bool isActive;
    [SerializeField] private bool runStarted;
    [SerializeField] private bool runCompleted;
    [SerializeField] private int currentRingIndex;

    public bool IsActive => isActive;

    [Header("Diagnostics")]
    [Tooltip("If true, logs warnings when play guides are invisible or cannot start.")]
    public bool logDiagnostics = true;

	[Header("Interactor Gating (PlayMode)")]
	[Tooltip("If true, disables NearFarInteractor far casting while in PlayMode (equivalent to unchecking 'Enable Far Casting').")]
	public bool disableNearFarFarCastingInPlayMode = true;

    public void SetStartingObject(GameObject obj)
    {
        if (obj == null)
            return;

        startingObject = obj;

        // If the caller hasn't provided an override transform, track the starting object.
        if (trackedTransform == null)
            trackedTransform = startingObject.transform;
    }

    private Transform _root;
    private readonly List<XRBaseInteractable> _startingInteractables = new();
    private Coroutine _samplingRoutine;
    private float _runStartTime;
    private readonly List<TrajectorySample> _samples = new();
    private readonly List<GameObject> _ringObjects = new();
    private GameObject _endTriggerObject;

    private readonly List<Collider> _startingObjectColliders = new();

    private float _lastProgressTime;

    private Vector3 _startPosition;
    private Quaternion _startRotation;
    private Vector3 _endPosition;
    private List<Vector3> _ringPositions = new();
    private List<Vector3> _ringScales = new();

    private bool _modeHooked;
    private readonly List<NearFarFarCastingState> _nearFarCastingCache = new();

    private struct NearFarFarCastingState
    {
        public NearFarInteractor interactor;
        public bool wasFarCastingEnabled;
        public NearFarFarCastingState(NearFarInteractor interactor, bool wasFarCastingEnabled)
        {
            this.interactor = interactor;
            this.wasFarCastingEnabled = wasFarCastingEnabled;
        }
    }

    [Serializable]
    private struct TrajectorySample
    {
        public float t;
        public Vector3 position;
        public Quaternion rotation;

        public TrajectorySample(float t, Vector3 position, Quaternion rotation)
        {
            this.t = t;
            this.position = position;
            this.rotation = rotation;
        }
    }

    void OnEnable()
    {
        if (taskManager == null)
            taskManager = FindFirstObjectByType<TaskManager>();

        if (trajectoryEditor == null)
            trajectoryEditor = FindFirstObjectByType<TaskTrajectoryEditor>();

		if (shadowManager == null)
			shadowManager = FindFirstObjectByType<ShadowManager>();

        EnsureModeHooked();
    }

    void Start()
    {
        EnsureModeHooked();
        ApplyMode(ModeManager.Instance != null ? ModeManager.Instance.CurrentMode : Mode.Build);
    }

    void Update()
    {
        // In some scene setups, PlayManager can enable before ModeManager.Awake sets Instance.
        // Ensure we hook up as soon as the ModeManager exists.
        EnsureModeHooked();

        if (!isActive || !runStarted || runCompleted)
            return;

        // Fallback progression that doesn't rely on the Physics layer collision matrix.
        // If triggers are blocked (e.g. InteractableNoCollision vs InteractableNoCollision),
        // proximity checks still allow the run to advance.
        ProximityProgressFallback();
    }

    void OnDisable()
    {
        if (_modeHooked && ModeManager.Instance != null)
            ModeManager.Instance.OnModeChanged -= HandleModeChanged;
        _modeHooked = false;

        ApplyMode(Mode.Build);
    }

    private void EnsureModeHooked()
    {
        if (_modeHooked)
            return;

        if (ModeManager.Instance == null)
            return;

        ModeManager.Instance.OnModeChanged += HandleModeChanged;
        _modeHooked = true;

        // Immediately sync to current mode.
        ApplyMode(ModeManager.Instance.CurrentMode);
    }

    [UnityEngine.ContextMenu("DEBUG/Force Enter PlayMode")]
    private void DebugForceEnterPlayMode()
    {
        ApplyMode(Mode.Play);
        if (logDiagnostics)
            Debug.Log("PlayManager DEBUG: Forced Mode.Play; should create PlayTrajectoryReplay root if startingObject + trajectory exist.");
    }

    private void HandleModeChanged(Mode newMode) => ApplyMode(newMode);

    private void ApplyMode(Mode mode)
    {
        bool shouldBeActive = mode == Mode.Play;
        if (shouldBeActive == isActive)
            return;

        if (shouldBeActive)
            EnterPlayMode();
        else
            ExitPlayMode();
    }

    private void EnterPlayMode()
    {
        isActive = true;
        runStarted = false;
        runCompleted = false;
        currentRingIndex = 0;
        _samples.Clear();

        if (taskManager == null)
            taskManager = FindFirstObjectByType<TaskManager>();
        if (trajectoryEditor == null)
            trajectoryEditor = FindFirstObjectByType<TaskTrajectoryEditor>();

        if (shadowManager == null)
            shadowManager = FindFirstObjectByType<ShadowManager>();

        // Starting object comes from TaskManager at runtime.
        if (taskManager != null && taskManager.StartingObject != null)
            startingObject = taskManager.StartingObject;

        // Fallback: if TaskManager has no concrete object reference (e.g. after loading a saved recording),
        // try to rebind using the last remembered SpawnedObject persistentId.
        if (startingObject == null && taskManager != null)
        {
            string id = PlayerPrefs.GetString("LastTaskStartingObjectPersistentId", "");
            if (!string.IsNullOrWhiteSpace(id))
            {
                var spawnedObjects = FindObjectsOfType<SpawnedObject>(true);
                for (int i = 0; i < spawnedObjects.Length; i++)
                {
                    var so = spawnedObjects[i];
                    if (so == null) continue;
                    if (string.Equals(so.persistentId, id, StringComparison.Ordinal))
                    {
                        taskManager.BindStartingObject(so.gameObject);
                        startingObject = taskManager.StartingObject;
                        break;
                    }
                }
            }
        }

        if (startingObject == null)
        {
            Debug.LogWarning("PlayManager: startingObject is null (TaskManager has none and inspector field not set). Play mode guidance will not start.");
            return;
        }

        DisableNearFarFarCastingForPlayMode();

        // If the caller hasn't provided an override transform, track the starting object.
        if (trackedTransform == null)
            trackedTransform = startingObject.transform;

        if (shadowManager != null)
        {
            // Record immediately when entering PlayMode (captures player view + controllers) AND the tracked object.
            shadowManager.TryAutoBindSources();
            shadowManager.EnsureExtraSource(trackedTransform);
            shadowManager.BeginRecording();
        }

        // Re-apply the saved SpawnedObject settings (gravity, collision, freeze rotation, etc).
        ApplyStartingObjectSavedSettings();

        CacheStartingObjectColliders();

        if (trackedTransform == null)
            trackedTransform = startingObject.transform;

        if (!TryResolveSavedTask(out _startPosition, out _startRotation, out _ringPositions, out _ringScales, out _endPosition))
        {
            Debug.LogWarning("PlayManager: No saved task trajectory found (TaskTrajectoryEdits or TaskManager). Play mode guidance will be empty.");
            _ringPositions = new List<Vector3>();
            _ringScales = new List<Vector3>();
            _endPosition = startingObject.transform.position;
            _startPosition = startingObject.transform.position;
            _startRotation = startingObject.transform.rotation;
        }

        if (teleportStartingObjectOnEnterPlayMode)
            TeleportStartingObject(_startPosition, _startRotation);

        EnsureRoot();
        BuildPlayGuides();
        HookStartingObjectGrab(true);

        if (logDiagnostics)
            DiagnoseGuideVisibility();

        // If the player is already holding the object when entering PlayMode,
        // we won't get a selectEntered event. Start immediately in that case.
        TryBeginRunIfAlreadySelected();
    }

    private void ExitPlayMode()
    {
        isActive = false;
		if (shadowManager != null)
		{
			shadowManager.StopRecording();
			shadowManager.StopReplay();
		}
		RestoreNearFarFarCasting();
        HookStartingObjectGrab(false);
        StopSampling();
        ClearGuides();
        runStarted = false;
        runCompleted = false;
        currentRingIndex = 0;
        _lastProgressTime = 0f;
		_startingObjectColliders.Clear();
    }

    private void DisableNearFarFarCastingForPlayMode()
    {
        if (!disableNearFarFarCastingInPlayMode)
            return;

        _nearFarCastingCache.Clear();
        var nearFars = FindObjectsOfType<NearFarInteractor>(true);
        for (int i = 0; i < nearFars.Length; i++)
        {
            var nf = nearFars[i];
            if (nf == null)
                continue;
            _nearFarCastingCache.Add(new NearFarFarCastingState(nf, nf.enableFarCasting));
            nf.enableFarCasting = false;
        }
    }

    private void RestoreNearFarFarCasting()
    {
        if (_nearFarCastingCache.Count == 0)
            return;
        for (int i = 0; i < _nearFarCastingCache.Count; i++)
        {
            var state = _nearFarCastingCache[i];
            if (state.interactor != null)
                state.interactor.enableFarCasting = state.wasFarCastingEnabled;
        }
        _nearFarCastingCache.Clear();
    }

    private void EnsureRoot()
    {
        if (_root != null)
            return;

        var existing = GameObject.Find(playWorldRootName);
        if (existing == null)
            existing = new GameObject(playWorldRootName);

        _root = existing.transform;
    }

    private void ClearGuides()
    {
        for (int i = 0; i < _ringObjects.Count; i++)
        {
            if (_ringObjects[i] != null)
                Destroy(_ringObjects[i]);
        }
        _ringObjects.Clear();

        if (_endTriggerObject != null)
            Destroy(_endTriggerObject);
        _endTriggerObject = null;
    }

    private void BuildPlayGuides()
    {
        ClearGuides();

        if (_ringPositions == null)
            _ringPositions = new List<Vector3>();
        if (_ringScales == null)
            _ringScales = new List<Vector3>();

        // Ring density/stride is owned by TaskTrajectoryEditor.
        int stride = 1;
        if (trajectoryEditor != null)
            stride = trajectoryEditor.showMarkerForEverySample ? 1 : Mathf.Max(1, trajectoryEditor.ringStride);

        var usedRingPositions = _ringPositions.Where((p, idx) => idx % stride == 0).ToList();
        var usedRingScales = _ringScales.Count == _ringPositions.Count
            ? _ringScales.Where((s, idx) => idx % stride == 0).ToList()
            : null;

        for (int i = 0; i < usedRingPositions.Count; i++)
        {
            Vector3 desiredLossyScale = Vector3.one;
            if (usedRingScales != null && i >= 0 && i < usedRingScales.Count)
                desiredLossyScale = usedRingScales[i];

            var ring = CreateRing($"PlayRing_{i}", usedRingPositions[i], i, desiredLossyScale);

            // Orient ring plane perpendicular to the trajectory direction.
            Vector3 direction;
            if (i < usedRingPositions.Count - 1)
                direction = usedRingPositions[i + 1] - usedRingPositions[i];
            else if (i > 0)
                direction = usedRingPositions[i] - usedRingPositions[i - 1];
            else
                direction = Vector3.forward;

            if (direction.sqrMagnitude > 0.0001f)
                ring.transform.rotation = Quaternion.FromToRotation(Vector3.up, direction.normalized);

            ring.SetActive(false);
            _ringObjects.Add(ring);
        }

        _endTriggerObject = CreateEndTrigger("PlayEndPoint", _endPosition);
        _endTriggerObject.SetActive(true);
    }

    private Material ResolveRingMaterial()
    {
        if (trajectoryEditor != null && trajectoryEditor.lineMaterial != null)
            return trajectoryEditor.lineMaterial;

        // Fallback material (not user-facing setting).
        var shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null)
            shader = Shader.Find("Unlit/Color");
        if (shader == null)
            return null;

        var mat = new Material(shader);
        mat.enableInstancing = true;
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.white);
        if (mat.HasProperty("_Color")) mat.SetColor("_Color", Color.white);
        return mat;
    }

    private GameObject CreateRing(string name, Vector3 position, int ringIndex, Vector3 desiredLossyScale)
    {
        float ringRadius = trajectoryEditor != null ? trajectoryEditor.ringRadius : 0.06f;
        float ringLineWidth = trajectoryEditor != null ? trajectoryEditor.ringLineWidth : 0.006f;
        int ringSegments = trajectoryEditor != null ? trajectoryEditor.ringSegments : 48;
        float ringTriggerRadius = trajectoryEditor != null ? trajectoryEditor.playRingTriggerRadius : 0.08f;

        var go = new GameObject(name);
        go.transform.SetParent(_root, worldPositionStays: true);
        go.transform.position = position;

        // Preserve ring scaling edits from Task mode.
        // desiredLossyScale comes from the TaskTrajectoryEditor ring transform (typically uniform).
        // Convert world scale -> local scale under our runtime root.
        if (desiredLossyScale == default)
            desiredLossyScale = Vector3.one;
        go.transform.localScale = _root != null ? ComputeRelativeScale(_root.lossyScale, desiredLossyScale) : desiredLossyScale;

        var lr = go.AddComponent<LineRenderer>();
        lr.useWorldSpace = false;
        lr.loop = true;
        lr.widthMultiplier = ringLineWidth;
        lr.positionCount = Mathf.Max(12, ringSegments);
        var mat = ResolveRingMaterial();
        if (mat != null)
            lr.sharedMaterial = mat;

        var pts = new Vector3[lr.positionCount];
        for (int i = 0; i < pts.Length; i++)
        {
            float a = (i / (float)pts.Length) * Mathf.PI * 2f;
            pts[i] = new Vector3(Mathf.Cos(a) * ringRadius, 0f, Mathf.Sin(a) * ringRadius);
        }
        lr.SetPositions(pts);

        // Trigger hitbox: thin disc-like box in the ring plane (better than a sphere).
        float triggerRadius = Mathf.Max(ringRadius, ringTriggerRadius);
        float thickness = Mathf.Max(0.02f, ringLineWidth * 6f);
        var col = go.AddComponent<BoxCollider>();
        col.isTrigger = true;
        col.center = Vector3.zero;
        col.size = new Vector3(triggerRadius * 2f, thickness, triggerRadius * 2f);

        // Set to a non-collidable layer to avoid physics interference.
        int layer = LayerMask.NameToLayer("InteractableNoCollision");
        if (layer >= 0)
            go.layer = layer;
        else if (logDiagnostics)
            Debug.LogWarning("PlayManager: Layer 'InteractableNoCollision' not found; ring will remain on Default layer.");

        var trigger = go.AddComponent<PlayRingTrigger>();
        trigger.manager = this;
        trigger.ringIndex = ringIndex;

        return go;
    }

    private static Vector3 ComputeRelativeScale(Vector3 parentLossyScale, Vector3 childLossyScale)
    {
        float sx = Mathf.Abs(parentLossyScale.x) > 1e-6f ? (childLossyScale.x / parentLossyScale.x) : 1f;
        float sy = Mathf.Abs(parentLossyScale.y) > 1e-6f ? (childLossyScale.y / parentLossyScale.y) : 1f;
        float sz = Mathf.Abs(parentLossyScale.z) > 1e-6f ? (childLossyScale.z / parentLossyScale.z) : 1f;
        return new Vector3(sx, sy, sz);
    }

    private GameObject CreateEndTrigger(string name, Vector3 position)
    {
        float endTriggerRadius = trajectoryEditor != null ? trajectoryEditor.playEndTriggerRadius : 0.12f;

        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = name;
        go.transform.SetParent(_root, worldPositionStays: true);
        go.transform.position = position;
        go.transform.localScale = Vector3.one * (endTriggerRadius * 2f);

        var col = go.GetComponent<SphereCollider>();
        col.isTrigger = true;
        col.radius = 0.5f; // because we scale the sphere

        var rb = go.AddComponent<Rigidbody>();
        // Set to a non-collidable layer to avoid physics interference.
        int layer = LayerMask.NameToLayer("InteractableNoCollision");
        if (layer >= 0)
            go.layer = layer;
        else if (logDiagnostics)
            Debug.LogWarning("PlayManager: Layer 'InteractableNoCollision' not found; end trigger will remain on Default layer.");

        rb.isKinematic = true;
        rb.useGravity = false;

        var trigger = go.AddComponent<PlayEndTrigger>();
        trigger.manager = this;

        return go;
    }

    private void ApplyStartingObjectSavedSettings()
    {
        if (startingObject == null)
            return;

        var rb = startingObject.GetComponentInParent<Rigidbody>() ?? startingObject.GetComponent<Rigidbody>();
        if (rb != null)
        {
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }

        // Ensure SpawnedObject is enabled and applies its persisted settings.
        var spawnedObject = startingObject.GetComponentInParent<SpawnedObject>() ?? startingObject.GetComponent<SpawnedObject>();
        if (spawnedObject != null)
        {
            spawnedObject.enabled = true;
            spawnedObject.ApplySavedSettings();
        }

        // Ensure XR interactables are enabled.
        var interactables = startingObject.GetComponentsInChildren<UnityEngine.XR.Interaction.Toolkit.Interactables.XRBaseInteractable>(true);
        foreach (var interactable in interactables)
        {
            if (interactable != null)
                interactable.enabled = true;
        }

        if (rb != null)
            rb.WakeUp();
    }

    private void CacheStartingObjectColliders()
    {
        _startingObjectColliders.Clear();
        if (startingObject == null)
            return;

        var cols = startingObject.GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < cols.Length; i++)
        {
            var c = cols[i];
            if (c == null)
                continue;
            // Ignore disabled colliders and triggers on the held object (we want its physical presence).
            if (!c.enabled)
                continue;
            _startingObjectColliders.Add(c);
        }
    }

    private void TeleportStartingObject(Vector3 position, Quaternion rotation)
    {
        startingObject.transform.SetPositionAndRotation(position, rotation);

        var rb = startingObject.GetComponentInParent<Rigidbody>() ?? startingObject.GetComponent<Rigidbody>();
        if (rb != null)
        {
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            rb.WakeUp();
        }
    }

    private void HookStartingObjectGrab(bool hook)
    {
        if (_startingInteractables.Count > 0)
        {
            for (int i = 0; i < _startingInteractables.Count; i++)
            {
                var it = _startingInteractables[i];
                if (it == null)
                    continue;
                it.selectEntered.RemoveListener(HandleStartingObjectGrabbed);
                it.selectExited.RemoveListener(HandleStartingObjectReleased);
            }
            _startingInteractables.Clear();
        }

        if (!hook || startingObject == null)
            return;

        // Hook ALL interactables under the starting object. Depending on SpawnedObject settings,
        // the active interactable might be XRGrabInteractable or XRSimpleInteractable.
        var interactables = startingObject.GetComponentsInChildren<XRBaseInteractable>(true);
        for (int i = 0; i < interactables.Length; i++)
        {
            var it = interactables[i];
            if (it == null)
                continue;

            it.selectEntered.AddListener(HandleStartingObjectGrabbed);
            it.selectExited.AddListener(HandleStartingObjectReleased);
            _startingInteractables.Add(it);
        }

        if (_startingInteractables.Count == 0)
            Debug.LogWarning("PlayManager: startingObject has no XRBaseInteractable; cannot detect grab to start run.");
    }

    private void TryBeginRunIfAlreadySelected()
    {
        if (!isActive || runCompleted)
            return;

        for (int i = 0; i < _startingInteractables.Count; i++)
        {
            var it = _startingInteractables[i];
            if (it == null)
                continue;

            if (it.isSelected)
            {
                BeginRunIfNeeded();
                ShowCurrentRing();
                return;
            }
        }
    }

    private void DiagnoseGuideVisibility()
    {
        int layer = LayerMask.NameToLayer("InteractableNoCollision");
        if (layer < 0)
            return;

        var cam = Camera.main;
        if (cam == null)
            return;

        int mask = cam.cullingMask;
        bool visible = (mask & (1 << layer)) != 0;
        if (!visible)
        {
            Debug.LogWarning(
                "PlayManager: Camera.main is not rendering layer 'InteractableNoCollision'. " +
                "Play rings/end trigger will be invisible. Add that layer to the camera culling mask.");
        }
    }

    private void HandleStartingObjectGrabbed(SelectEnterEventArgs _)
    {
        if (!isActive || runCompleted)
            return;

        BeginRunIfNeeded();
        ShowCurrentRing();
    }

    private void HandleStartingObjectReleased(SelectExitEventArgs _)
    {
        if (!isActive || runCompleted)
            return;
    }

    private void BeginRunIfNeeded()
    {
        if (runStarted)
            return;

        runStarted = true;
        _runStartTime = Time.time;
        _lastProgressTime = Time.time;
        StartSampling();
    }

    private void ProximityProgressFallback(bool ignoreDebounce = false)
    {
        if (trackedTransform == null)
            return;

        // Debounce to avoid skipping multiple rings in one frame.
		if (!ignoreDebounce && Time.time - _lastProgressTime < 0.08f)
            return;

        float ringRadius = trajectoryEditor != null ? trajectoryEditor.playRingTriggerRadius : 0.08f;
        float endRadius = trajectoryEditor != null ? trajectoryEditor.playEndTriggerRadius : 0.12f;
        Vector3 pos = trackedTransform.position;

        // Ring progression.
        if (_ringObjects.Count > 0 && currentRingIndex >= 0 && currentRingIndex < _ringObjects.Count)
        {
            var ringObj = _ringObjects[currentRingIndex];
            if (ringObj != null)
            {
                Vector3 ringPos = ringObj.transform.position;
				float d = DistanceFromStartingObjectToPoint(ringPos, pos);
                if (d <= ringRadius)
                {
                    _lastProgressTime = Time.time;
                    NotifyRingHit(currentRingIndex);
                    return;
                }
            }
        }

        // End progression.
        if ((_ringObjects.Count == 0 || currentRingIndex >= _ringObjects.Count) && _endTriggerObject != null)
        {
            Vector3 endPos = _endTriggerObject.transform.position;
            float d = DistanceFromStartingObjectToPoint(endPos, pos);
            if (d <= endRadius)
            {
                _lastProgressTime = Time.time;
                NotifyEndReached();
            }
        }
    }

    private float DistanceFromStartingObjectToPoint(Vector3 targetPoint, Vector3 fallbackPosition)
    {
        // Use closest-point distance from any collider on the starting object.
        // This is more accurate than using the object's transform pivot.
        float best = float.PositiveInfinity;
        for (int i = 0; i < _startingObjectColliders.Count; i++)
        {
            var c = _startingObjectColliders[i];
            if (c == null || !c.enabled)
                continue;
            Vector3 p = c.ClosestPoint(targetPoint);
            float d = Vector3.Distance(p, targetPoint);
            if (d < best)
                best = d;
        }

        if (float.IsPositiveInfinity(best))
            best = Vector3.Distance(fallbackPosition, targetPoint);

        return best;
    }

    private void ShowCurrentRing()
    {
        if (_ringObjects.Count == 0)
            return;

        int idx = Mathf.Clamp(currentRingIndex, 0, _ringObjects.Count - 1);
        for (int i = 0; i < _ringObjects.Count; i++)
        {
            if (_ringObjects[i] != null)
                _ringObjects[i].SetActive(i == idx);
        }

		// If the object is already inside the newly activated ring, advance immediately.
		ProximityProgressFallback(ignoreDebounce: true);
    }

    public bool IsStartingObjectCollider(Collider other)
    {
        if (other == null || startingObject == null)
            return false;

        return other.transform == startingObject.transform || other.transform.IsChildOf(startingObject.transform);
    }

    public void NotifyRingHit(int ringIndex)
    {
        if (!isActive || runCompleted || !runStarted)
            return;

        if (ringIndex != currentRingIndex)
            return;

        if (currentRingIndex >= 0 && currentRingIndex < _ringObjects.Count && _ringObjects[currentRingIndex] != null)
            _ringObjects[currentRingIndex].SetActive(false);

        currentRingIndex++;

        if (currentRingIndex >= _ringObjects.Count)
        {
            currentRingIndex = _ringObjects.Count;
            return;
        }

        ShowCurrentRing();
    }

    public void NotifyEndReached()
    {
        if (!isActive || runCompleted || !runStarted)
            return;

        // Require rings cleared before finishing.
        if (_ringObjects.Count > 0 && currentRingIndex < _ringObjects.Count)
            return;

        CompleteRun();
    }

    private void StartSampling()
    {
        if (samplingRate <= 0f)
        {
            Debug.LogWarning("PlayManager: samplingRate must be > 0.");
            return;
        }

        if (trackedTransform == null)
        {
            Debug.LogWarning("PlayManager: trackedTransform is null; recording will be empty.");
            return;
        }

        _samples.Clear();
        StopSampling();
        _samplingRoutine = StartCoroutine(SamplingRoutine());
    }

    private void StopSampling()
    {
        if (_samplingRoutine != null)
            StopCoroutine(_samplingRoutine);
        _samplingRoutine = null;
    }

    private IEnumerator SamplingRoutine()
    {
        float interval = 1f / Mathf.Max(1f, samplingRate);
        var wait = new WaitForSeconds(interval);

        while (isActive && runStarted && !runCompleted && trackedTransform != null)
        {
            float t = Time.time - _runStartTime;
            _samples.Add(new TrajectorySample(t, trackedTransform.position, trackedTransform.rotation));
            yield return wait;
        }
    }

    private void CompleteRun()
    {
        runCompleted = true;
        StopSampling();
        SaveToCsv();
		// After the CSV export completes, replay the recorded HMD/controller motion after 2 seconds.
		if (shadowManager != null)
		{
			shadowManager.StopRecording();
			shadowManager.ReplayLast(delaySeconds: 2f);
		}
        Debug.Log($"PlayManager: Run completed. Samples={_samples.Count}");
    }

    private void SaveToCsv()
    {
        try
        {
            var ci = CultureInfo.InvariantCulture;

            string folder = Path.Combine(Application.persistentDataPath, outputFolderName);
            Directory.CreateDirectory(folder);

            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", ci);
            string fileName = $"{outputFilePrefix}_{timestamp}.csv";
            string fullPath = Path.Combine(folder, fileName);

            using (var writer = new StreamWriter(fullPath, false, Encoding.UTF8))
            {
                writer.WriteLine("timestamp,pos_x,pos_y,pos_z");
                for (int i = 0; i < _samples.Count; i++)
                {
                    var s = _samples[i];
                    writer.WriteLine(
                        $"{s.t.ToString("F6", ci)}," +
                        $"{s.position.x.ToString("F6", ci)}," +
                        $"{s.position.y.ToString("F6", ci)}," +
                        $"{s.position.z.ToString("F6", ci)}");
                }
            }

            PlayerPrefs.SetString("LastPlayTrajectoryCsvPath", fullPath);
            PlayerPrefs.Save();

            Debug.Log($"PlayManager: Saved play trajectory CSV -> {fullPath}");
        }
        catch (Exception e)
        {
            Debug.LogError($"PlayManager: Failed to save CSV: {e.Message}");
        }
    }

    private bool TryResolveSavedTask(out Vector3 startPos, out Quaternion startRot, out List<Vector3> ringPositions, out List<Vector3> ringScales, out Vector3 endPos)
    {
        // Preferred: use edited handles from TaskTrajectoryEditor (persisted in the scene).
        if (TryResolveFromTaskEdits(out startPos, out startRot, out ringPositions, out ringScales, out endPos))
            return true;

        // Fallback: use raw TaskManager recording snapshot (SaveSystem persists this too).
        if (TryResolveFromTaskManager(out startPos, out startRot, out ringPositions, out ringScales, out endPos))
            return true;

        startPos = Vector3.zero;
        startRot = Quaternion.identity;
        ringPositions = null;
        ringScales = null;
        endPos = Vector3.zero;
        return false;
    }

    private bool TryResolveFromTaskEdits(out Vector3 startPos, out Quaternion startRot, out List<Vector3> ringPositions, out List<Vector3> ringScales, out Vector3 endPos)
    {
        startPos = Vector3.zero;
        startRot = Quaternion.identity;
        endPos = Vector3.zero;
        ringPositions = null;
        ringScales = null;

        string rootName = trajectoryEditor != null ? trajectoryEditor.worldRootName : "TaskTrajectoryEdits";
        var root = GameObject.Find(rootName);
        if (root == null)
            return false;

        var startHandle = FindFirstChildByName(root.transform, "StartPointHandle");
        var endHandle = FindFirstChildByName(root.transform, "EndPointHandle");
        if (startHandle == null || endHandle == null)
            return false;

        var rings = root.GetComponentsInChildren<Transform>(true)
            .Where(t => t != null && t.name.StartsWith("TrajectoryRing_", StringComparison.OrdinalIgnoreCase))
            .Select(t => new { t, idx = ParseTrailingInt(t.name) })
            .Where(x => x.idx >= 0)
            .OrderBy(x => x.idx)
            .Select(x => x.t)
            .ToList();

        if (rings.Count == 0)
            return false;

        startPos = startHandle.position;
        startRot = startHandle.rotation;
        endPos = endHandle.position;
        ringPositions = rings.Select(r => r.position).ToList();
        ringScales = rings.Select(r => r.lossyScale).ToList();
        return true;
    }

    private bool TryResolveFromTaskManager(out Vector3 startPos, out Quaternion startRot, out List<Vector3> ringPositions, out List<Vector3> ringScales, out Vector3 endPos)
    {
        startPos = Vector3.zero;
        startRot = Quaternion.identity;
        endPos = Vector3.zero;
        ringPositions = null;
        ringScales = null;

        if (taskManager == null)
            taskManager = FindFirstObjectByType<TaskManager>();

        if (taskManager == null)
            return false;
        if (!taskManager.HasEndPoint)
            return false;
        if (taskManager.Trajectory == null || taskManager.Trajectory.Count == 0)
            return false;

        startPos = taskManager.StartingObjectInitialPosition;
        startRot = taskManager.StartingObjectInitialRotation;
        endPos = taskManager.EndPoint;

        ringPositions = new List<Vector3>(taskManager.Trajectory.Count);
        ringScales = new List<Vector3>(taskManager.Trajectory.Count);
        for (int i = 0; i < taskManager.Trajectory.Count; i++)
        {
            ringPositions.Add(taskManager.Trajectory[i].position);
            ringScales.Add(Vector3.one);
        }

        return true;
    }

    private static Transform FindFirstChildByName(Transform root, string name)
    {
        if (root == null)
            return null;

        var all = root.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < all.Length; i++)
        {
            var t = all[i];
            if (t != null && string.Equals(t.name, name, StringComparison.Ordinal))
                return t;
        }
        return null;
    }

    private static int ParseTrailingInt(string s)
    {
        if (string.IsNullOrEmpty(s))
            return -1;

        int underscore = s.LastIndexOf('_');
        if (underscore < 0 || underscore >= s.Length - 1)
            return -1;

        string tail = s.Substring(underscore + 1);
        return int.TryParse(tail, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : -1;
    }
}
