using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Saves/loads the full runtime state of the configured content scene.
///
/// What is saved:
/// - Every GameObject in the content scene (including children)
/// - Active state, layer
/// - Transform local position/rotation/scale
/// - Supported fields on every Component (public + [SerializeField] private fields)
///
/// Spawned prefabs:
/// - For objects with SpawnedObject: we also record a prefab name (Resources) and will recreate missing ones.
/// - On load we also destroy spawned objects that are not present in the save, to restore the last saved state.
/// </summary>
public class SaveSystem : MonoBehaviour
{
    [Header("Scene Names")]
    [Tooltip("Persistent scene that holds player/managers (not directly saved).")]
    [SerializeField] private string mainSceneName = "Persistent";

    [Tooltip("Editable/content scene that gets saved/loaded.")]
    [SerializeField] private string editableSceneName = "Scene";

    [Header("Save Settings")]
    [Tooltip("Filename for the content scene save (stored in persistentDataPath).")]
    [SerializeField] private string saveFileName = "scene_save.json";

    [Tooltip("Resources subfolder for spawnable prefabs.")]
    [SerializeField] private string spawnablePrefabsFolder = "SpawnablePrefabs";

    [Header("Advanced")]
    [Tooltip("If false, only spawned objects will be saved/restored (not recommended).")]
    [SerializeField] private bool includeNonSpawnedObjects = true;

    [Tooltip("If true, also attempts to write back Behaviour.enabled for components that have it.")]
    [SerializeField] private bool saveComponentEnabledState = true;

    [Tooltip("When loading, log up to this many missing/unrestored objects (by path).")]
    [SerializeField] private int maxMissingRestoreLogs = 50;

    [Serializable]
    private sealed class SceneSnapshot
    {
        public int version = 3;
        public string sceneName;
        public string savedAtUtc;
        public List<ObjectSnapshot> objects = new List<ObjectSnapshot>();

        // Optional: last completed motor task recording (lives in Persistent scene, so we store it here explicitly)
        public TaskRecordingSnapshot taskRecording;
    }

    [Serializable]
    private sealed class TaskRecordingSnapshot
    {
        public Vector3 startPosition;
        public Quaternion startRotation;
        public Vector3 endPoint;
        public List<TaskTrajectorySampleSnapshot> trajectory = new List<TaskTrajectorySampleSnapshot>();

		// Optional: lets PlayMode re-bind the real starting object after load.
		public string startingObjectPersistentId;

        // TaskTrajectoryEditor visualization + play trigger sizing (captured from TaskTrajectoryEditor)
        public int ringStride;
        public bool showMarkerForEverySample;
        public float ringRadius;
        public float ringLineWidth;
        public int ringSegments;
        public float lineWidth;
        public int subdivisionsPerSegment;
        public bool includeExactSamplePointsInLine;
        public float endAreaRadius;
        public float endAreaLineWidth;
        public float playRingTriggerRadius;
        public float playEndTriggerRadius;
    }

    [Serializable]
    private sealed class TaskTrajectorySampleSnapshot
    {
        public float t;
        public Vector3 position;
        public Quaternion rotation;
    }

    [Serializable]
    private sealed class ObjectSnapshot
    {
        public string path; // stable-ish path using name + sibling index
        public string name;
        public string parentPath; // null/empty if root
        public string parentPersistentId; // if parent has SpawnedObject
        public bool activeSelf;
        public int layer;

        public TransformSnapshot transform;

        public bool isSpawned;
        public string prefabName;
        public string persistentId; // SpawnedObject.persistentId

        public List<ComponentSnapshot> components = new List<ComponentSnapshot>();
    }

    [Serializable]
    private sealed class TransformSnapshot
    {
        public Vector3 localPosition;
        public Quaternion localRotation;
        public Vector3 localScale;
    }

    [Serializable]
    private sealed class ComponentSnapshot
    {
        public string type;
        public bool? enabled; // Behaviour.enabled
        public JObject fields;
    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
    public sealed class SkipSaveAttribute : Attribute { }

