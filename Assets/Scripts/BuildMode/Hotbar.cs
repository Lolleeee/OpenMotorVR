using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.SceneManagement;
using System.Collections.Generic;

public class Hotbar : MonoBehaviour
{
    [Header("References")]
    public GameObject hotbarPrefab;
    public GameObject hotbarItemPrefab; // Single item template
    public NearFarInteractor nearFarInteractor;
    
    [Header("Input Actions")]
    public InputActionProperty toggleHotbarAction;
    public InputActionProperty spawnAction;
    public InputActionProperty navigationAction;

    public bool allowHotbar = true;
    
    [Header("Settings")]
    public Vector3 hotbarOffset = new Vector3(0, 0, 0.15f);
    public Vector3 hotbarRotationOffsetEuler = Vector3.zero;
    public Vector3 spawnOffset = new Vector3(0, 0, 0.3f);
    public string prefabFolderPath = "SpawnablePrefabs";
    
    [Header("Grid Settings")]
    public int visibleColumns = 3;
    public int visibleRows = 3;
    public float cellSpacing = 0.06f;

    [Header("Visual Settings")]
    public Material backgroundMaterial; // Assign custom material
    [Tooltip("If no material assigned, creates transparent material with this color")]
    public Color defaultBackgroundColor = new Color(0.3f, 0.3f, 0.3f, 0.05f);
    public Vector3 backgroundLocalPosition = Vector3.zero;
    public Vector3 backgroundLocalScale = Vector3.one;
    public Vector3 previewLocalPosition = new Vector3(0, 0, -0.1f);
    public float previewScale = 0.8f;
    public bool rotatePreview = true;

    [Header("Locomotion Control")]
    public GameObject locomotionObject;
    
    [Header("Scene Management")]
    [Tooltip("Name of the scene where spawned objects will be instantiated. Will be created if it doesn't exist.")]
    public string spawnedObjectsSceneName = "SpawnedObjects";
    
    private Scene spawnedObjectsScene;
    private GameObject activeHotbar;
    private Transform gridContainer;
    private GameObject selectionIndicator;
    private List<GameObject> loadedPrefabs = new List<GameObject>();
    private List<HotbarItem> visibleItems = new List<HotbarItem>();
    
    // Virtual grid tracking
    private int totalItems = 0;
    private int totalRows = 0;
    private int currentVirtualRow = 0; // Which row of the full grid we're viewing
    private int currentVirtualCol = 0;
    private int scrollOffset = 0; // How many rows we've scrolled
    
    private bool wasTogglePressed = false;
    private float navigationCooldown = 0f;
    
    void Start()
    {
        EnsureSpawnedObjectsScene();
    }
    
    void EnsureSpawnedObjectsScene()
    {
        // Check if the scene already exists
        spawnedObjectsScene = SceneManager.GetSceneByName(spawnedObjectsSceneName);
        
        if (!spawnedObjectsScene.IsValid())
        {
            // Create new scene
            spawnedObjectsScene = SceneManager.CreateScene(spawnedObjectsSceneName);
            Debug.Log($"Created new scene for spawned objects: {spawnedObjectsSceneName}");
        }
        else
        {
            Debug.Log($"Using existing scene for spawned objects: {spawnedObjectsSceneName}");
        }
    }
    
    void LoadPrefabsFromFolder()
    {
        GameObject[] prefabs = Resources.LoadAll<GameObject>(prefabFolderPath);
        loadedPrefabs.Clear();
        loadedPrefabs.AddRange(prefabs);
        
        totalItems = loadedPrefabs.Count;
        totalRows = Mathf.CeilToInt((float)totalItems / visibleColumns);
        
        Debug.Log($"Loaded {totalItems} prefabs - Grid will have {totalRows} total rows");
    }
    
    void Update()
    {   
        HandleToggleHotbar();
        
        if (activeHotbar != null)
        {
            UpdateHotbarPosition();
            HandleNavigation();
            HandleSpawn();
        }
        
        if (navigationCooldown > 0)
            navigationCooldown -= Time.deltaTime;
    }
    
    void HandleToggleHotbar()
    {
        bool isPressed = toggleHotbarAction.action.ReadValue<float>() > 0.5f;
        
        if (isPressed && !wasTogglePressed)
        {
            if (activeHotbar == null)
                OpenHotbar();
            else
                CloseHotbar();
        }
        
        wasTogglePressed = isPressed;
    }
    
