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
    
    private Rigidbody rb;
    private Collider[] colliders;
    private XRSimpleInteractable simpleInteractable;
    private XRGrabInteractable grabInteractable;
    private XRGeneralGrabTransformer grabTransformer;
    private UnityEngine.XR.Interaction.Toolkit.XRInteractionManager manager;

    void Awake()
    {
        // // Ensure only one interactable exists
        // var simple = GetComponent<UnityEngine.XR.Interaction.Toolkit.Interactables.XRSimpleInteractable>();
        // if (simple != null)
        // {
        //     DestroyImmediate(simple);
        //     Debug.Log($"{name}: Removed redundant XRSimpleInteractable");
        // }

        rb = GetComponent<Rigidbody>();
        if (rb == null)
            rb = gameObject.AddComponent<Rigidbody>(); // ensures assignment immediately

        colliders = GetComponentsInChildren<Collider>();

        grabInteractable = GetComponent<XRGrabInteractable>() ?? gameObject.AddComponent<XRGrabInteractable>();
        simpleInteractable = GetComponent<XRSimpleInteractable>() ?? gameObject.AddComponent<XRSimpleInteractable>();

        var manager = grabInteractable.interactionManager ?? FindFirstObjectByType<UnityEngine.XR.Interaction.Toolkit.XRInteractionManager>();

        if (manager != null)
        {
            grabInteractable.interactionManager = manager;
            simpleInteractable.interactionManager = manager;
        }
        
        ApplySettings();
    }

    // -------------------- TOGGLE METHODS --------------------
    public void ToggleGravity()
    {
        SetGravity(!gravityEnabled);
    }

    public void ToggleCollision()
    {
        SetCollision(!collisionEnabled);
    }

    public void ToggleKinematic()
    {
        SetKinematic(!kinematic);
    }

    public void ToggleGrabbable()
    {
        SetGrabbable(!grabEnabled);
    }

    // -------------------- SET METHODS --------------------
    public void SetGravity(bool enabled)
    {
        gravityEnabled = enabled;
        if (rb != null)
            rb.useGravity = enabled;
        Debug.Log($"{gameObject.name}: Gravity = {enabled}");
    }

    public void SetCollision(bool enabled)
    {
        collisionEnabled = enabled;
        foreach (Collider col in colliders)
            if (col != null)
                col.enabled = enabled;

        Debug.Log($"{gameObject.name}: Collision = {enabled}");
    }

    public void SetKinematic(bool enabled)
    {
        kinematic = enabled;

        if (enabled && rb == null)
        {
            rb = gameObject.AddComponent<Rigidbody>();
        }

        rb.isKinematic = enabled;

        Debug.Log($"{gameObject.name}: Kinematic = {enabled}");
    }

    void SetGrabbable(bool enabled)
    {
        grabEnabled = enabled;

        // Ensure references exist
        if (rb == null)
            rb = GetComponent<Rigidbody>() ?? gameObject.AddComponent<Rigidbody>();

        if (grabInteractable == null)
            grabInteractable = GetComponent<XRGrabInteractable>() ?? gameObject.AddComponent<XRGrabInteractable>();

        if (simpleInteractable == null)
            simpleInteractable = GetComponent<XRSimpleInteractable>() ?? gameObject.AddComponent<XRSimpleInteractable>();

        if (manager == null)
            manager = FindFirstObjectByType<UnityEngine.XR.Interaction.Toolkit.XRInteractionManager>();

        // Unregister both safely
        if (manager != null)
        {
            manager.UnregisterInteractable((IXRInteractable)grabInteractable);
            manager.UnregisterInteractable((IXRInteractable)simpleInteractable);
        }

        // Apply physics & activation
        if (enabled)
        {
            // Enable grabbing
            grabInteractable.enabled = true;
            simpleInteractable.enabled = false;

            rb.isKinematic = false;
            rb.useGravity = true;

            foreach (var c in colliders)
                c.enabled = true;

            // Re-register after enabling
            if (manager != null)
                manager.RegisterInteractable((IXRInteractable)grabInteractable);
        }
        else
        {
            // Disable grabbing, fallback to simple interactable
            grabInteractable.enabled = false;
            simpleInteractable.enabled = true;

            rb.isKinematic = true;
            rb.useGravity = false;

            foreach (var c in colliders)
                c.enabled = true; // keep hover detection possible

            if (manager != null)
                manager.RegisterInteractable((IXRInteractable)simpleInteractable);
        }

        Debug.Log($"{name}: Grabbable = {grabEnabled} (grab={grabInteractable.enabled}, simple={simpleInteractable.enabled})");
    }

    // -------------------- OTHER --------------------
    public void DestroyObject()
    {
        Debug.Log($"Destroying {gameObject.name}");
        Destroy(gameObject);
    }

    void ApplySettings()
    {
        SetGravity(gravityEnabled);
        SetCollision(collisionEnabled);
        SetKinematic(kinematic);
        SetGrabbable(grabEnabled);
    }
}
