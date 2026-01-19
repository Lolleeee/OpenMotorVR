using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Transformers;

public class SpawnedObject : MonoBehaviour
{
    [Header("Physics Settings")]
    public bool gravityEnabled = true;
    public bool collisionEnabled = true;
    public bool kinematic = false;
    
    [Header("Interaction Settings")]
    public bool grabEnabled = true;
    
    [Header("Two-Handed Scaling")]
    [Tooltip("Enable two-handed scaling on the grab transformer")]
    public bool enableTwoHandedScaling = true;
    [Tooltip("If set, this child transform will be used as the secondary attach point for two-handed interactions")]
    public Transform secondaryAttachTransform;
    [Tooltip("Allow the object to stay where grabbed instead of snapping to attach point")]
    public bool useDynamicAttach = true;

    [Header("Layer Configuration")]
    [Tooltip("Layer name for objects that collide physically (e.g. 'Interactable').")]
    public string layerCollision = "Interactable"; 
    
    [Tooltip("Layer name for objects that DO NOT collide with environment (e.g. 'InteractableNoCollision').")]
    public string layerNoCollision = "InteractableNoCollision";
    
    private Rigidbody rb;
    private Collider[] colliders;
    private XRSimpleInteractable simpleInteractable;
    private XRGrabInteractable grabInteractable;
    private UnityEngine.XR.Interaction.Toolkit.XRInteractionManager manager;
    private bool _isSpecialVolume;

    void Awake()
    {
        _isSpecialVolume = GetComponentInChildren<GoalVolume>() != null || GetComponentInChildren<StartingZone>() != null;

        rb = GetComponent<Rigidbody>();
        if (rb == null) rb = gameObject.AddComponent<Rigidbody>(); 

        if (_isSpecialVolume)
        {
            kinematic = true;
            gravityEnabled = false;
            collisionEnabled = false; // GoalVolumes default to ghost mode
            Debug.Log($"{name}: Identified as GoalVolume. Enforcing kinematic/ghost state.");
        }

        colliders = GetComponentsInChildren<Collider>();

        grabInteractable = GetComponent<XRGrabInteractable>() ?? gameObject.AddComponent<XRGrabInteractable>();
        simpleInteractable = GetComponent<XRSimpleInteractable>() ?? gameObject.AddComponent<XRSimpleInteractable>();

        var manager = grabInteractable.interactionManager ?? FindFirstObjectByType<UnityEngine.XR.Interaction.Toolkit.XRInteractionManager>();

        if (manager != null)
        {
            grabInteractable.interactionManager = manager;
            simpleInteractable.interactionManager = manager;
        }
        
        ConfigureTwoHandedScaling();
        ApplySettings();
    }

    // -------------------- TOGGLE METHODS --------------------
    public void ToggleGravity() { SetGravity(!gravityEnabled); }
    public void ToggleCollision() { SetCollision(!collisionEnabled); }
    public void ToggleKinematic() { SetKinematic(!kinematic); }
    public void ToggleGrabbable() { SetGrabbable(!grabEnabled); }

    // -------------------- SET METHODS --------------------
    public void SetGravity(bool enabled)
    {
        gravityEnabled = enabled;
        
        // Safety: If collision is disabled (ghost), we cannot use gravity or we fall forever.
        if (!collisionEnabled || _isSpecialVolume)
        {
            if (rb != null) rb.useGravity = false;
            return;
        }

        if (rb != null) rb.useGravity = enabled;
        Debug.Log($"{gameObject.name}: Gravity = {enabled}");
    }

    public void SetCollision(bool enabled)
    {
        collisionEnabled = enabled;

        // 1. Switch Layers
        string targetLayer = enabled ? layerCollision : layerNoCollision;
        int layerId = LayerMask.NameToLayer(targetLayer);

        bool hasTrigger = false;
        foreach (var c in GetComponentsInChildren<Collider>(true))
        {
            if (c != null && c.isTrigger)
            {
                hasTrigger = true;
                break;
            }
        }

        if (layerId > -1)
        {
            if (!hasTrigger)
            {
                SetLayerRecursive(gameObject, layerId);
            }
            else
            {
                gameObject.layer = layerId;
            }
        }
        else
        {
            Debug.LogWarning($"{name}: Layer '{targetLayer}' not found. Please add it in Tags & Layers.");
        }

        // 2. Manage Physics Safety
        if (!enabled)
        {
            if (rb != null) rb.useGravity = false;
        }
        else
        {
            if (rb != null) rb.useGravity = gravityEnabled;
        }

        Debug.Log($"{gameObject.name}: SetCollision({enabled}) -> Layer '{targetLayer}' (recursive={(!hasTrigger)})");
    }

