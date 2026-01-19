using UnityEngine;
using System.Collections.Generic;
using System.IO;
using System;
using System.Linq;
using UnityEngine.SceneManagement;

public class SaveLoadManager : MonoBehaviour
{
    public static SaveLoadManager Instance { get; private set; }

    [Header("Prefabs")]
    public GameObject playerPrefab;
    public GameObject modeChangerPrefab;
    public GameObject saveLoadManagerPrefab;

    [Header("Save/Load")]
    public string saveFileName = "scene_save.json";
    [Tooltip("Scene name that contains spawned objects (e.g., 'SpawnedObjects'). Leave empty to skip.")]
    public string spawnedObjectsSceneName = "SpawnedObjects";
    [Tooltip("Resources subfolder that contains spawnable prefabs (e.g., 'SpawnablePrefabs'). Used as a fallback when loading by prefab name.")]
    public string spawnablePrefabsFolder = "SpawnablePrefabs";

    [Serializable]
    public class SavedObject
    {
        public string prefabName;
        public Vector3 position;
        public Quaternion rotation;
        public string sceneName;
        public Vector3 localScale;
        public bool gravityEnabled;
        public bool collisionEnabled;
        public bool kinematic;
        public bool grabEnabled;
        public bool enableTwoHandedScaling;
        public bool useDynamicAttach;
        public int layer;
    }

    [Serializable]
    public class SceneSave
    {
        public List<SavedObject> objects = new List<SavedObject>();
    }

