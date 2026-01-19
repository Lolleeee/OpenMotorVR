using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.InputSystem;

public class TrajectoryVisualizer : MonoBehaviour
{
    [Header("Input (controller button)")]
    [Tooltip("Press to toggle trajectory on/off (and reload latest when turning on).")]
    public InputActionProperty toggleAction;

    [Tooltip("If true, visualizer only works in Task mode.")]
    public bool taskModeOnly = true;

    [Header("CSV Source")]
    [Tooltip("PlayerPrefs key written by RightControllerTracker.")]
    public string lastCsvPlayerPrefsKey = "LastTrajectoryCsvPath";

    [Tooltip("Fallback folder to search for newest CSV if PlayerPrefs not found.")]
    public string fallbackFolderRelativeToProject = "Assets/Tracking";

    [Tooltip("If set, loads this file (relative to project root) instead of last/newest.")]
    public string overrideCsvPath = "";
    [Header("Parenting")]
    [Tooltip("If plotParent is null, create/find this root at scene root so plots don't follow the controller.")]
    public string worldRootName = "TrajectoryPlots";

    private Transform _plotContainer;
        [Header("Coloring")]
    public bool useGradient = true;

    [Tooltip("Early (t=0) to Late (t=1) gradient for the trajectory line.")]
    public Gradient trajectoryGradient = DefaultGradient();

    [Tooltip("If true, sample points are colored to match the gradient.")]
    public bool colorSamplePoints = true;

    [Tooltip("Only used when auto-creating spheres (no prefab).")]
    public Material samplePointMaterial;

    [Header("Rendering")]
    public Transform plotParent;
    public Material lineMaterial;
    public float lineWidth = 0.01f;

    [Tooltip("If null, spheres will be created automatically.")]
    public GameObject samplePointPrefab;

    public float samplePointScale = 0.02f;

    [Header("Interpolation")]
    [Tooltip("Subdivisions per segment (higher = smoother).")]
    [Range(1, 50)]
    public int subdivisionsPerSegment = 10;

    [Tooltip("If true, line includes original sample positions exactly.")]
    public bool includeExactSamplePointsInLine = true;

    private bool _visible;
    private LineRenderer _line;
    private readonly List<GameObject> _spawnedPoints = new();

    void OnEnable()
    {
        if (ModeManager.Instance != null)
            ModeManager.Instance.OnModeChanged += HandleModeChanged;

        if (toggleAction.action != null)
            toggleAction.action.Enable();
    }

    void OnDisable()
    {
        if (ModeManager.Instance != null)
            ModeManager.Instance.OnModeChanged -= HandleModeChanged;

        if (toggleAction.action != null)
            toggleAction.action.Disable();

        ClearPlot();
    }

    void Update()
    {
        if (taskModeOnly && ModeManager.Instance != null && !ModeManager.Instance.IsTaskMode)
            return;

        if (toggleAction.action == null)
            return;

        // rising edge (button)
        if (toggleAction.action.WasPressedThisFrame())
        {
            if (_visible) Hide();
            else Show();
        }
    }

    void HandleModeChanged(Mode newMode)
    {
        if (!taskModeOnly)
            return;

        if (newMode != Mode.Task)
            Hide();
    }

    void Show()
    {
        _visible = true;
        RebuildPlot();
    }

    void Hide()
    {
        _visible = false;
        ClearPlot();
    }

    void RebuildPlot()
    {
        ClearPlot();

        string csvPath = ResolveCsvPath();
        if (string.IsNullOrWhiteSpace(csvPath))
        {
            Debug.LogWarning("TrajectoryVisualizer: No CSV found to load.");
            return;
        }

        if (!File.Exists(csvPath))
        {
            Debug.LogWarning($"TrajectoryVisualizer: CSV not found: {csvPath}");
            return;
        }

        var samples = LoadSamples(csvPath);
        if (samples.Count < 2)
        {
            Debug.LogWarning($"TrajectoryVisualizer: Not enough samples to draw (count={samples.Count}).");
            return;
        }

        EnsurePlotContainer();
        EnsureLineRenderer();

        if (useGradient)
            _line.colorGradient = trajectoryGradient;

        int n = samples.Count;
        for (int i = 0; i < n; i++)
        {
            float u = (n <= 1) ? 0f : (i / (float)(n - 1));
            _spawnedPoints.Add(SpawnPoint(samples[i], u));
        }

        var linePoints = BuildCatmullRomLine(samples, subdivisionsPerSegment, includeExactSamplePointsInLine);
        _line.positionCount = linePoints.Count;
        _line.SetPositions(linePoints.ToArray());

        Debug.Log($"TrajectoryVisualizer: Loaded '{Path.GetFileName(csvPath)}' samples={samples.Count}, linePoints={linePoints.Count}");
    }

    void EnsurePlotContainer()
    {
        // If user explicitly set plotParent, use it.
        if (plotParent != null)
        {
            _plotContainer = plotParent;
            return;
        }

        // Otherwise, create/find a stable root at scene root (NOT under controllers).
        var root = GameObject.Find(worldRootName);
        if (root == null)
            root = new GameObject(worldRootName);

        // Create a per-plot container so ClearPlot only removes what we spawned.
        var containerGO = new GameObject("TrajectoryPlotInstance");
        containerGO.transform.SetParent(root.transform, worldPositionStays: true);
        _plotContainer = containerGO.transform;
    }

