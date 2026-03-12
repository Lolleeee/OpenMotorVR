using UnityEngine;
using UnityEngine.SceneManagement;

public class SceneLoader : MonoBehaviour
{
    [Header("Scene Names")]
    [Tooltip("Persistent scene with player/managers (loaded first).")]
    [SerializeField] private string mainSceneName = "Persistent";

    [Tooltip("Editable/content scene to load additively and to save.")]
    [SerializeField] private string contentSceneName = "Scene";

    [Header("Optional References")]
    [Tooltip("SaveSystem that will save/load the content scene. Auto-syncs scene names.")]
    [SerializeField] private SaveSystem saveSystem;

    private static SceneLoader instance;

    private void Awake()
    {
        if (instance == null)
        {
            instance = this;
            DontDestroyOnLoad(gameObject);
        }
        else
        {
            Destroy(gameObject);
        }
    }

    /// <summary>
    /// Load the Main scene (persistent - player, managers)
    /// </summary>
    public void LoadMainScene()
    {
        if (!SceneManager.GetSceneByName(mainSceneName).isLoaded)
        {
            SceneManager.LoadScene(mainSceneName, LoadSceneMode.Single);
            Debug.Log($"Main scene loaded: {mainSceneName}");
        }
    }

    /// <summary>
    /// Load the content scene additively with Main
    /// </summary>
    public void LoadContentScene()
    {
        Scene mainScene = SceneManager.GetSceneByName(mainSceneName);
        
        if (!mainScene.isLoaded)
        {
            Debug.LogWarning("Main scene must be loaded first!");
            LoadMainScene();
        }

        if (!SceneManager.GetSceneByName(contentSceneName).isLoaded)
        {
            SceneManager.LoadScene(contentSceneName, LoadSceneMode.Additive);
            Debug.Log($"Content scene loaded additively: {contentSceneName}");
        }
    }

    /// <summary>
    /// Unload the content scene (keeps Main loaded)
    /// </summary>
    public void UnloadContentScene()
    {
        Scene contentScene = SceneManager.GetSceneByName(contentSceneName);
        
        if (contentScene.isLoaded)
        {
            SceneManager.UnloadSceneAsync(contentScene);
            Debug.Log($"Content scene unloaded: {contentSceneName}");
        }
    }

    /// <summary>
    /// Reload the content scene
    /// </summary>
    public void ReloadContentScene()
    {
        UnloadContentScene();
        // Small delay to ensure unload completes
        Invoke(nameof(LoadContentScene), 0.1f);
    }

    /// <summary>
    /// Get the content scene
    /// </summary>
    public Scene GetContentScene()
    {
        return SceneManager.GetSceneByName(contentSceneName);
    }

    /// <summary>
    /// Get the main scene
    /// </summary>
    public Scene GetMainScene()
    {
        return SceneManager.GetSceneByName(mainSceneName);
    }

    /// Save the editable/content scene via SaveSystem
    public void SaveContentScene()
    {
        EnsureSaveSystem();
        if (saveSystem == null)
        {
            Debug.LogWarning("SaveSystem not assigned and not found in scene.");
            return;
        }

        EnsureScenesLoaded();
        saveSystem.SaveScene();
    }

    /// Load the last save for the editable/content scene via SaveSystem
    public void LoadLastSave()
    {
        EnsureSaveSystem();
        if (saveSystem == null)
        {
            Debug.LogWarning("SaveSystem not assigned and not found in scene.");
            return;
        }

        EnsureScenesLoaded();
        saveSystem.LoadScene();
    }

    private void EnsureSaveSystem()
    {
        if (saveSystem == null)
        {
            saveSystem = FindFirstObjectByType<SaveSystem>();
        }

        // Sync scene names with SaveSystem if found
        if (saveSystem != null)
        {
            SyncSceneNamesWithSaveSystem();
        }
    }

    private void SyncSceneNamesWithSaveSystem()
    {
        // Get SaveSystem's private fields via reflection to sync scene names
        var mainSceneField = typeof(SaveSystem).GetField("mainSceneName", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var editableSceneField = typeof(SaveSystem).GetField("editableSceneName", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        if (mainSceneField != null)
        {
            string saveSystemMainScene = (string)mainSceneField.GetValue(saveSystem);
            if (!string.IsNullOrEmpty(saveSystemMainScene) && saveSystemMainScene != mainSceneName)
            {
                Debug.Log($"Syncing main scene name: '{mainSceneName}' -> '{saveSystemMainScene}'");
                mainSceneName = saveSystemMainScene;
            }
        }

        if (editableSceneField != null)
        {
            string saveSystemEditableScene = (string)editableSceneField.GetValue(saveSystem);
            if (!string.IsNullOrEmpty(saveSystemEditableScene) && saveSystemEditableScene != contentSceneName)
            {
                Debug.Log($"Syncing content scene name: '{contentSceneName}' -> '{saveSystemEditableScene}'");
                contentSceneName = saveSystemEditableScene;
            }
        }
    }

    private void EnsureScenesLoaded()
    {
        // Ensure main is loaded
        if (!SceneManager.GetSceneByName(mainSceneName).isLoaded)
        {
            LoadMainScene();
        }

        // Ensure content is loaded additively
        if (!SceneManager.GetSceneByName(contentSceneName).isLoaded)
        {
            LoadContentScene();
        }
    }

    public static SceneLoader Instance => instance;
}
