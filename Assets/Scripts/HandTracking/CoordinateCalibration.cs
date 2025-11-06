using UnityEngine;

[System.Serializable]
public class CoordinateCalibration
{
    [Header("Position Scale")]
    [Tooltip("Scale factor for X, Y, Z axes")]
    public Vector3 positionScale = new Vector3(2f, 2f, 2f);
    
    [Header("Position Offset")]
    [Tooltip("Center the hand in MediaPipe space (usually -0.5 to center X and Y)")]
    public Vector3 mediaPipeOffset = new Vector3(-0.5f, -0.5f, 0f);
    
    [Tooltip("Offset in Unity world space (move hands to comfortable viewing position)")]
    public Vector3 worldOffset = new Vector3(0f, 1.2f, 0.5f);
    
    [Header("Axis Flipping")]
    [Tooltip("Flip Y axis (MediaPipe Y is inverted)")]
    public bool flipY = true;
    
    [Tooltip("Flip Z axis (depth)")]
    public bool flipZ = true;
    
    [Header("Smoothing")]
    [Tooltip("Position smoothing (0 = instant, 1 = very smooth but laggy)")]
    [Range(0f, 0.9f)]
    public float positionSmoothing = 0.3f;
    
    public Vector3 ConvertMediaPipeToUnity(float x, float y, float z)
    {
        // Step 1: Apply MediaPipe offset (center coordinates)
        x += mediaPipeOffset.x;
        y += mediaPipeOffset.y;
        z += mediaPipeOffset.z;
        
        // Step 2: Flip axes if needed
        if (flipY) y = -y;
        if (flipZ) z = -z;
        
        // Step 3: Scale to Unity space
        Vector3 scaled = new Vector3(
            x * positionScale.x,
            y * positionScale.y,
            z * positionScale.z
        );
        
        // Step 4: Apply world offset
        return scaled + worldOffset;
    }
}