    private static readonly BindingFlags FieldFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public void SaveScene()
    {
        Scene contentScene = ResolveEditableScene();
        if (!contentScene.IsValid() || !contentScene.isLoaded)
        {
            Debug.LogWarning($"Scene '{GetEditableSceneName()}' is not loaded!");
            return;
        }

        var snapshot = new SceneSnapshot
        {
            sceneName = contentScene.name,
            savedAtUtc = DateTime.UtcNow.ToString("O")
        };

		snapshot.taskRecording = CaptureTaskRecording();

        var serializer = CreateSerializer();
        foreach (var root in contentScene.GetRootGameObjects())
        {
            // If user wants to only persist spawned objects, skip everything else.
            if (!includeNonSpawnedObjects && root.GetComponentInChildren<SpawnedObject>(true) == null)
                continue;

            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t == null) continue;
                var go = t.gameObject;
                if (go == null || go.scene != contentScene) continue;

                // If we only care about spawned hierarchies, filter.
                if (!includeNonSpawnedObjects && go.GetComponentInParent<SpawnedObject>(true) == null)
                    continue;

                snapshot.objects.Add(CaptureGameObject(go, serializer));
            }
        }

        string json = JsonConvert.SerializeObject(snapshot, Formatting.Indented, CreateJsonSettings());
        File.WriteAllText(GetSavePath(), json);
        Debug.Log($"Saved {snapshot.objects.Count} objects to {GetSavePath()} (scene '{contentScene.name}').");
    }

    public void LoadScene()
    {
        StartCoroutine(LoadSceneCoroutine());
    }

    private IEnumerator LoadSceneCoroutine()
    {
        Scene contentScene = ResolveEditableScene();
        if (!contentScene.IsValid() || !contentScene.isLoaded)
        {
            Debug.LogWarning($"Scene '{GetEditableSceneName()}' is not loaded!");
            yield break;
        }

        string path = GetSavePath();
        if (!File.Exists(path))
        {
            Debug.LogWarning($"No save file found at {path}.");
            yield break;
        }

        SceneSnapshot snapshot;
        try
        {
            string json = File.ReadAllText(path);
            snapshot = JsonConvert.DeserializeObject<SceneSnapshot>(json, CreateJsonSettings());
        }
        catch (Exception ex)
        {
            Debug.LogError($"Failed to read/parse save file at {path}: {ex}");
            yield break;
        }

        if (snapshot == null || snapshot.objects == null)
        {
            Debug.LogWarning($"Save file at {path} was empty or invalid.");
            yield break;
        }

        // 1) Destroy spawned objects that are not present in the snapshot
        var savedSpawnedIds = new HashSet<string>(snapshot.objects
            .Where(o => o != null && o.isSpawned && !string.IsNullOrEmpty(o.persistentId))
            .Select(o => o.persistentId));
        bool hasAnySpawnedIds = savedSpawnedIds.Count > 0;

        // Back-compat fallback for older saves
        var savedSpawnedPaths = hasAnySpawnedIds
            ? null
            : new HashSet<string>(snapshot.objects.Where(o => o != null && o.isSpawned).Select(o => o.path));
        var existingSpawned = FindObjectsOfType<SpawnedObject>(true);
        foreach (var spawned in existingSpawned)
        {
            if (spawned == null) continue;
            var go = spawned.gameObject;
            if (go == null || go.scene != contentScene) continue;

            if (hasAnySpawnedIds)
            {
                if (string.IsNullOrEmpty(spawned.persistentId) || !savedSpawnedIds.Contains(spawned.persistentId))
                    Destroy(go);
            }
            else
            {
                string goPath = BuildPath(go.transform);
                if (!savedSpawnedPaths.Contains(goPath))
                    Destroy(go);
            }
        }

        // Wait a frame for Destroy() to take effect.
        yield return null;

        // 2) Recreate missing spawned objects
        var existingObjectsByPath = BuildSceneLookup(contentScene);
        var existingSpawnedById = BuildSpawnedLookupById(contentScene);
        foreach (var obj in snapshot.objects
                     .Where(o => o != null && o.isSpawned && !string.IsNullOrEmpty(o.path))
                     .OrderBy(o => o.path.Length))
        {
            if (!string.IsNullOrEmpty(obj.persistentId) && existingSpawnedById.ContainsKey(obj.persistentId))
                continue;
            if (string.IsNullOrEmpty(obj.persistentId) && existingObjectsByPath.ContainsKey(obj.path))
                continue;

            var prefab = LoadPrefab(obj.prefabName);
            if (prefab == null)
            {
                Debug.LogWarning($"Prefab '{obj.prefabName}' not found in Resources; cannot recreate '{obj.path}'.");
                continue;
            }

            Transform parent = null;
            if (!string.IsNullOrEmpty(obj.parentPersistentId) && existingSpawnedById.TryGetValue(obj.parentPersistentId, out var parentSpawnedGo) && parentSpawnedGo != null)
                parent = parentSpawnedGo.transform;
            else if (!string.IsNullOrEmpty(obj.parentPath) && existingObjectsByPath.TryGetValue(obj.parentPath, out var parentGo) && parentGo != null)
                parent = parentGo.transform;

            var instance = Instantiate(prefab);
            instance.name = string.IsNullOrEmpty(obj.name) ? instance.name.Replace("(Clone)", string.Empty).Trim() : obj.name;

            // Parent first (so sibling index applies to the right list)
            if (parent != null)
                instance.transform.SetParent(parent, true);

            SceneManager.MoveGameObjectToScene(instance, contentScene);

            // Restore sibling index so saved path keys remain stable across load.
            int savedSiblingIndex = GetSiblingIndexFromPath(obj.path);
            if (savedSiblingIndex >= 0)
                instance.transform.SetSiblingIndex(savedSiblingIndex);

            instance.SetActive(obj.activeSelf);

            // Apply persistent ID so future loads match reliably.
            var spawned = instance.GetComponent<SpawnedObject>();
            if (spawned != null && !string.IsNullOrEmpty(obj.persistentId))
                spawned.persistentId = obj.persistentId;

            // Use the saved path as the key; we rebuild the lookup after all creation anyway.
            existingObjectsByPath[obj.path] = instance;
            if (spawned != null && !string.IsNullOrEmpty(spawned.persistentId))
                existingSpawnedById[spawned.persistentId] = instance;
        }

        // 3) Apply snapshot data (transforms + fields)
        existingObjectsByPath = BuildSceneLookup(contentScene);
        existingSpawnedById = BuildSpawnedLookupById(contentScene);
        var serializer = CreateSerializer();

        int missingCount = 0;
        int missingLogged = 0;
        foreach (var obj in snapshot.objects)
        {
            if (obj == null || string.IsNullOrEmpty(obj.path))
                continue;

            bool exists = false;
            if (obj.isSpawned && !string.IsNullOrEmpty(obj.persistentId))
                exists = existingSpawnedById.ContainsKey(obj.persistentId);
            else
                exists = existingObjectsByPath.ContainsKey(obj.path);

            if (exists)
                continue;

            missingCount++;
            if (missingLogged < Mathf.Max(0, maxMissingRestoreLogs))
            {
                Debug.LogWarning($"Could not restore object '{obj.name}' at path '{obj.path}' (spawned={obj.isSpawned}, prefab='{obj.prefabName}', parent='{obj.parentPath}'). If this was spawned, verify the prefab exists in Resources and that its parent hierarchy and sibling order haven't changed since save.");
                missingLogged++;
            }
        }

        // Apply parents first (shorter paths first) to reduce hierarchy surprises.
        foreach (var obj in snapshot.objects.OrderBy(o => o?.path?.Length ?? int.MaxValue))
        {
            if (obj == null || string.IsNullOrEmpty(obj.path))
                continue;

            GameObject go = null;
            if (obj.isSpawned && !string.IsNullOrEmpty(obj.persistentId))
                existingSpawnedById.TryGetValue(obj.persistentId, out go);
            if (go == null)
                existingObjectsByPath.TryGetValue(obj.path, out go);
            if (go == null)
                continue;

            // Active state and layer
            go.SetActive(obj.activeSelf);
            if (obj.layer >= 0)
                go.layer = obj.layer;

            // Transform (local)
            if (obj.transform != null)
            {
                var t = go.transform;
                t.localPosition = obj.transform.localPosition;
                t.localRotation = obj.transform.localRotation;
                t.localScale = obj.transform.localScale;
            }

            ApplyComponents(go, obj, serializer);
        }

        if (missingCount > 0)
        {
            Debug.LogWarning($"Load completed with {missingCount} objects not restored (logged {missingLogged}). This usually means the hierarchy changed since save, the object was removed, or a spawned prefab could not be found.");
        }

        ApplyTaskRecording(snapshot.taskRecording);

        Debug.Log($"Loaded snapshot ({snapshot.objects.Count} objects) from {path} into scene '{contentScene.name}'.");
    }

    private TaskRecordingSnapshot CaptureTaskRecording()
    {
        var taskManager = FindFirstObjectByType<TaskManager>();
        if (taskManager == null)
            return null;
        if (!taskManager.HasEndPoint)
            return null;
        if (taskManager.Trajectory == null || taskManager.Trajectory.Count == 0)
            return null;

        var rec = new TaskRecordingSnapshot
        {
            startPosition = taskManager.StartingObjectInitialPosition,
            startRotation = taskManager.StartingObjectInitialRotation,
            endPoint = taskManager.EndPoint,
        };

        // Remember which object was used as the starting object (if it's a SpawnedObject).
        if (taskManager.StartingObject != null)
        {
            var spawned = taskManager.StartingObject.GetComponentInParent<SpawnedObject>() ?? taskManager.StartingObject.GetComponent<SpawnedObject>();
            if (spawned != null && !string.IsNullOrWhiteSpace(spawned.persistentId))
                rec.startingObjectPersistentId = spawned.persistentId;
        }

        var editor = FindFirstObjectByType<TaskTrajectoryEditor>();
        if (editor != null)
        {
            rec.ringStride = editor.ringStride;
            rec.showMarkerForEverySample = editor.showMarkerForEverySample;
            rec.ringRadius = editor.ringRadius;
            rec.ringLineWidth = editor.ringLineWidth;
            rec.ringSegments = editor.ringSegments;
            rec.lineWidth = editor.lineWidth;
            rec.subdivisionsPerSegment = editor.subdivisionsPerSegment;
            rec.includeExactSamplePointsInLine = editor.includeExactSamplePointsInLine;
            rec.endAreaRadius = editor.endAreaRadius;
            rec.endAreaLineWidth = editor.endAreaLineWidth;
            rec.playRingTriggerRadius = editor.playRingTriggerRadius;
            rec.playEndTriggerRadius = editor.playEndTriggerRadius;
        }

        for (int i = 0; i < taskManager.Trajectory.Count; i++)
        {
            var s = taskManager.Trajectory[i];
            rec.trajectory.Add(new TaskTrajectorySampleSnapshot
            {
                t = s.t,
                position = s.position,
                rotation = s.rotation
            });
        }

        return rec;
    }

    private void ApplyTaskRecording(TaskRecordingSnapshot rec)
    {
        if (rec == null || rec.trajectory == null || rec.trajectory.Count == 0)
            return;

        var taskManager = FindFirstObjectByType<TaskManager>();
        if (taskManager == null)
            return;

        var samples = new List<TaskManager.TrajectorySample>(rec.trajectory.Count);
        for (int i = 0; i < rec.trajectory.Count; i++)
        {
            var s = rec.trajectory[i];
            samples.Add(new TaskManager.TrajectorySample(s.t, s.position, s.rotation));
        }

        taskManager.LoadSavedRecording(rec.startPosition, rec.startRotation, rec.endPoint, samples);

        // Rebind a concrete starting object reference if possible.
        if (!string.IsNullOrWhiteSpace(rec.startingObjectPersistentId))
        {
            var spawnedObjects = FindObjectsOfType<SpawnedObject>(true);
            for (int i = 0; i < spawnedObjects.Length; i++)
            {
                var so = spawnedObjects[i];
                if (so == null) continue;
                if (string.Equals(so.persistentId, rec.startingObjectPersistentId, StringComparison.Ordinal))
                {
                    taskManager.BindStartingObject(so.gameObject);
                    break;
                }
            }
        }

        var editor = FindFirstObjectByType<TaskTrajectoryEditor>();
        if (editor != null)
        {
            // Restore visualization/editor settings first so BuildFromTaskManager uses them.
            if (rec.ringStride > 0) editor.ringStride = rec.ringStride;
            editor.showMarkerForEverySample = rec.showMarkerForEverySample;
            if (rec.ringRadius > 0f) editor.ringRadius = rec.ringRadius;
            if (rec.ringLineWidth > 0f) editor.ringLineWidth = rec.ringLineWidth;
            if (rec.ringSegments > 0) editor.ringSegments = rec.ringSegments;
            if (rec.lineWidth > 0f) editor.lineWidth = rec.lineWidth;
            if (rec.subdivisionsPerSegment > 0) editor.subdivisionsPerSegment = rec.subdivisionsPerSegment;
            editor.includeExactSamplePointsInLine = rec.includeExactSamplePointsInLine;
            if (rec.endAreaRadius > 0f) editor.endAreaRadius = rec.endAreaRadius;
            if (rec.endAreaLineWidth > 0f) editor.endAreaLineWidth = rec.endAreaLineWidth;
            if (rec.playRingTriggerRadius > 0f) editor.playRingTriggerRadius = rec.playRingTriggerRadius;
            if (rec.playEndTriggerRadius > 0f) editor.playEndTriggerRadius = rec.playEndTriggerRadius;

            // Rebind in case the editor cached a previous TaskManager reference.
            editor.taskManager = taskManager;
            editor.BuildFromTaskManager();
        }
    }

    public bool HasSaveFile() => File.Exists(GetSavePath());

    private ObjectSnapshot CaptureGameObject(GameObject go, JsonSerializer serializer)
    {
        var t = go.transform;
        var spawned = go.GetComponent<SpawnedObject>();
        var parentSpawned = t.parent != null ? t.parent.GetComponent<SpawnedObject>() : null;
        var snapshot = new ObjectSnapshot
        {
            path = BuildPath(t),
            name = go.name,
            parentPath = t.parent != null ? BuildPath(t.parent) : null,
            parentPersistentId = parentSpawned != null ? parentSpawned.persistentId : null,
            activeSelf = go.activeSelf,
            layer = go.layer,
            transform = new TransformSnapshot
            {
                localPosition = t.localPosition,
                localRotation = t.localRotation,
                localScale = t.localScale
            },
            isSpawned = spawned != null,
            prefabName = spawned != null ? GuessPrefabName(go.name) : null,
            persistentId = spawned != null ? spawned.persistentId : null
        };

        foreach (var comp in go.GetComponents<Component>())
        {
            if (comp == null) continue;
            if (comp is Transform) continue;

            var type = comp.GetType();
            if (Attribute.IsDefined(type, typeof(SkipSaveAttribute), true))
                continue;

            // Skip Unity internals that tend to be noisy or unsafe to reflect
            if (type == typeof(MeshFilter) || type == typeof(MeshRenderer) || type == typeof(SkinnedMeshRenderer))
                continue;

            var fields = CaptureFields(comp, serializer);
            if (fields == null || !fields.HasValues)
                continue;

            bool? enabled = null;
            if (saveComponentEnabledState && comp is Behaviour behaviour)
                enabled = behaviour.enabled;

            snapshot.components.Add(new ComponentSnapshot
            {
                type = type.AssemblyQualifiedName,
                enabled = enabled,
                fields = fields
            });
        }

        return snapshot;
    }

    private JObject CaptureFields(Component component, JsonSerializer serializer)
    {
        var result = new JObject();
        var type = component.GetType();

        foreach (var field in type.GetFields(FieldFlags))
        {
            if (field.IsStatic || field.IsLiteral || field.IsInitOnly)
                continue;
            if (Attribute.IsDefined(field, typeof(NonSerializedAttribute)))
                continue;

            bool isPublic = field.IsPublic;
            bool hasSerializeField = Attribute.IsDefined(field, typeof(SerializeField), true);
            if (!isPublic && !hasSerializeField)
                continue;

            if (!IsSupportedFieldType(field.FieldType))
                continue;

            try
            {
                object value = field.GetValue(component);
                if (value == null)
                {
                    result[field.Name] = JValue.CreateNull();
                    continue;
                }
                result[field.Name] = JToken.FromObject(value, serializer);
            }
            catch
            {
                // Ignore fields that throw (some Unity internals can be fragile)
            }
        }

        return result;
    }

    private void ApplyComponents(GameObject go, ObjectSnapshot snapshot, JsonSerializer serializer)
    {
        if (snapshot.components == null || snapshot.components.Count == 0)
            return;

        foreach (var compSnapshot in snapshot.components)
        {
            if (compSnapshot == null || string.IsNullOrEmpty(compSnapshot.type))
                continue;

            Type type;
            try
            {
                type = Type.GetType(compSnapshot.type);
            }
            catch
            {
                continue;
            }
            if (type == null)
                continue;

            // If the component doesn't exist, we can add it for MonoBehaviours. For built-in components, we do not.
            Component component = go.GetComponent(type);
            if (component == null)
            {
                if (typeof(MonoBehaviour).IsAssignableFrom(type))
                {
                    try { component = go.AddComponent(type); }
                    catch { component = null; }
                }
            }
            if (component == null)
                continue;

            // Behaviour.enabled
            if (saveComponentEnabledState && compSnapshot.enabled.HasValue && component is Behaviour behaviour)
                behaviour.enabled = compSnapshot.enabled.Value;

            // Fields
            if (compSnapshot.fields != null)
            {
                foreach (var prop in compSnapshot.fields.Properties())
                {
                    var field = type.GetField(prop.Name, FieldFlags);
                    if (field == null) continue;
                    if (field.IsStatic || field.IsLiteral || field.IsInitOnly) continue;

                    if (!IsSupportedFieldType(field.FieldType))
                        continue;

                    try
                    {
                        object value = prop.Value.Type == JTokenType.Null ? null : prop.Value.ToObject(field.FieldType, serializer);
                        field.SetValue(component, value);
                    }
                    catch
                    {
                        // ignore
                    }
                }
            }

            // Special handling: SpawnedObject has runtime side effects that need setters.
            if (component is SpawnedObject spawned)
            {
                spawned.SetGrabbable(spawned.grabEnabled);
                spawned.SetCollision(spawned.collisionEnabled);
                spawned.SetGravity(spawned.gravityEnabled);
                spawned.SetKinematic(spawned.kinematic);
            }
        }
    }

    private static bool IsSupportedFieldType(Type type)
    {
        if (type == null) return false;
        if (typeof(UnityEngine.Object).IsAssignableFrom(type)) return false; // can't safely restore references
        if (typeof(Delegate).IsAssignableFrom(type)) return false;

        if (type.IsPrimitive || type.IsEnum) return true;
        if (type == typeof(string) || type == typeof(decimal)) return true;

        // Common Unity structs
        if (type == typeof(Vector2) || type == typeof(Vector3) || type == typeof(Vector4)) return true;
        if (type == typeof(Quaternion)) return true;
        if (type == typeof(Color) || type == typeof(Color32)) return true;
        if (type == typeof(Rect) || type == typeof(Bounds)) return true;

        // Arrays / Lists
        if (type.IsArray)
            return IsSupportedFieldType(type.GetElementType());

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            return IsSupportedFieldType(type.GetGenericArguments()[0]);

        // Fallback: allow [Serializable] structs/classes that are not UnityEngine.Object.
        return type.IsSerializable;
    }

    private static string GuessPrefabName(string instanceName)
    {
        if (string.IsNullOrEmpty(instanceName)) return instanceName;
        return instanceName.Replace("(Clone)", string.Empty).Trim();
    }

    private static string BuildPath(Transform t)
    {
        if (t == null) return null;
        var parts = new List<string>(16);
        var cur = t;
        while (cur != null)
        {
            parts.Add($"{cur.name}#{cur.GetSiblingIndex()}");
            cur = cur.parent;
        }
        parts.Reverse();
        return string.Join("/", parts);
    }

    private static int GetSiblingIndexFromPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return -1;

        int slash = path.LastIndexOf('/');
        string last = slash >= 0 ? path.Substring(slash + 1) : path;
        int hash = last.LastIndexOf('#');
        if (hash < 0 || hash == last.Length - 1)
            return -1;

        if (int.TryParse(last.Substring(hash + 1), out int index))
            return index;
        return -1;
    }

    private static Dictionary<string, GameObject> BuildSceneLookup(Scene scene)
    {
        var dict = new Dictionary<string, GameObject>();
        if (!scene.IsValid() || !scene.isLoaded) return dict;

        foreach (var root in scene.GetRootGameObjects())
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t == null) continue;
                var go = t.gameObject;
                if (go == null || go.scene != scene) continue;
                dict[BuildPath(t)] = go;
            }
        }
        return dict;
    }

    private static Dictionary<string, GameObject> BuildSpawnedLookupById(Scene scene)
    {
        var dict = new Dictionary<string, GameObject>();
        if (!scene.IsValid() || !scene.isLoaded) return dict;

        var spawned = UnityEngine.Object.FindObjectsOfType<SpawnedObject>(true);
        foreach (var s in spawned)
        {
            if (s == null) continue;
            var go = s.gameObject;
            if (go == null || go.scene != scene) continue;
            if (string.IsNullOrEmpty(s.persistentId)) continue;
            dict[s.persistentId] = go;
        }

        return dict;
    }

    private GameObject LoadPrefab(string prefabName)
    {
        if (string.IsNullOrEmpty(prefabName))
            return null;

        var prefab = Resources.Load<GameObject>(prefabName);
        if (prefab != null)
            return prefab;

        if (!string.IsNullOrEmpty(spawnablePrefabsFolder))
        {
            string combined = $"{spawnablePrefabsFolder}/{prefabName}";
            prefab = Resources.Load<GameObject>(combined);
        }
        return prefab;
    }

    private string GetSavePath() => Path.Combine(Application.persistentDataPath, saveFileName);

    private static JsonSerializerSettings CreateJsonSettings()
    {
        return new JsonSerializerSettings
        {
            TypeNameHandling = TypeNameHandling.None,
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
            NullValueHandling = NullValueHandling.Include,
            MissingMemberHandling = MissingMemberHandling.Ignore,
            ObjectCreationHandling = ObjectCreationHandling.Replace,
        };
    }

    private static JsonSerializer CreateSerializer()
    {
        return JsonSerializer.Create(CreateJsonSettings());
    }

    private string GetEditableSceneName()
    {
        return string.IsNullOrEmpty(editableSceneName) ? mainSceneName : editableSceneName;
    }

    private Scene ResolveEditableScene()
    {
        string targetName = GetEditableSceneName();
        var scene = SceneManager.GetSceneByName(targetName);
        if (scene.IsValid())
            return scene;

        var mainScene = SceneManager.GetSceneByName(mainSceneName);
        if (mainScene.IsValid())
        {
            Debug.LogWarning($"Scene '{targetName}' not found; falling back to main scene '{mainSceneName}'.");
            return mainScene;
        }

        var active = SceneManager.GetActiveScene();
        Debug.LogWarning($"Scene '{targetName}' not found; falling back to active scene '{active.name}'.");
        return active;
    }
}
