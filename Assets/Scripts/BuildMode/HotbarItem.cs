using UnityEngine;
using System.Collections;

public class HotbarItem : MonoBehaviour
{
    [Header("Item Data")]
    public GameObject prefabToSpawn;
    
    [Header("Visual")]
    public MeshRenderer backgroundRenderer;
    public Transform previewContainer;
    
    [Header("Preview Settings")]
    public float previewScale = 0.8f;
    public bool rotatePreview = true;
    public float rotationSpeed = 30f;
    [Tooltip("Size threshold after which logarithmic scaling starts")]
    public float logScaleThreshold = 0.1f;
    [Tooltip("Logarithm base for scaling objects larger than threshold")]
    public float logScaleBase = 2f;
    
    private GameObject previewInstance;
    
    void Start()
    {
        if (backgroundRenderer == null)
            backgroundRenderer = GetComponent<MeshRenderer>();
        
        // REMOVED: Don't override material - VRHotbar sets it!
        // The material is already set by VRHotbar's CreateVisibleGrid()
    }
    
    public void SetPrefab(GameObject prefab)
    {
        prefabToSpawn = prefab;
        CreateMiniaturePreview();
    }
    
    void CreateMiniaturePreview()
    {
        // Clean up old preview
        if (previewInstance != null)
            Destroy(previewInstance);
        
        if (prefabToSpawn == null || previewContainer == null)
            return;
        
        // Spawn tiny version
        previewInstance = Instantiate(prefabToSpawn, previewContainer);
        previewInstance.transform.localPosition = Vector3.zero;
        previewInstance.transform.localRotation = Quaternion.Euler(15, 45, 0);
        
        // Calculate bounds to scale properly
        Bounds bounds = CalculateBounds(previewInstance);
        
        if (bounds.size.magnitude > 0.001f)
        {
            // Get the maximum dimension of the object
            float maxSize = Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z);
            
            // Target preview size (base size when object is at threshold)
            float targetSize = 0.04f * previewScale;
            
            // Calculate scale with logarithmic growth for large objects
            float scale;
            if (maxSize <= logScaleThreshold)
            {
                // Below threshold: linear scale down
                scale = targetSize / maxSize;
            }
            else
            {
                // Above threshold: logarithmic growth
                // scale = targetSize * log_base(maxSize / threshold)
                float ratio = maxSize / logScaleThreshold;
                float logFactor = Mathf.Log(ratio, logScaleBase);
                scale = (targetSize / logScaleThreshold) * Mathf.Max(1f, logFactor);
            }
            
            // Apply scale
            previewInstance.transform.localScale = Vector3.one * scale;
            
            // Center it
            Vector3 offset = bounds.center - previewInstance.transform.position;
            previewInstance.transform.localPosition = -offset * scale;
        }
        else
        {
            // Fallback scale
            previewInstance.transform.localScale = Vector3.one * 0.02f;
        }
        
        // Remove physics and interaction components
        CleanupPreview(previewInstance);
        
        // Start rotation
        if (rotatePreview)
        {
            StartCoroutine(RotatePreview());
        }
    }
    
    Bounds CalculateBounds(GameObject obj)
    {
        Renderer[] renderers = obj.GetComponentsInChildren<Renderer>();
        
        if (renderers.Length == 0)
            return new Bounds(obj.transform.position, Vector3.zero);
        
        Bounds bounds = renderers[0].bounds;
        
        foreach (Renderer renderer in renderers)
        {
            bounds.Encapsulate(renderer.bounds);
        }
        
        return bounds;
    }
    
    void CleanupPreview(GameObject preview)
    {
        // Remove all physics
        foreach (var rb in preview.GetComponentsInChildren<Rigidbody>())
            Destroy(rb);
        
        // Remove all colliders
        foreach (var col in preview.GetComponentsInChildren<Collider>())
            Destroy(col);
        
        // Remove XR interactables
        foreach (var interactable in preview.GetComponentsInChildren<UnityEngine.XR.Interaction.Toolkit.Interactables.XRBaseInteractable>())
            Destroy(interactable);
        
        // Set to UI layer to prevent interaction
        SetLayerRecursively(preview, LayerMask.NameToLayer("UI"));
    }
    
    void SetLayerRecursively(GameObject obj, int layer)
    {
        obj.layer = layer;
        foreach (Transform child in obj.transform)
        {
            SetLayerRecursively(child.gameObject, layer);
        }
    }
    
    IEnumerator RotatePreview()
    {
        while (previewInstance != null)
        {
            previewInstance.transform.Rotate(Vector3.up, rotationSpeed * Time.deltaTime, Space.Self);
            yield return null;
        }
    }
    
    void OnDestroy()
    {
        if (previewInstance != null)
            Destroy(previewInstance);
    }
}
