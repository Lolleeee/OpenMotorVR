using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Transformers;

/// <summary>
/// Spawns editable visual handles after a TaskManager recording completes:
/// - Start point sphere (movable)
/// - One editable "flight ring" per trajectory sample
/// - Interpolated line through edited ring positions
/// - End point handle + editable circular area
/// </summary>
public class TaskTrajectoryEditor : MonoBehaviour
{
    [Header("References")]
    public TaskManager taskManager;

    [Tooltip("If null, auto-found at runtime.")]
    public XRInteractionManager interactionManager;

    [Tooltip("If true, only active in Task mode.")]
    public bool taskModeOnly = true;

    [Header("Root / Cleanup")]
    public string worldRootName = "TaskTrajectoryEdits";

    [Header("Start Handle")]
    public float startHandleScale = 0.05f;

    [Header("Rings")]
    [Tooltip("Create one ring for every Nth sample (1 = every sample).")]
    [Min(1)]
    public int ringStride = 1;

    public float ringRadius = 0.06f;
    public float ringLineWidth = 0.006f;
    [Range(12, 128)]
    public int ringSegments = 48;

    [Header("Line")]
    public Material lineMaterial;
    public float lineWidth = 0.01f;

    [Range(1, 50)]
    public int subdivisionsPerSegment = 10;

    public bool includeExactSamplePointsInLine = true;

    [Header("Sample Markers")]
    [Tooltip("If true, creates one ring marker for every recorded sample (can be heavy for long recordings).")]
    public bool showMarkerForEverySample = true;

    [Header("PlayMode Triggers")]
    [Tooltip("Trigger radius used by PlayMode ring progression. (PlayManager reads this.)")]
    [Min(0.001f)]
    public float playRingTriggerRadius = 0.08f;

    [Tooltip("Trigger radius used by PlayMode completion trigger. (PlayManager reads this.)")]
    [Min(0.001f)]
    public float playEndTriggerRadius = 0.12f;

    [Header("End Handle")]
    public float endHandleScale = 0.06f;
    public float endAreaRadius = 0.25f;
    public float endAreaLineWidth = 0.01f;

	[Tooltip("XR General Grab Transformer: maximum scale ratio allowed when two-hand scaling the end area.")]
	[Min(0.01f)]
	public float endAreaMaximumScaleRatio = 3f;

    private Transform _root;
    private Transform _instance;

    private GameObject _startHandle;
    private GameObject _endHandle;
    private LineRenderer _line;
	private Material _runtimeLineMaterial;

    private readonly List<GameObject> _ringObjects = new();
    private readonly List<Transform> _ringTransforms = new();

    private Vector3[] _lastRingPositions;

    void Awake()
    {
        if (taskManager == null)
            taskManager = FindFirstObjectByType<TaskManager>();

        if (interactionManager == null)
            interactionManager = FindFirstObjectByType<XRInteractionManager>();

		// If no material is assigned, we create an XR-safe instanced unlit material.
		if (lineMaterial == null)
			_runtimeLineMaterial = CreateDefaultLineMaterial();
    }

    void OnEnable()
    {
        if (ModeManager.Instance != null)
            ModeManager.Instance.OnModeChanged += HandleModeChanged;

        if (taskManager != null)
            taskManager.EndPointCaptured += HandleRecordingCompleted;

		// Ensure visibility matches current mode.
		if (ModeManager.Instance != null)
			ApplyVisibility(ModeManager.Instance.CurrentMode);
    }

    void OnDisable()
    {
        if (ModeManager.Instance != null)
            ModeManager.Instance.OnModeChanged -= HandleModeChanged;

        if (taskManager != null)
            taskManager.EndPointCaptured -= HandleRecordingCompleted;
    }

    private void HandleModeChanged(Mode newMode)
    {
        // Keep the hierarchy for PlayMode to read, but hide it outside TaskMode.
        ApplyVisibility(newMode);
    }

    private void ApplyVisibility(Mode mode)
    {
        bool visible = mode == Mode.Task;
        if (_instance != null)
            _instance.gameObject.SetActive(visible);
    }

