using UnityEngine;
using UnityEngine.XR.Hands;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Diagnostics;       
using System.IO;   
using Newtonsoft.Json.Linq;
using Debug = UnityEngine.Debug;

public class MediaPipeHandReceiver : MonoBehaviour
{
    [Header("Python Script Settings")]
    [Tooltip("Path to Python executable")]
    public string pythonPath = ".venv/bin/python3";  
    
    [Tooltip("Path to MediaPipe hand tracking script")]
    public string scriptPath = "Assets/Scripts/HandTracking/stream.py";  
    private Process pythonProcess;
    
    [Header("Network Settings")]
    private UdpClient udpClient;
    private Thread receiveThread;
    private int port = 5065;
    private bool isRunning = false;
    
    private string latestHandData = "";
    private object dataLock = new object();
    
    [Header("Visualization Settings")]
    public float cylinderRadius = 0.005f;
    public Material boneMaterial;
    
    [Header("Calibration")]
    public CoordinateCalibration calibration = new CoordinateCalibration();
    
    // OpenXR joint mapping
    private static readonly Dictionary<int, XRHandJointID> mediaPipeToXRJoint = new Dictionary<int, XRHandJointID>()
    
    {
        {0, XRHandJointID.Wrist},
        {1, XRHandJointID.ThumbMetacarpal},
        {2, XRHandJointID.ThumbProximal},
        {3, XRHandJointID.ThumbDistal},
        {4, XRHandJointID.ThumbTip},
        {5, XRHandJointID.IndexProximal},
        {6, XRHandJointID.IndexIntermediate},
        {7, XRHandJointID.IndexDistal},
        {8, XRHandJointID.IndexTip},
        {9, XRHandJointID.MiddleProximal},
        {10, XRHandJointID.MiddleIntermediate},
        {11, XRHandJointID.MiddleDistal},
        {12, XRHandJointID.MiddleTip},
        {13, XRHandJointID.RingProximal},
        {14, XRHandJointID.RingIntermediate},
        {15, XRHandJointID.RingDistal},
        {16, XRHandJointID.RingTip},
        {17, XRHandJointID.LittleProximal},
        {18, XRHandJointID.LittleIntermediate},
        {19, XRHandJointID.LittleDistal},
        {20, XRHandJointID.LittleTip}
    };
    
    // Hand bone connections (MediaPipe indices)
    private static readonly int[][] handConnections = new int[][]
    {
        // Thumb
        new int[] {0, 1}, new int[] {1, 2}, new int[] {2, 3}, new int[] {3, 4},
        // Index
        new int[] {0, 5}, new int[] {5, 6}, new int[] {6, 7}, new int[] {7, 8},
        // Middle
        new int[] {0, 9}, new int[] {9, 10}, new int[] {10, 11}, new int[] {11, 12},
        // Ring
        new int[] {0, 13}, new int[] {13, 14}, new int[] {14, 15}, new int[] {15, 16},
        // Pinky
        new int[] {0, 17}, new int[] {17, 18}, new int[] {18, 19}, new int[] {19, 20},
        // Palm connections
        new int[] {5, 9}, new int[] {9, 13}, new int[] {13, 17}
    };
    
    private Dictionary<string, Dictionary<int, Vector3>> jointPositions = 
        new Dictionary<string, Dictionary<int, Vector3>>();
    
    private Dictionary<string, GameObject[]> handBones = 
        new Dictionary<string, GameObject[]>();

    private Dictionary<string, bool> handActive = new Dictionary<string, bool>();
    
    void Start()
    {
        // Auto-detect script path if not set
        if (string.IsNullOrEmpty(scriptPath))
        {
            scriptPath = Path.Combine(Application.dataPath, "..", "stream.py");
        }
        
        // Launch Python script
        StartPythonScript();
        
        // Wait a moment for Python to start
        System.Threading.Thread.Sleep(1000);
        
        Debug.Log("Starting UDP receiver on port " + port);
        
        // Initialize hands
        InitializeHandSkeleton("Left");
        InitializeHandSkeleton("Right");
        
        // Initialize as inactive
        handActive["Left"] = false;
        handActive["Right"] = false;
        
        isRunning = true;
        receiveThread = new Thread(ReceiveData);
        receiveThread.IsBackground = true;
        receiveThread.Start();
    }
    
