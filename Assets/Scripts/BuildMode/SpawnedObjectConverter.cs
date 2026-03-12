using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactors;


public class SpawnedObjectConverter : MonoBehaviour
{
    [SerializeField] private InputActionProperty triggerInputAction;
    [SerializeField] private NearFarInteractor nearFarInteractor;
    [SerializeField] private float raycastDistance = 100f;
    [SerializeField] private Color glowColor = Color.green;
    [SerializeField] private float glowDuration = 1.5f;
    [SerializeField] private float glowIntensity = 2f;
    [SerializeField] private LineRenderer rayLine;
    [SerializeField] private Color rayColor = Color.cyan;
    [SerializeField] private float rayWidth = 0.01f;
    [SerializeField] private Color hitboxColor = Color.yellow;
    [SerializeField] private Color convertedColor = Color.green;

    private GameObject targetObject;
    private bool isTriggerPressed = false;
    private LineRenderer hitboxRenderer;
    
    private void OnEnable()
    {
        Debug.Log("SpawnedObjectConverter OnEnable called");
        
        if (triggerInputAction.action != null)
        {
            Debug.Log($"Trigger action found: {triggerInputAction.action.name}, enabled: {triggerInputAction.action.enabled}");
            triggerInputAction.action.Enable();
            triggerInputAction.action.started += OnTriggerPressed;
            triggerInputAction.action.canceled += OnTriggerReleased;
            Debug.Log("Trigger callbacks registered");
        }
        else
        {
            Debug.LogWarning("Trigger Input Action is NULL!");
        }

        // Create LineRenderer if not assigned
        if (rayLine == null)
        {
            GameObject lineObj = new GameObject("ConversionRay");
            lineObj.transform.SetParent(transform);
            rayLine = lineObj.AddComponent<LineRenderer>();
            rayLine.startWidth = rayWidth;
            rayLine.endWidth = rayWidth;
            rayLine.material = new Material(Shader.Find("Sprites/Default"));
            rayLine.startColor = rayColor;
            rayLine.endColor = rayColor;
        }
        rayLine.enabled = false;

        // Create hitbox LineRenderer
        GameObject hitboxObj = new GameObject("HitboxRenderer");
        hitboxObj.transform.SetParent(transform);
        hitboxRenderer = hitboxObj.AddComponent<LineRenderer>();
        hitboxRenderer.startWidth = 0.02f;
        hitboxRenderer.endWidth = 0.02f;
        hitboxRenderer.loop = true;
        hitboxRenderer.useWorldSpace = true;
        hitboxRenderer.material = new Material(Shader.Find("Sprites/Default"));
        hitboxRenderer.startColor = hitboxColor;
        hitboxRenderer.endColor = hitboxColor;
        hitboxRenderer.positionCount = 16;
        hitboxRenderer.enabled = false;
    }

    private void OnDisable()
    {
        if (triggerInputAction.action != null)
        {
            triggerInputAction.action.started -= OnTriggerPressed;
            triggerInputAction.action.canceled -= OnTriggerReleased;
        }
    }

    private void Update()
    {
        // Debug trigger value
        if (triggerInputAction.action != null)
        {
            float triggerValue = triggerInputAction.action.ReadValue<float>();
            if (triggerValue > 0.01f)
            {
                Debug.Log($"Trigger value: {triggerValue}");
            }
        }
        
        if (isTriggerPressed)
        {
            UpdateRaycast();
        }
    }

    private void OnTriggerPressed(InputAction.CallbackContext context)
    {
        Debug.Log("Trigger pressed - conversion ray enabled");
        isTriggerPressed = true;
        rayLine.enabled = true;
    }

    private void OnTriggerReleased(InputAction.CallbackContext context)
    {
        Debug.Log("Trigger released");
        isTriggerPressed = false;
        rayLine.enabled = false;
        hitboxRenderer.enabled = false;

        // Perform conversion on the target object
        if (targetObject != null)
        {
            ProcessObject(targetObject);
            targetObject = null;
        }
    }

    private void UpdateRaycast()
    {
        Vector3 rayOrigin = transform.position;
        Vector3 rayDirection = transform.forward;

        // Use NearFarInteractor ray if it's active
        if (nearFarInteractor != null && nearFarInteractor.enabled)
        {
            rayOrigin = nearFarInteractor.curveOrigin.position;
            rayDirection = nearFarInteractor.curveOrigin.forward;
        }

        RaycastHit hitInfo;
        if (Physics.Raycast(rayOrigin, rayDirection, out hitInfo, raycastDistance))
        {
            GameObject hitObject = hitInfo.collider.gameObject;
            targetObject = hitObject;

            // Draw ray to hit point
            rayLine.SetPosition(0, rayOrigin);
            rayLine.SetPosition(1, hitInfo.point);

            // Show hitbox
            DrawHitbox(hitInfo.collider);
        }
        else
        {
            // No hit, draw ray to max distance
            targetObject = null;
            rayLine.SetPosition(0, rayOrigin);
            rayLine.SetPosition(1, rayOrigin + rayDirection * raycastDistance);
            hitboxRenderer.enabled = false;
        }
    }