    void Update()
    {
        if (taskModeOnly && ModeManager.Instance != null && !ModeManager.Instance.IsTaskMode)
            return;

        if (_line == null || _ringTransforms.Count < 2)
            return;

        if (_lastRingPositions == null || _lastRingPositions.Length != _ringTransforms.Count)
            _lastRingPositions = new Vector3[_ringTransforms.Count];

        bool dirty = false;
        for (int i = 0; i < _ringTransforms.Count; i++)
        {
            Vector3 p = _ringTransforms[i] != null ? _ringTransforms[i].position : Vector3.zero;
            if (_lastRingPositions[i] != p)
            {
                dirty = true;
                _lastRingPositions[i] = p;
            }
        }

        if (dirty)
            RebuildLineFromRings();
    }

    private void HandleRecordingCompleted(Vector3 _)
    {
        BuildFromTaskManager();
    }

    [UnityEngine.ContextMenu("Build From TaskManager")]
    public void BuildFromTaskManager()
    {
        if (taskManager == null)
            taskManager = FindFirstObjectByType<TaskManager>();

        if (taskManager == null)
        {
            Debug.LogWarning("TaskTrajectoryEditor: taskManager missing.");
            return;
        }

        if (!taskManager.HasEndPoint)
            return;

        EnsureRoot();
        ClearInstanceOnly();

        _instance = new GameObject("TaskTrajectoryInstance").transform;
        _instance.SetParent(_root, worldPositionStays: true);

        _startHandle = CreateSphereHandle(
            name: "StartPointHandle",
            position: taskManager.StartingObjectInitialPosition,
            scale: startHandleScale,
            parent: _instance);

        _ringObjects.Clear();
        _ringTransforms.Clear();

        var traj = taskManager.Trajectory;
        if (traj != null)
        {
			int stride = showMarkerForEverySample ? 1 : Mathf.Max(1, ringStride);
			for (int i = 0; i < traj.Count; i += stride)
            {
                var ring = CreateRingHandle(
                    name: $"TrajectoryRing_{i}",
                    position: traj[i].position,
                    radius: ringRadius,
                    lineWidth: ringLineWidth,
                    segments: ringSegments,
                    parent: _instance);

                _ringObjects.Add(ring);
                _ringTransforms.Add(ring.transform);
            }
        }

        _endHandle = CreateEndHandle(taskManager.EndPoint, _instance);

        EnsureLineRenderer(_instance);
        RebuildLineFromRings();
    }

    private void EnsureRoot()
    {
        if (_root != null)
            return;

        var existing = GameObject.Find(worldRootName);
        if (existing == null)
            existing = new GameObject(worldRootName);

        _root = existing.transform;
    }

    private void ClearInstanceOnly()
    {
        if (_instance != null)
            Destroy(_instance.gameObject);

        _instance = null;
        _startHandle = null;
        _endHandle = null;
        _line = null;
        _ringObjects.Clear();
        _ringTransforms.Clear();
        _lastRingPositions = null;
    }

    [UnityEngine.ContextMenu("Clear")]
    public void Clear()
    {
        ClearInstanceOnly();
    }

    private GameObject CreateSphereHandle(string name, Vector3 position, float scale, Transform parent)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = name;
        go.transform.position = position;
        go.transform.localScale = Vector3.one * scale;
        if (parent != null) go.transform.SetParent(parent, worldPositionStays: true);

        // Keep collider solid for XR interaction, but put on non-colliding layer.
        go.layer = LayerMask.NameToLayer("InteractableNoCollision");