    void InitializeHandSkeleton(string handLabel)
    {
        jointPositions[handLabel] = new Dictionary<int, Vector3>();
        
        GameObject handParent = new GameObject($"{handLabel}Hand");
        handParent.transform.parent = transform;
        
        // Create cylinders for each bone connection
        handBones[handLabel] = new GameObject[handConnections.Length];
        
        for (int i = 0; i < handConnections.Length; i++)
        {
            GameObject bone = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            bone.name = $"Bone_{handConnections[i][0]}_{handConnections[i][1]}";
            bone.transform.parent = handParent.transform;
            bone.transform.localScale = new Vector3(cylinderRadius, 0.05f, cylinderRadius);
            
            // Apply material and color
            Renderer renderer = bone.GetComponent<Renderer>();
            if (boneMaterial != null)
            {
                renderer.material = boneMaterial;
            }
            else
            {
                renderer.material.color = handLabel == "Left" ? Color.cyan : Color.magenta;
            }
            
            handBones[handLabel][i] = bone;
        }
        
        Debug.Log($"Initialized {handLabel} hand skeleton with {handConnections.Length} bones");
    }
    
    void ReceiveData()
    {
        udpClient = new UdpClient(port);
        IPEndPoint remoteEndPoint = new IPEndPoint(IPAddress.Any, port);
        
        while (isRunning)
        {
            try
            {
                byte[] data = udpClient.Receive(ref remoteEndPoint);
                string message = Encoding.UTF8.GetString(data);
                
                lock (dataLock)
                {
                    latestHandData = message;
                }
            }
            catch (Exception e)
            {
                Debug.LogError("UDP Receive Error: " + e.Message);
            }
        }
    }
    
    void Update()
    {
        string handData;
        lock (dataLock)
        {
            handData = latestHandData;
        }
        
        if (!string.IsNullOrEmpty(handData))
        {
            ProcessHandData(handData);
        }
        
        UpdateSkeletons();
    }
    
    void ProcessHandData(string jsonData)
    {
        try
        {
            JObject data = JObject.Parse(jsonData);
            JArray hands = (JArray)data["hands"];
            
            // Reset all hands to inactive
            handActive["Left"] = false;
            handActive["Right"] = false;
            
            // Process detected hands
            foreach (JObject hand in hands)
            {
                string label = hand["label"].ToString();
                JArray landmarks = (JArray)hand["landmarks"];
                
                UpdateHandJoints(label, landmarks);
                handActive[label] = true;  // Mark as active
            }
            
            // Hide inactive hands
            UpdateHandVisibility();
        }
        catch (Exception e)
        {
            Debug.LogError("JSON Parse Error: " + e.Message);
        }
    }

    void UpdateHandVisibility()
    {
        foreach (var handLabel in handBones.Keys)
        {
            bool isActive = handActive.ContainsKey(handLabel) && handActive[handLabel];
            
            // Show or hide all bones for this hand
            foreach (GameObject bone in handBones[handLabel])
            {
                bone.SetActive(isActive);
            }
        }
    }

    void UpdateSkeletons()
    {
        foreach (var handLabel in jointPositions.Keys)
        {
            // Only update if hand is active
            if (!handActive.ContainsKey(handLabel) || !handActive[handLabel])
                continue;
                
            if (!handBones.ContainsKey(handLabel)) continue;
            
            var joints = jointPositions[handLabel];
            
            // Update each bone cylinder
            for (int i = 0; i < handConnections.Length; i++)
            {
                int startIdx = handConnections[i][0];
                int endIdx = handConnections[i][1];
                
                if (joints.ContainsKey(startIdx) && joints.ContainsKey(endIdx))
                {
                    Vector3 startPos = joints[startIdx];
                    Vector3 endPos = joints[endIdx];
                    
                    Vector3 midPoint = (startPos + endPos) / 2f;
                    handBones[handLabel][i].transform.position = midPoint;
                    
                    Vector3 direction = endPos - startPos;
                    handBones[handLabel][i].transform.rotation = Quaternion.FromToRotation(Vector3.up, direction);
                    
                    float distance = Vector3.Distance(startPos, endPos);
                    Vector3 scale = handBones[handLabel][i].transform.localScale;
                    scale.y = distance / 2f;
                    handBones[handLabel][i].transform.localScale = scale;
                }
            }
        }
    }