    public void SetKinematic(bool enabled)
    {   
        kinematic = enabled;

        // EXCEPTION: GoalVolume must stay kinematic
        if (_isSpecialVolume)
        {
            if (rb != null) rb.isKinematic = true;
            return;
        }

        if (enabled && rb == null) rb = gameObject.AddComponent<Rigidbody>();
        if (rb != null) rb.isKinematic = enabled;

        Debug.Log($"{gameObject.name}: Kinematic = {enabled}");
    }

    public void SetGrabbable(bool enabled)
    {
        grabEnabled = enabled;

        if (rb == null) rb = GetComponent<Rigidbody>() ?? gameObject.AddComponent<Rigidbody>();
        if (grabInteractable == null) grabInteractable = GetComponent<XRGrabInteractable>() ?? gameObject.AddComponent<XRGrabInteractable>();
        if (simpleInteractable == null) simpleInteractable = GetComponent<XRSimpleInteractable>() ?? gameObject.AddComponent<XRSimpleInteractable>();
        if (manager == null) manager = FindFirstObjectByType<UnityEngine.XR.Interaction.Toolkit.XRInteractionManager>();

        if (manager != null)
        {
            manager.UnregisterInteractable((IXRInteractable)grabInteractable);
            manager.UnregisterInteractable((IXRInteractable)simpleInteractable);
        }

        if (enabled)
        {
            grabInteractable.enabled = true;
            simpleInteractable.enabled = false;

            // Physics Logic:
            // 1. GoalVolume -> Always Kinematic, No Gravity
            // 2. No Collision -> No Gravity
            bool forceNoGravity = _isSpecialVolume || !collisionEnabled;
            bool forceKinematic = _isSpecialVolume; 

            rb.isKinematic = forceKinematic ? true : kinematic;
            rb.useGravity = forceNoGravity ? false : true;

            foreach (var c in colliders) c.enabled = true;

            if (manager != null) manager.RegisterInteractable((IXRInteractable)grabInteractable);
        }
        else
        {
            grabInteractable.enabled = false;
            simpleInteractable.enabled = true;

            rb.isKinematic = true;
            rb.useGravity = false;

            foreach (var c in colliders) c.enabled = true;

            if (manager != null) manager.RegisterInteractable((IXRInteractable)simpleInteractable);
        }

        Debug.Log($"{name}: Grabbable = {grabEnabled}");
    }

    // Helper to set layers recursively, but skipping special layers (like Ignore Raycast used by GoalVolume sensors)
    void SetLayerRecursive(GameObject obj, int newLayer)
    {
        // Layer 2 is "Ignore Raycast". GoalVolume uses this for its internal sensor. Don't touch it.
        if (obj.layer == 2) return;

        obj.layer = newLayer;
        foreach (Transform child in obj.transform)
        {
            SetLayerRecursive(child.gameObject, newLayer);
        }
    }

    public void DestroyObject()
    {
        Debug.Log($"Destroying {gameObject.name}");
        Destroy(gameObject);
    }

    void ApplySettings()
    {   
        SetGrabbable(grabEnabled);
        SetGravity(gravityEnabled);
        SetCollision(collisionEnabled);
        SetKinematic(kinematic);
    }

    void ConfigureTwoHandedScaling()
    {
        if (!grabInteractable) return;

        // Set Select Mode to allow multiple interactors
        grabInteractable.selectMode = UnityEngine.XR.Interaction.Toolkit.Interactables.InteractableSelectMode.Multiple;

        // Enable Use Dynamic Attach
        if (useDynamicAttach)
        {
            grabInteractable.useDynamicAttach = true;
        }

        // Add or configure XR General Grab Transformer
        XRGeneralGrabTransformer grabTransformer = GetComponent<XRGeneralGrabTransformer>();
        if (grabTransformer == null)
        {
            grabTransformer = gameObject.AddComponent<XRGeneralGrabTransformer>();
        }

        // Enable two-handed scaling
        if (enableTwoHandedScaling)
        {
            grabTransformer.allowTwoHandedScaling = true; // allow scaling with two controllers
            grabTransformer.allowOneHandedScaling = false; // optional: disable one-hand scale
            Debug.Log($"{name}: Two-handed scaling enabled");
        }

        // Set secondary attach transform if provided
        if (secondaryAttachTransform != null)
        {
            grabInteractable.secondaryAttachTransform = secondaryAttachTransform;
            Debug.Log($"{name}: Secondary attach transform set to {secondaryAttachTransform.name}");
        }
    }
}