    void OpenHotbar()
    {   
        
        Transform attachTransform = nearFarInteractor.attachTransform;
        Vector3 hotbarPosition = attachTransform.position + attachTransform.TransformDirection(hotbarOffset);
        Quaternion hotbarRotation = attachTransform.rotation;

        activeHotbar = Instantiate(hotbarPrefab, hotbarPosition, hotbarRotation);

        if (locomotionObject != null)
        {
            locomotionObject.SetActive(false);
            Debug.Log("Locomotion disabled");
        }
        
        // Find or create grid container
        gridContainer = activeHotbar.transform.Find("GridContainer");
        if (gridContainer == null)
        {
            GameObject container = new GameObject("GridContainer");
            container.transform.parent = activeHotbar.transform;
            container.transform.localPosition = Vector3.zero;
            container.transform.localRotation = Quaternion.identity;
            gridContainer = container.transform;
        }
        
        // Find or create selection indicator
        selectionIndicator = activeHotbar.transform.Find("SelectionIndicator")?.gameObject;
        if (selectionIndicator == null)
        {
            selectionIndicator = GameObject.CreatePrimitive(PrimitiveType.Cube);
            selectionIndicator.name = "SelectionIndicator";
            selectionIndicator.transform.parent = activeHotbar.transform;
            selectionIndicator.transform.localScale = Vector3.one * (cellSpacing * 0.9f);
            
            // Make it glow
            MeshRenderer renderer = selectionIndicator.GetComponent<MeshRenderer>();
            Material mat = new Material(Shader.Find("Standard"));
            mat.SetColor("_EmissionColor", Color.yellow * 2f);
            mat.EnableKeyword("_EMISSION");
            renderer.material = mat;
        }
        
        // Create visible grid
        CreateVisibleGrid();
        
        // Reset virtual position
        currentVirtualRow = 0;
        currentVirtualCol = 0;
        scrollOffset = 0;
        
        UpdateGridContent();
        UpdateSelection();
        
        Debug.Log($"Hotbar opened - {visibleColumns}x{visibleRows} visible window, {totalRows} total rows");
    }
    
    void CreateVisibleGrid()
    {
        visibleItems.Clear();
        
        for (int row = 0; row < visibleRows; row++)
        {
            for (int col = 0; col < visibleColumns; col++)
            {
                // Create cell background as PLANE
                GameObject itemObj = GameObject.CreatePrimitive(PrimitiveType.Quad);
                itemObj.name = $"Item_{row}_{col}";
                itemObj.transform.parent = gridContainer;
                
                float xPos = (col - (visibleColumns - 1) / 2f) * cellSpacing;
                float yPos = ((visibleRows - 1) / 2f - row) * cellSpacing;
                itemObj.transform.localPosition = new Vector3(xPos, yPos, 0) + backgroundLocalPosition;
                itemObj.transform.localRotation = Quaternion.identity;
                itemObj.transform.localScale = backgroundLocalScale * (cellSpacing * 0.9f);
                
                // Apply material - INSTANTIATE IT!
                MeshRenderer bgRenderer = itemObj.GetComponent<MeshRenderer>();
                
                if (backgroundMaterial != null)
                {
                    // Create NEW instance of the material (don't modify the asset!)
                    bgRenderer.material = new Material(backgroundMaterial);
                    Debug.Log($"Applied custom material: {backgroundMaterial.name}");
                }
                else
                {
                    // Create new material instance with default color
                    Material newMat = new Material(bgRenderer.material); // Copy Quad's default
                    newMat.color = defaultBackgroundColor;
                    bgRenderer.material = newMat;
                    Debug.Log($"Created default material with color: {defaultBackgroundColor}");
                }
                
                // Create preview container
                GameObject previewContainer = new GameObject("PreviewContainer");
                previewContainer.transform.parent = itemObj.transform;
                previewContainer.transform.localPosition = previewLocalPosition;
                previewContainer.transform.localRotation = Quaternion.identity;
                previewContainer.transform.localScale = Vector3.one;
                
                // Add HotbarItem component
                HotbarItem item = itemObj.AddComponent<HotbarItem>();
                item.backgroundRenderer = bgRenderer;
                item.previewContainer = previewContainer.transform;
                item.previewScale = previewScale;
                item.rotatePreview = rotatePreview;
                
                // Remove default collider from quad primitive so interactor passes through
                Collider meshCollider = itemObj.GetComponent<Collider>();
                if (meshCollider != null)
                    DestroyImmediate(meshCollider);
                
                visibleItems.Add(item);
            }
        }
        
        Debug.Log($"Created {visibleItems.Count} grid cells");
    }


    
    void UpdateGridContent()
    {
        // Update what each visible cell shows based on scroll offset
        for (int visibleRow = 0; visibleRow < visibleRows; visibleRow++)
        {
            for (int visibleCol = 0; visibleCol < visibleColumns; visibleCol++)
            {
                int visibleIndex = visibleRow * visibleColumns + visibleCol;
                
                // Calculate which item in the full list this corresponds to
                int actualRow = visibleRow + scrollOffset;
                int actualIndex = actualRow * visibleColumns + visibleCol;
                
                HotbarItem item = visibleItems[visibleIndex];
                
                if (actualIndex < totalItems)
                {
                    // Valid item - show it
                    item.SetPrefab(loadedPrefabs[actualIndex]);
                    item.gameObject.SetActive(true);
                }
                else
                {
                    // Beyond our items - hide it
                    item.SetPrefab(null);
                    item.gameObject.SetActive(false);
                }
            }
        }
    }
    