    private void Awake()
    {
        // Singleton pattern
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    public void SaveScene()
    {
        var save = new SceneSave();

        // Get active scene name for better save file naming
        string activeSceneName = SceneManager.GetActiveScene().name;
        
        // Save only spawned/spawnable objects from the active scene (with SpawnedObject component)
        foreach (var go in GetSceneObjectsToSave(activeSceneName))
        {
            var spawnedComp = go.GetComponent<SpawnedObject>();
            if (spawnedComp == null)
                continue;

            var prefabName = go.name.Replace("(Clone)", "").Trim();
            save.objects.Add(new SavedObject
            {
                prefabName = prefabName,
                position = go.transform.position,
                rotation = go.transform.rotation,
                sceneName = activeSceneName,
                localScale = go.transform.localScale,
                gravityEnabled = spawnedComp.gravityEnabled,
                collisionEnabled = spawnedComp.collisionEnabled,
                kinematic = spawnedComp.kinematic,
                grabEnabled = spawnedComp.grabEnabled,
                enableTwoHandedScaling = spawnedComp.enableTwoHandedScaling,
                useDynamicAttach = spawnedComp.useDynamicAttach,
                layer = go.layer
            });
        }

        // Also save objects from SpawnedObjects scene if it exists and is different
        if (!string.IsNullOrEmpty(spawnedObjectsSceneName))
        {
            Scene spawnedScene = SceneManager.GetSceneByName(spawnedObjectsSceneName);
            if (spawnedScene.IsValid())
            {
                var spawnedObjects = spawnedScene.GetRootGameObjects();
                foreach (var go in spawnedObjects)
                {
                    var spawnedComp = go.GetComponent<SpawnedObject>();
                    if (spawnedComp == null)
                        continue;

                    var prefabName = go.name.Replace("(Clone)", "").Trim();
                    save.objects.Add(new SavedObject
                    {
                        prefabName = prefabName,
                        position = go.transform.position,
                        rotation = go.transform.rotation,
                        sceneName = spawnedObjectsSceneName,
                        localScale = go.transform.localScale,
                        gravityEnabled = spawnedComp.gravityEnabled,
                        collisionEnabled = spawnedComp.collisionEnabled,
                        kinematic = spawnedComp.kinematic,
                        grabEnabled = spawnedComp.grabEnabled,
                        enableTwoHandedScaling = spawnedComp.enableTwoHandedScaling,
                        useDynamicAttach = spawnedComp.useDynamicAttach,
                        layer = go.layer
                    });
                }
                Debug.Log($"Saved {spawnedObjects.Length} objects from '{spawnedObjectsSceneName}' scene");
            }
        }

        string json = JsonUtility.ToJson(save, true);
        File.WriteAllText(GetSavePath(), json);
        Debug.Log($"Scene saved to {GetSavePath()} ({save.objects.Count} objects)");
    }

    public void LoadScene()
    {
        string path = GetSavePath();
        if (!File.Exists(path))
        {
            Debug.LogWarning("No save file found!");
            return;
        }

        string json = File.ReadAllText(path);
        var save = JsonUtility.FromJson<SceneSave>(json);

        // Destroy only spawned objects (those with SpawnedObject component) from active scene
        string activeSceneName = SceneManager.GetActiveScene().name;
        foreach (var go in GetSceneObjectsToSave(activeSceneName))
        {
            if (go.GetComponent<SpawnedObject>() != null)
                Destroy(go);
        }

        // Destroy spawned objects from SpawnedObjects scene if it exists
        if (!string.IsNullOrEmpty(spawnedObjectsSceneName))
        {
            Scene spawnedScene = SceneManager.GetSceneByName(spawnedObjectsSceneName);
            if (spawnedScene.IsValid())
            {
                var spawnedObjects = spawnedScene.GetRootGameObjects();
                foreach (var go in spawnedObjects)
                {
                    if (go.GetComponent<SpawnedObject>() != null)
                        Destroy(go);
                }
            }
        }

        // Instantiate saved objects with correct position AND rotation
        Scene targetScene = SceneManager.GetActiveScene();
        if (!string.IsNullOrEmpty(spawnedObjectsSceneName))
        {
            Scene spawnedScene = SceneManager.GetSceneByName(spawnedObjectsSceneName);
            if (!spawnedScene.IsValid())
            {
                spawnedScene = SceneManager.CreateScene(spawnedObjectsSceneName);
            }
        }

        foreach (var obj in save.objects)
        {
            var prefab = LoadPrefab(obj.prefabName);
            if (prefab == null)
            {
                Debug.LogWarning($"Prefab '{obj.prefabName}' not found in Resources (tried '{obj.prefabName}' and '{spawnablePrefabsFolder}/{obj.prefabName}')!");
                continue;
            }

            GameObject instance = Instantiate(prefab, obj.position, obj.rotation);
            instance.transform.localScale = obj.localScale;

            // Move back to its original scene if specified
            if (!string.IsNullOrEmpty(obj.sceneName) && !string.IsNullOrEmpty(spawnedObjectsSceneName) && obj.sceneName == spawnedObjectsSceneName)
            {
                Scene spawnedScene = SceneManager.GetSceneByName(spawnedObjectsSceneName);
                if (spawnedScene.IsValid())
                {
                    SceneManager.MoveGameObjectToScene(instance, spawnedScene);
                }
            }

            // Reapply SpawnedObject runtime state
            var spawnedComp = instance.GetComponent<SpawnedObject>() ?? instance.AddComponent<SpawnedObject>();
            SetLayerRecursive(instance, obj.layer);
            spawnedComp.grabEnabled = obj.grabEnabled;
            spawnedComp.gravityEnabled = obj.gravityEnabled;
            spawnedComp.collisionEnabled = obj.collisionEnabled;
            spawnedComp.kinematic = obj.kinematic;
            spawnedComp.enableTwoHandedScaling = obj.enableTwoHandedScaling;
            spawnedComp.useDynamicAttach = obj.useDynamicAttach;

            // Apply settings through public setters to ensure physics/interactable state is correct
            spawnedComp.SetGrabbable(spawnedComp.grabEnabled);
            spawnedComp.SetCollision(spawnedComp.collisionEnabled);
            spawnedComp.SetGravity(spawnedComp.gravityEnabled);
            spawnedComp.SetKinematic(spawnedComp.kinematic);
        }

        Debug.Log($"Scene loaded from {path} ({save.objects.Count} objects)");
    }

    private GameObject LoadPrefab(string prefabName)
    {
        // Try direct load first
        var prefab = Resources.Load<GameObject>(prefabName);
        if (prefab != null)
            return prefab;

        // Fallback: prepend configured spawnable folder
        if (!string.IsNullOrEmpty(spawnablePrefabsFolder))
        {
            string combined = $"{spawnablePrefabsFolder}/{prefabName}";
            prefab = Resources.Load<GameObject>(combined);
        }

        return prefab;
    }

    private IEnumerable<GameObject> GetSceneObjectsToSave()
    {
        // Only root objects, and skip protected prefabs
        var roots = SceneManager.GetActiveScene().GetRootGameObjects();
        foreach (var go in roots)
        {
            if (IsProtected(go)) continue;
            yield return go;
        }
    }

    private IEnumerable<GameObject> GetSceneObjectsToSave(string sceneName)
    {
        // Get objects from a specific scene, filtering to spawned objects only
        Scene scene = SceneManager.GetSceneByName(sceneName);
        if (!scene.IsValid())
        {
            yield break;
        }

        var roots = scene.GetRootGameObjects();
        foreach (var go in roots)
        {
            if (IsProtected(go)) continue;
            if (go.GetComponent<SpawnedObject>() == null) continue; // only save spawned objects
            yield return go;
        }
    }

    private bool IsProtected(GameObject go)
    {
        // Only one of each protected prefab per scene
        if (playerPrefab != null && go.name.StartsWith(playerPrefab.name)) return true;
        if (modeChangerPrefab != null && go.name.StartsWith(modeChangerPrefab.name)) return true;
        if (saveLoadManagerPrefab != null && go.name.StartsWith(saveLoadManagerPrefab.name)) return true;
        if (go == this.gameObject) return true;
        return false;
    }

    private void SetLayerRecursive(GameObject obj, int newLayer)
    {
        if (newLayer < 0) return;
        obj.layer = newLayer;
        foreach (Transform child in obj.transform)
        {
            SetLayerRecursive(child.gameObject, newLayer);
        }
    }

    private string GetSavePath()
    {
        return Path.Combine(Application.persistentDataPath, saveFileName);
    }
}