    void EnsureLineRenderer()
    {
        var parent = _plotContainer != null ? _plotContainer : null;

        var go = new GameObject("TrajectoryLine");
        if (parent != null)
            go.transform.SetParent(parent, worldPositionStays: true);

        _line = go.AddComponent<LineRenderer>();
        _line.useWorldSpace = true;
        _line.widthMultiplier = lineWidth;
        _line.numCapVertices = 4;
        _line.numCornerVertices = 4;

        if (lineMaterial != null)
            _line.material = lineMaterial;
    }

    GameObject SpawnPoint(Vector3 worldPos, float u01)
    {
        Transform parent = _plotContainer;

        GameObject p;
        if (samplePointPrefab != null)
        {
            p = Instantiate(samplePointPrefab, worldPos, Quaternion.identity);
            if (parent != null) p.transform.SetParent(parent, worldPositionStays: true);
        }
        else
        {
            p = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            if (parent != null) p.transform.SetParent(parent, worldPositionStays: true);

            p.transform.position = worldPos;

            var col = p.GetComponent<Collider>();
            if (col != null) Destroy(col);
        }

        p.transform.localScale = Vector3.one * samplePointScale;
        p.name = "TrajectorySamplePoint";

        if (useGradient && colorSamplePoints)
        {
            var r = p.GetComponent<Renderer>();
            if (r != null)
            {
                // Use sharedMaterial to avoid instantiating a new material per point if you provide one.
                if (samplePointMaterial != null)
                    r.sharedMaterial = samplePointMaterial;

                Color c = trajectoryGradient.Evaluate(Mathf.Clamp01(u01));
                // If the shader uses _BaseColor (URP) this may not work; fallback to .color works for Standard.
                if (r.material.HasProperty("_BaseColor")) r.material.SetColor("_BaseColor", c);
                else r.material.color = c;
            }
        }

        return p;
    }

    GameObject SpawnPoint(Vector3 worldPos) => SpawnPoint(worldPos, 0f);

    static Gradient DefaultGradient()
    {
        var g = new Gradient();
        g.SetKeys(
            new[]
            {
                new GradientColorKey(new Color(0.1f, 0.6f, 1f), 0f), // early (blue)
                new GradientColorKey(new Color(1f, 0.2f, 0.2f), 1f)  // late (red)
            },
            new[]
            {
                new GradientAlphaKey(1f, 0f),
                new GradientAlphaKey(1f, 1f)
            }
        );
        return g;
    }
    void ClearPlot()
    {
        for (int i = 0; i < _spawnedPoints.Count; i++)
        {
            if (_spawnedPoints[i] != null)
                Destroy(_spawnedPoints[i]);
        }
        _spawnedPoints.Clear();

        if (_line != null)
            Destroy(_line.gameObject);
        _line = null;

        // If we auto-created a plot container (plotParent was null), remove it too.
        if (plotParent == null && _plotContainer != null)
            Destroy(_plotContainer.gameObject);

        _plotContainer = null;
    }

    string ResolveCsvPath()
    {
        // explicit override (relative to project root)
        if (!string.IsNullOrWhiteSpace(overrideCsvPath))
            return Path.Combine(Application.dataPath, "..", overrideCsvPath);

        // prefer PlayerPrefs "last"
        string last = PlayerPrefs.GetString(lastCsvPlayerPrefsKey, "");
        if (!string.IsNullOrWhiteSpace(last))
            return last;

        // fallback: newest csv in folder
        string folder = Path.Combine(Application.dataPath, "..", fallbackFolderRelativeToProject);
        if (!Directory.Exists(folder))
            return "";

        string newest = "";
        DateTime newestTime = DateTime.MinValue;

        foreach (var f in Directory.GetFiles(folder, "*.csv", SearchOption.TopDirectoryOnly))
        {
            DateTime t = File.GetLastWriteTimeUtc(f);
            if (t > newestTime)
            {
                newestTime = t;
                newest = f;
            }
        }

        return newest;
    }

    static List<Vector3> LoadSamples(string csvPath)
    {
        // Accepts:
        // timestamp,pos_x,pos_y,pos_z
        // OR t,controller_x,controller_y,controller_z,...
        var ci = CultureInfo.InvariantCulture;
        var result = new List<Vector3>();

        var lines = File.ReadAllLines(csvPath);
        if (lines.Length == 0)
            return result;

        for (int i = 0; i < lines.Length; i++)
        {
            if (i == 0) continue; // header
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;

            var parts = line.Split(',');
            if (parts.Length < 4) continue;

            // For both formats, controller/world pos is columns 1..3
            if (float.TryParse(parts[1], NumberStyles.Float, ci, out float x) &&
                float.TryParse(parts[2], NumberStyles.Float, ci, out float y) &&
                float.TryParse(parts[3], NumberStyles.Float, ci, out float z))
            {
                result.Add(new Vector3(x, y, z));
            }
        }

        return result;
    }

    static List<Vector3> BuildCatmullRomLine(List<Vector3> samples, int subdivisions, bool includeExactSamples)
    {
        // Catmull–Rom needs 4 points; we clamp ends by repeating.
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

        // Ensure final endpoint
        line.Add(samples[n - 1]);
        return line;
    }

    static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
    {
        // Standard Catmull–Rom spline (centripetal is nicer but this is simple and stable)
        float t2 = t * t;
        float t3 = t2 * t;

        return 0.5f * (
            (2f * p1) +
            (-p0 + p2) * t +
            (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 +
            (-p0 + 3f * p1 - 3f * p2 + p3) * t3
        );
    }
}