    private void DrawHitbox(Collider collider)
    {
        hitboxRenderer.enabled = true;
        Bounds bounds = collider.bounds;

        Vector3 min = bounds.min;
        Vector3 max = bounds.max;

        // Create box corners
        Vector3[] corners = new Vector3[16];
        
        // Bottom face
        corners[0] = new Vector3(min.x, min.y, min.z);
        corners[1] = new Vector3(max.x, min.y, min.z);
        corners[2] = new Vector3(max.x, min.y, max.z);
        corners[3] = new Vector3(min.x, min.y, max.z);
        corners[4] = new Vector3(min.x, min.y, min.z); // back to start
        
        // Up to top face
        corners[5] = new Vector3(min.x, max.y, min.z);
        corners[6] = new Vector3(max.x, max.y, min.z);
        corners[7] = new Vector3(max.x, min.y, min.z); // back down
        corners[8] = new Vector3(max.x, max.y, min.z); // back up
        corners[9] = new Vector3(max.x, max.y, max.z);
        corners[10] = new Vector3(max.x, min.y, max.z); // back down
        corners[11] = new Vector3(max.x, max.y, max.z); // back up
        corners[12] = new Vector3(min.x, max.y, max.z);
        corners[13] = new Vector3(min.x, min.y, max.z); // back down
        corners[14] = new Vector3(min.x, max.y, max.z); // back up
        corners[15] = new Vector3(min.x, max.y, min.z);

        hitboxRenderer.SetPositions(corners);
    }

    private bool IsSpawnedObject(GameObject obj)
    {
        return obj.GetComponent<SpawnedObject>() != null;
    }

    private void ProcessObject(GameObject detectedObject)
    {
        // Find the root object with SpawnedObject component (might be on parent)
        GameObject rootObject = detectedObject;
        SpawnedObject spawnedComponent = detectedObject.GetComponent<SpawnedObject>();
        
        // If not found on this object, check parent hierarchy
        if (spawnedComponent == null)
        {
            spawnedComponent = detectedObject.GetComponentInParent<SpawnedObject>();
            if (spawnedComponent != null)
            {
                rootObject = spawnedComponent.gameObject;
                Debug.Log($"Found SpawnedObject on parent: {rootObject.name}");
            }
        }
        
        Collider detectedCollider = rootObject.GetComponent<Collider>();
        if (detectedCollider == null)
        {
            // If root doesn't have collider, use the one we hit
            detectedCollider = detectedObject.GetComponent<Collider>();
        }
        
        // Check if it already has SpawnedObject component - if so, remove it
        if (spawnedComponent != null)
        {
            Debug.Log($"{rootObject.name} is a spawned object - removing XRI components.");
            
            // Mark as converted and save original prefab name for save system
            string prefabName = rootObject.name.Replace("(Clone)", "").Trim();
            var converted = rootObject.GetComponent<ConvertedObject>() ?? rootObject.AddComponent<ConvertedObject>();
            converted.Initialize(prefabName);
            
            // Remove only XRI interaction components from the ROOT object, keep Transform and Collider
            Destroy(spawnedComponent);
            
            var grabInteractable = rootObject.GetComponent<UnityEngine.XR.Interaction.Toolkit.Interactables.XRGrabInteractable>();
            if (grabInteractable != null)
            {
                Debug.Log($"Destroying XRGrabInteractable from {rootObject.name}");
                Destroy(grabInteractable);
            }
            
            var simpleInteractable = rootObject.GetComponent<UnityEngine.XR.Interaction.Toolkit.Interactables.XRSimpleInteractable>();
            if (simpleInteractable != null)
            {
                Debug.Log($"Destroying XRSimpleInteractable from {rootObject.name}");
                Destroy(simpleInteractable);
            }
            
            var grabTransformer = rootObject.GetComponent<UnityEngine.XR.Interaction.Toolkit.Transformers.XRGeneralGrabTransformer>();
            if (grabTransformer != null)
            {
                Debug.Log($"Destroying XRGeneralGrabTransformer from {rootObject.name}");
                Destroy(grabTransformer);
            }
            
            // Also remove Rigidbody if you want a completely static object
            var rb = rootObject.GetComponent<Rigidbody>();
            if (rb != null)
            {
                Debug.Log($"Destroying Rigidbody from {rootObject.name}");
                Destroy(rb);
            }
            
            // Show hitbox glow feedback
            if (detectedCollider != null)
            {
                StartCoroutine(ShowHitboxGlow(detectedCollider, Color.red));
            }
            
            Debug.Log($"{rootObject.name} is now a regular object with Transform and Collider only.");
            return;
        }

        // Otherwise, convert to spawned object
        ConvertToSpawnedObject(rootObject, detectedCollider);
    }

