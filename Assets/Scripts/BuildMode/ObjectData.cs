using UnityEngine;

[System.Serializable]
public class ObjectData
{
    public string objectName;
    public bool isSpawned;
    
    // Transform
    public Vector3 position;
    public Quaternion rotation;
    public Vector3 scale;
    
    // SpawnedObject properties (only if isSpawned == true)
    public bool gravityEnabled;
    public bool collisionEnabled;
    public bool kinematic;
    public bool grabEnabled;
    public bool enableTwoHandedScaling;
    public bool useDynamicAttach;
    public string layerCollision;
    public string layerNoCollision;

    public ObjectData() { }

    public ObjectData(GameObject obj, SpawnedObject spawnedObj = null)
    {
        objectName = obj.name;
        isSpawned = spawnedObj != null;

        // Save transform
        position = obj.transform.position;
        rotation = obj.transform.rotation;
        scale = obj.transform.localScale;

        // Save spawned properties if applicable
        if (isSpawned && spawnedObj != null)
        {
            gravityEnabled = spawnedObj.gravityEnabled;
            collisionEnabled = spawnedObj.collisionEnabled;
            kinematic = spawnedObj.kinematic;
            grabEnabled = spawnedObj.grabEnabled;
            enableTwoHandedScaling = spawnedObj.enableTwoHandedScaling;
            useDynamicAttach = spawnedObj.useDynamicAttach;
            layerCollision = spawnedObj.layerCollision;
            layerNoCollision = spawnedObj.layerNoCollision;
        }
    }
}

[System.Serializable]
public class SceneData
{
    public ObjectData[] objects;
}
