using UnityEngine;

/// <summary>
/// Central gate that enables/disables the BuildMode folder scripts based on ModeManager.
/// Attach this to a GameObject that is always active (recommended: the same object as ModeManager).
/// </summary>
[DefaultExecutionOrder(-1000)]
public class BuildModeScriptsDeactivator : MonoBehaviour
{
    private void OnEnable()
    {
        if (ModeManager.Instance != null)
        {
            ModeManager.Instance.OnModeChanged += HandleModeChanged;
            HandleModeChanged(ModeManager.Instance.CurrentMode);
        }
    }

    private void OnDisable()
    {
        if (ModeManager.Instance != null)
            ModeManager.Instance.OnModeChanged -= HandleModeChanged;
    }

    private void HandleModeChanged(Mode newMode)
    {
        bool enable = newMode == Mode.Build;

        SetEnabled<Hotbar>(enable);
        SetEnabled<ObjectInspector>(enable);
        SetEnabled<SpawnedObjectConverter>(enable);

        // These typically exist on spawned menu/hotbar instances; keeping them gated avoids any stray Update() work.
        SetEnabled<InspectorOption>(enable);
        SetEnabled<HotbarItem>(enable);

        // Marker component used by the save/load pipeline; safe to gate as requested.
        SetEnabled<ConvertedObject>(enable);
    }

    private static void SetEnabled<T>(bool enabled) where T : Behaviour
    {
        // includeInactive = true so we can re-enable components that were disabled earlier.
        var behaviours = Object.FindObjectsOfType<T>(true);
        for (int i = 0; i < behaviours.Length; i++)
        {
            var behaviour = behaviours[i];
            if (behaviour == null)
                continue;

            // Never disable this controller.
            if (behaviour is BuildModeScriptsDeactivator)
                continue;

            behaviour.enabled = enabled;
        }
    }
}