    void CloseHotbar()
    {
        if (locomotionObject != null)
        {
            locomotionObject.SetActive(true);
            Debug.Log("Locomotion re-enabled");
        }

        if (activeHotbar != null)
        {
            Destroy(activeHotbar);
            activeHotbar = null;
            gridContainer = null;
            selectionIndicator = null;
            visibleItems.Clear();
        }
    }
    
    void UpdateHotbarPosition()
    {
        Transform attachTransform = nearFarInteractor.attachTransform;
        Vector3 hotbarPosition = attachTransform.position + attachTransform.TransformDirection(hotbarOffset);
        Quaternion hotbarRotation = attachTransform.rotation * Quaternion.Euler(hotbarRotationOffsetEuler);
        
        activeHotbar.transform.position = hotbarPosition;
        activeHotbar.transform.rotation = hotbarRotation;
    }
    
    void HandleNavigation()
    {
        Vector2 navigationInput = navigationAction.action.ReadValue<Vector2>();
        
        if (navigationCooldown <= 0 && (Mathf.Abs(navigationInput.x) > 0.5f || Mathf.Abs(navigationInput.y) > 0.5f))
        {
            int oldRow = currentVirtualRow;
            int oldCol = currentVirtualCol;
            
            // Navigate columns using X-axis
            if (navigationInput.x > 0.5f) // Right
            {
                currentVirtualCol++;
            }
            else if (navigationInput.x < -0.5f) // Left
            {
                currentVirtualCol--;
            }
            
            // Navigate rows using Y-axis
            if (navigationInput.y > 0.5f) // Up
            {
                currentVirtualRow--;
            }
            else if (navigationInput.y < -0.5f) // Down
            {
                currentVirtualRow++;
            }
            
            // Wrap columns
            if (currentVirtualCol < 0)
                currentVirtualCol = visibleColumns - 1;
            else if (currentVirtualCol >= visibleColumns)
                currentVirtualCol = 0;
            
            // Wrap around at boundaries
            if (currentVirtualRow < 0)
                currentVirtualRow = totalRows - 1;
            else if (currentVirtualRow >= totalRows)
                currentVirtualRow = 0;
            
            // Check if we need to scroll
            bool needsUpdate = false;
            
            // Scroll down if we go past bottom of visible window
            if (currentVirtualRow >= scrollOffset + visibleRows)
            {
                scrollOffset = currentVirtualRow - visibleRows + 1;
                needsUpdate = true;
            }
            // Scroll up if we go above top of visible window
            else if (currentVirtualRow < scrollOffset)
            {
                scrollOffset = currentVirtualRow;
                needsUpdate = true;
            }
            
            // Clamp scroll offset
            scrollOffset = Mathf.Clamp(scrollOffset, 0, Mathf.Max(0, totalRows - visibleRows));
            
            if (needsUpdate)
            {
                UpdateGridContent();
                Debug.Log($"Scrolled to offset: {scrollOffset}");
            }
            
            if (oldRow != currentVirtualRow || oldCol != currentVirtualCol)
            {
                UpdateSelection();
                navigationCooldown = 0.2f;
            }
        }
    }
    
    void UpdateSelection()
    {
        if (selectionIndicator == null || visibleItems.Count == 0)
            return;
        
        // Calculate which visible cell corresponds to current virtual position
        int visibleRow = currentVirtualRow - scrollOffset;
        int visibleCol = currentVirtualCol;
        
        // Make sure we're in visible range
        if (visibleRow >= 0 && visibleRow < visibleRows && visibleCol >= 0 && visibleCol < visibleColumns)
        {
            int visibleIndex = visibleRow * visibleColumns + visibleCol;
            
            if (visibleIndex < visibleItems.Count)
            {
                Vector3 itemPosition = visibleItems[visibleIndex].transform.localPosition;
                selectionIndicator.transform.localPosition = itemPosition;
                selectionIndicator.SetActive(true);
                
                int actualIndex = currentVirtualRow * visibleColumns + currentVirtualCol;
                string itemName = actualIndex < totalItems ? loadedPrefabs[actualIndex].name : "Empty";
                Debug.Log($"Selected: [{currentVirtualRow},{currentVirtualCol}] = {itemName}");
            }
        }
        else
        {
            selectionIndicator.SetActive(false);
        }
    }
    