        ConfigureAsGrabbable(go, allowTwoHandScaling: false);
        return go;
    }

    private GameObject CreateRingHandle(string name, Vector3 position, float radius, float lineWidth, int segments, Transform parent)
    {
        var go = new GameObject(name);
        go.transform.position = position;
        if (parent != null) go.transform.SetParent(parent, worldPositionStays: true);

        // Visual ring - will be rotated perpendicular to trajectory
        var lr = go.AddComponent<LineRenderer>();
        lr.useWorldSpace = false;
        lr.loop = true;
        lr.widthMultiplier = lineWidth;
        lr.positionCount = Mathf.Max(12, segments);
		var mat = GetLineMaterial();
		if (mat != null) lr.sharedMaterial = mat;

        var pts = new Vector3[lr.positionCount];
        for (int i = 0; i < pts.Length; i++)
        {
            float a = (i / (float)pts.Length) * Mathf.PI * 2f;
            pts[i] = new Vector3(Mathf.Cos(a) * radius, 0f, Mathf.Sin(a) * radius);
        }
        lr.SetPositions(pts);

        // Collider for grabbing: use a thin disc-like box aligned with the ring plane.
        // This avoids the "giant sphere" feel and scales naturally with the ring radius.
        float thickness = Mathf.Max(0.02f, lineWidth * 6f);
        var col = go.AddComponent<BoxCollider>();
        col.isTrigger = false;
        col.center = Vector3.zero;
        col.size = new Vector3(radius * 2f, thickness, radius * 2f);
        go.transform.localScale = Vector3.one;
        // Keep collider solid for XR interaction, but put on non-colliding layer.
        go.layer = LayerMask.NameToLayer("InteractableNoCollision");

        // Add component to rotate ring perpendicular to trajectory
        var ringRotator = go.AddComponent<TrajectoryRingRotator>();
        ringRotator.lineRenderer = lr;

        ConfigureAsGrabbable(go, allowTwoHandScaling: true);
        return go;
    }

    private GameObject CreateEndHandle(Vector3 endPoint, Transform parent)
    {
        var root = new GameObject("EndPointHandle");
        root.transform.position = endPoint;
        if (parent != null) root.transform.SetParent(parent, worldPositionStays: true);

        // Point
        var point = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        point.name = "EndPoint";
        point.transform.SetParent(root.transform, worldPositionStays: false);
        point.transform.localPosition = Vector3.zero;
        point.transform.localScale = Vector3.one * endHandleScale;
        point.layer = LayerMask.NameToLayer("InteractableNoCollision");

        // Area circle
        var area = new GameObject("EndArea");
        area.transform.SetParent(root.transform, worldPositionStays: false);
        area.transform.localPosition = Vector3.zero;
        area.layer = LayerMask.NameToLayer("InteractableNoCollision");

        var lr = area.AddComponent<LineRenderer>();
        lr.useWorldSpace = false;
        lr.loop = true;
        lr.widthMultiplier = endAreaLineWidth;
        lr.positionCount = Mathf.Max(24, ringSegments);
		var mat = GetLineMaterial();
		if (mat != null) lr.sharedMaterial = mat;

        var pts = new Vector3[lr.positionCount];
        for (int i = 0; i < pts.Length; i++)
        {
            float a = (i / (float)pts.Length) * Mathf.PI * 2f;
            pts[i] = new Vector3(Mathf.Cos(a) * endAreaRadius, 0f, Mathf.Sin(a) * endAreaRadius);
        }
        lr.SetPositions(pts);

        // Collider / grab on the root
        root.layer = LayerMask.NameToLayer("InteractableNoCollision");
        var col = root.AddComponent<SphereCollider>();
        col.center = Vector3.zero;
        col.radius = endAreaRadius;
        col.isTrigger = true;
        root.transform.localScale = Vector3.one;

        ConfigureAsGrabbable(root, allowTwoHandScaling: true);

        // Apply clamp settings for end-area scaling.
        var transformer = root.GetComponent<XRGeneralGrabTransformer>();
        if (transformer != null)
        {
            transformer.clampScaling = true;
            transformer.maximumScaleRatio = endAreaMaximumScaleRatio;
        }
        return root;
    }

    private void EnsureLineRenderer(Transform parent)
    {
        var go = new GameObject("EditedTrajectoryLine");
        if (parent != null) go.transform.SetParent(parent, worldPositionStays: true);
		go.layer = LayerMask.NameToLayer("InteractableNoCollision");

        _line = go.AddComponent<LineRenderer>();
        _line.useWorldSpace = true;
        _line.widthMultiplier = lineWidth;
        _line.numCapVertices = 4;
        _line.numCornerVertices = 4;
        var mat = GetLineMaterial();
        if (mat != null) _line.sharedMaterial = mat;
    }

    private Material GetLineMaterial()
    {
        return lineMaterial != null ? lineMaterial : _runtimeLineMaterial;
    }

    private static Material CreateDefaultLineMaterial()
    {
        // URP first (common in XR template), fall back to built-in Unlit.
        var shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null)
            shader = Shader.Find("Unlit/Color");
        if (shader == null)
            return null;

        var mat = new Material(shader);
        mat.enableInstancing = true;
        // Reasonable default color; can still be overridden by assigning lineMaterial in Inspector.
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.white);
        if (mat.HasProperty("_Color")) mat.SetColor("_Color", Color.white);
        return mat;
    }

    private void RebuildLineFromRings()
    {
        if (_line == null)
            return;

        var samples = new List<Vector3>(_ringTransforms.Count);
        for (int i = 0; i < _ringTransforms.Count; i++)
        {
            if (_ringTransforms[i] != null)
                samples.Add(_ringTransforms[i].position);
        }

        if (samples.Count < 2)
            return;

        var linePoints = BuildCatmullRomLine(samples, subdivisionsPerSegment, includeExactSamplePointsInLine);
        _line.positionCount = linePoints.Count;
        _line.SetPositions(linePoints.ToArray());

        // Update ring rotations to face trajectory direction
        UpdateRingRotations(samples);
    }

    private void UpdateRingRotations(List<Vector3> ringPositions)
    {
        for (int i = 0; i < _ringObjects.Count && i < ringPositions.Count; i++)
        {
            var rotator = _ringObjects[i].GetComponent<TrajectoryRingRotator>();
            if (rotator != null)
            {
                // Calculate direction towards next ring (or use previous if last)
                Vector3 direction;
                if (i < ringPositions.Count - 1)
                    direction = (ringPositions[i + 1] - ringPositions[i]).normalized;
                else if (i > 0)
                    direction = (ringPositions[i] - ringPositions[i - 1]).normalized;
                else
                    direction = Vector3.forward;

                rotator.SetTrajectoryDirection(direction);
            }
        }
    }

    private static List<Vector3> BuildCatmullRomLine(List<Vector3> samples, int subdivisions, bool includeExactSamples)
    {
        var line = new List<Vector3>(samples.Count * Mathf.Max(1, subdivisions));
        int n = samples.Count;

        for (int i = 0; i < n - 1; i++)
        {
            Vector3 p0 = samples[Mathf.Max(i - 1, 0)];
            Vector3 p1 = samples[i];
            Vector3 p2 = samples[i + 1];
            Vector3 p3 = samples[Mathf.Min(i + 2, n - 1)];

            if (includeExactSamples)
                line.Add(p1);

            for (int s = 1; s <= subdivisions; s++)
            {
                float t = s / (float)(subdivisions + 1);
                line.Add(CatmullRom(p0, p1, p2, p3, t));
            }
        }

        line.Add(samples[n - 1]);
        return line;
    }

    private static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
    {
        float t2 = t * t;
        float t3 = t2 * t;

        return 0.5f * (
            (2f * p1) +
            (-p0 + p2) * t +
            (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 +
            (-p0 + 3f * p1 - 3f * p2 + p3) * t3
        );
    }

    private void ConfigureAsGrabbable(GameObject go, bool allowTwoHandScaling)
    {
        var rb = go.GetComponent<Rigidbody>();
        if (rb == null) rb = go.AddComponent<Rigidbody>();
        rb.isKinematic = true;
        rb.useGravity = false;

        var grab = go.GetComponent<XRGrabInteractable>();
        if (grab == null) grab = go.AddComponent<XRGrabInteractable>();

        if (interactionManager != null)
            grab.interactionManager = interactionManager;
        else if (grab.interactionManager == null)
            grab.interactionManager = FindFirstObjectByType<XRInteractionManager>();

        if (allowTwoHandScaling)
        {
            // Two-hand scaling only works if the interactable allows multiple simultaneous selects.
            grab.selectMode = InteractableSelectMode.Multiple;

            var transformer = go.GetComponent<XRGeneralGrabTransformer>();
            if (transformer == null) transformer = go.AddComponent<XRGeneralGrabTransformer>();
            transformer.allowTwoHandedScaling = true;
        }
    }
}

/// <summary>
/// Helper component to rotate trajectory rings perpendicular to the trajectory line.
/// Orients the ring to face the direction of the next trajectory point.
/// </summary>
public class TrajectoryRingRotator : MonoBehaviour
{
    public LineRenderer lineRenderer;
    private Vector3 _trajectoryDirection = Vector3.forward;

    public void SetTrajectoryDirection(Vector3 direction)
    {
        if (direction.sqrMagnitude > 0.001f)
        {
            _trajectoryDirection = direction.normalized;
            UpdateRotation();
        }
    }

    private void UpdateRotation()
    {
        // Create a rotation that makes the ring perpendicular to the trajectory direction
        // The ring is in the XZ plane by default, so we rotate to face the trajectory
        if (_trajectoryDirection.sqrMagnitude > 0.001f)
        {
            // Ring points are created in the XZ plane (y=0), so the ring's normal starts as +Y.
            // To make the ring plane perpendicular to the trajectory, align its normal with the trajectory direction.
            Quaternion rot = Quaternion.FromToRotation(Vector3.up, _trajectoryDirection);
            transform.rotation = rot;
        }
    }
}