    private void ConvertToSpawnedObject(GameObject detectedObject, Collider detectedCollider)
    {
        // Ensure the object has a Rigidbody
        Rigidbody rb = detectedObject.GetComponent<Rigidbody>();
        if (rb == null)
        {
            rb = detectedObject.AddComponent<Rigidbody>();
        }

        // Ensure the object has XRGrabInteractable and XRSimpleInteractable
        if (detectedObject.GetComponent<UnityEngine.XR.Interaction.Toolkit.Interactables.XRGrabInteractable>() == null)
        {
            detectedObject.AddComponent<UnityEngine.XR.Interaction.Toolkit.Interactables.XRGrabInteractable>();
        }

        if (detectedObject.GetComponent<UnityEngine.XR.Interaction.Toolkit.Interactables.XRSimpleInteractable>() == null)
        {
            detectedObject.AddComponent<UnityEngine.XR.Interaction.Toolkit.Interactables.XRSimpleInteractable>();
        }

        // Ensure the object has XRGeneralGrabTransformer for two-handed scaling
        if (detectedObject.GetComponent<UnityEngine.XR.Interaction.Toolkit.Transformers.XRGeneralGrabTransformer>() == null)
        {
            detectedObject.AddComponent<UnityEngine.XR.Interaction.Toolkit.Transformers.XRGeneralGrabTransformer>();
        }

        // Finally, add the SpawnedObject component
        SpawnedObject spawnedComponent = detectedObject.AddComponent<SpawnedObject>();

        // Set to kinematic after conversion
        spawnedComponent.SetKinematic(true);

        // Show hitbox glow feedback
        if (detectedCollider != null)
        {
            StartCoroutine(ShowHitboxGlow(detectedCollider, convertedColor));
        }

        Debug.Log($"Converted {detectedObject.name} into a spawned object with all necessary components.");
    }

    private System.Collections.IEnumerator ShowHitboxGlow(Collider collider, Color glowColor)
    {
        // Temporarily create a glowing hitbox
        GameObject glowObj = new GameObject("TempHitboxGlow");
        LineRenderer glowRenderer = glowObj.AddComponent<LineRenderer>();
        glowRenderer.startWidth = 0.03f;
        glowRenderer.endWidth = 0.03f;
        glowRenderer.loop = true;
        glowRenderer.useWorldSpace = true;
        glowRenderer.material = new Material(Shader.Find("Sprites/Default"));
        glowRenderer.positionCount = 16;

        Bounds bounds = collider.bounds;
        Vector3 min = bounds.min;
        Vector3 max = bounds.max;

        Vector3[] corners = new Vector3[16];
        corners[0] = new Vector3(min.x, min.y, min.z);
        corners[1] = new Vector3(max.x, min.y, min.z);
        corners[2] = new Vector3(max.x, min.y, max.z);
        corners[3] = new Vector3(min.x, min.y, max.z);
        corners[4] = new Vector3(min.x, min.y, min.z);
        corners[5] = new Vector3(min.x, max.y, min.z);
        corners[6] = new Vector3(max.x, max.y, min.z);
        corners[7] = new Vector3(max.x, min.y, min.z);
        corners[8] = new Vector3(max.x, max.y, min.z);
        corners[9] = new Vector3(max.x, max.y, max.z);
        corners[10] = new Vector3(max.x, min.y, max.z);
        corners[11] = new Vector3(max.x, max.y, max.z);
        corners[12] = new Vector3(min.x, max.y, max.z);
        corners[13] = new Vector3(min.x, min.y, max.z);
        corners[14] = new Vector3(min.x, max.y, max.z);
        corners[15] = new Vector3(min.x, max.y, min.z);

        glowRenderer.SetPositions(corners);

        float elapsed = 0f;
        while (elapsed < glowDuration)
        {
            elapsed += Time.deltaTime;
            float progress = elapsed / glowDuration;
            
            // Fade from full intensity to transparent
            float alpha = Mathf.Lerp(1f, 0f, progress);
            Color currentColor = glowColor;
            currentColor.a = alpha;
            
            glowRenderer.startColor = currentColor;
            glowRenderer.endColor = currentColor;

            yield return null;
        }

        Destroy(glowObj);
    }
}