    void HandleSpawn()
    {
        if (spawnAction.action.triggered)
        {
            SpawnSelectedItem();
        }
    }
    
    void SpawnSelectedItem()
    {
        int actualIndex = currentVirtualRow * visibleColumns + currentVirtualCol;
        
        if (actualIndex >= 0 && actualIndex < totalItems)
        {
            GameObject prefabToSpawn = loadedPrefabs[actualIndex];
            
            if (prefabToSpawn != null)
            {
                Transform attachTransform = nearFarInteractor.attachTransform;
                // Calculate prefab bounds size (use Renderer or Collider)
                float size = 1f;
                var renderer = prefabToSpawn.GetComponentInChildren<Renderer>();
                if (renderer != null)
                    size = renderer.bounds.size.magnitude;
                else
                {
                    var collider = prefabToSpawn.GetComponentInChildren<Collider>();
                    if (collider != null)
                        size = collider.bounds.size.magnitude;
                }

                // Scale the offset based on size (tweak multiplier as needed)
                float scaleMultiplier = 0.6f; // 1.0 = full size, 0.5 = half, etc.
                Vector3 scaledOffset = spawnOffset.normalized * (spawnOffset.magnitude + size * scaleMultiplier);

                Vector3 spawnPosition = attachTransform.position + attachTransform.TransformDirection(scaledOffset);

                Quaternion spawnRotation = attachTransform.rotation;
                
                GameObject spawnedObject = Instantiate(prefabToSpawn, spawnPosition, spawnRotation);
                
                // Move to spawned objects scene
                if (spawnedObjectsScene.IsValid())
                {
                    SceneManager.MoveGameObjectToScene(spawnedObject, spawnedObjectsScene);
                }
                
                // ADD SPAWNED OBJECT COMPONENT
                SpawnedObject spawnedComp = spawnedObject.GetComponent<SpawnedObject>();
                if (spawnedComp == null)
                {
                    spawnedComp = spawnedObject.AddComponent<SpawnedObject>();
                }
                
                Debug.Log($"Spawned: {prefabToSpawn.name} in scene '{spawnedObjectsScene.name}'");
            }
        }
        else
        {
            Debug.LogWarning("No prefab at selected position");
        }
    }

    private void OnEnable()
    {   
        if (ModeManager.Instance != null)
        {
            ModeManager.Instance.OnModeChanged += HandleModeChanged;
        }

        if (ModeManager.Instance.IsBuildMode)
        {
            toggleHotbarAction.action.Enable();
            spawnAction.action.Enable();
            navigationAction.action.Enable();
        }
        else
        {
            toggleHotbarAction.action.Disable();
            spawnAction.action.Disable();
            navigationAction.action.Disable();
        }
        LoadPrefabsFromFolder();
    }

    private void OnDisable()
    {
        if (ModeManager.Instance != null)
        {
            ModeManager.Instance.OnModeChanged -= HandleModeChanged;
        }
        toggleHotbarAction.action.Disable();
        spawnAction.action.Disable();
        navigationAction.action.Disable();
        CloseHotbar();
    }

    private void HandleModeChanged(Mode newMode)
    {
        Debug.Log($"ContextMenu detected mode change: {newMode}");

        if (ModeManager.Instance.IsBuildMode)
        {
            toggleHotbarAction.action.Enable();
            spawnAction.action.Enable();
            navigationAction.action.Enable();
            LoadPrefabsFromFolder();
        }
        else
        {
            toggleHotbarAction.action.Disable();
            spawnAction.action.Disable();
            navigationAction.action.Disable();
            CloseHotbar();
        }
    }

    // Public methods for saving/loading spawned objects scene
    public void SaveSpawnedObjectsScene()
    {
        if (!spawnedObjectsScene.IsValid())
        {
            Debug.LogWarning("Spawned objects scene is not valid. Cannot save.");
            return;
        }

#if UNITY_EDITOR
        if (!Application.isPlaying)
        {
            UnityEditor.SceneManagement.EditorSceneManager.SaveScene(spawnedObjectsScene);
            Debug.Log($"Saved spawned objects scene: {spawnedObjectsSceneName}");
        }
        else
        {
            Debug.LogWarning("Cannot save scene during runtime. Use this in the editor only.");
        }
#else
        Debug.LogWarning("Scene saving is only available in the Unity Editor.");
#endif
    }

    public void ClearSpawnedObjects()
    {
        if (!spawnedObjectsScene.IsValid())
        {
            Debug.LogWarning("Spawned objects scene is not valid.");
            return;
        }

        GameObject[] rootObjects = spawnedObjectsScene.GetRootGameObjects();
        foreach (GameObject obj in rootObjects)
        {
            Destroy(obj);
        }
        
        Debug.Log($"Cleared all objects from scene: {spawnedObjectsSceneName}");
    }
}
