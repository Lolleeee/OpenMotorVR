using UnityEngine;

/// <summary>
/// Marks objects that were originally spawned prefabs but have been converted to static objects.
/// This helps the save system know to recreate them from prefabs on load.
/// </summary>
public class ConvertedObject : MonoBehaviour
{
    [Tooltip("Original prefab name for recreation on load")]
    public string originalPrefabName;
    
    public void Initialize(string prefabName)
    {
        originalPrefabName = prefabName;
    }
}