    void UpdateHandJoints(string handLabel, JArray landmarks)
    {
        if (!jointPositions.ContainsKey(handLabel)) return;
        
        // Store all joint positions
        for (int i = 0; i < landmarks.Count && i <= 20; i++)
        {
            JObject landmark = (JObject)landmarks[i];
            float x = landmark["x"].Value<float>();
            float y = landmark["y"].Value<float>();
            float z = landmark["z"].Value<float>();
            
            Vector3 position = calibration.ConvertMediaPipeToUnity(x, y, z);
            jointPositions[handLabel][i] = position;
        }
    }
    
    void StartPythonScript()
    {
        try
        {
            ProcessStartInfo startInfo = new ProcessStartInfo();
            
            string absoluteScriptPath;
            if (Path.IsPathRooted(scriptPath))
            {
                absoluteScriptPath = scriptPath;
            }
            else
            {
                absoluteScriptPath = Path.Combine(Application.dataPath, "..", scriptPath);
                absoluteScriptPath = Path.GetFullPath(absoluteScriptPath);
            }
            
            if (!File.Exists(absoluteScriptPath))
            {
                Debug.LogError($"Python script not found at: {absoluteScriptPath}");
                return;
            }
            
            string pythonExecutable;
            if (pythonPath == "python" || pythonPath == "python3")
            {
                pythonExecutable = pythonPath;
            }
            else if (Path.IsPathRooted(pythonPath))
            {
                pythonExecutable = pythonPath;
            }
            else
            {
                pythonExecutable = Path.Combine(Application.dataPath, "..", pythonPath);
                pythonExecutable = Path.GetFullPath(pythonExecutable);
                
                if (!File.Exists(pythonExecutable))
                {
                    Debug.LogError($"Python executable not found at: {pythonExecutable}");
                    return;
                }
            }
            
            startInfo.FileName = pythonExecutable;
            startInfo.Arguments = $"\"{absoluteScriptPath}\"";
            startInfo.WorkingDirectory = Path.GetDirectoryName(absoluteScriptPath);
            
            startInfo.UseShellExecute = false;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.CreateNoWindow = false;
            
            Debug.Log($"Python: {pythonExecutable}");
            Debug.Log($"Script: {absoluteScriptPath}");
            
            pythonProcess = new Process();
            pythonProcess.StartInfo = startInfo;
            
            pythonProcess.OutputDataReceived += (sender, args) => 
            {
                if (!string.IsNullOrEmpty(args.Data))
                    Debug.Log($"Python: {args.Data}");
            };
            pythonProcess.ErrorDataReceived += (sender, args) => 
            {
                if (!string.IsNullOrEmpty(args.Data))
                    Debug.LogError($"Python Error: {args.Data}");
            };
            
            pythonProcess.Start();
            pythonProcess.BeginOutputReadLine();
            pythonProcess.BeginErrorReadLine();
            
            Debug.Log("Python script started successfully");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Failed to start Python: {e.Message}");
        }
    }

    void OnApplicationQuit()
    {
        if (pythonProcess != null && !pythonProcess.HasExited)
        {
            pythonProcess.Kill();
            pythonProcess.Dispose();
            Debug.Log("Python script stopped");
        }
        
        isRunning = false;
        if (receiveThread != null)
        {
            receiveThread.Abort();
        }
        if (udpClient != null)
        {
            udpClient.Close();
        }
    }
}
