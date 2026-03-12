using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.Interactors.Visuals;

public class TaskManager : MonoBehaviour
{
	public enum HandUsed
	{
		Unknown = 0,
		Left = 1,
		Right = 2,
	}

	[Header("XR")]
	[Tooltip("Used to classify interactor as Left.")]
	public Transform leftControllerRoot;

	[Tooltip("Used to classify interactor as Right.")]
	public Transform rightControllerRoot;

	[Header("Interactors")]
	[Tooltip("Interactor on the left hand (XRDirectInteractor / XRRayInteractor / NearFarInteractor, etc).")]
	public XRBaseInteractor leftInteractor;

	[Tooltip("Interactor on the right hand (XRDirectInteractor / XRRayInteractor / NearFarInteractor, etc).")]
	public XRBaseInteractor rightInteractor;

	[Tooltip("If true, tries to auto-bind interactors from left/rightControllerRoot on Awake/Validate.")]
	public bool autoBindInteractors = true;

	[Header("Interactor Gating (Motor Task)")]
	[Tooltip("If true, disables far casting while a recording session is active (armed -> endpoint captured / stop / clear).")]
	public bool disableFarInteractorsDuringRecording = true;

	[Tooltip("If empty and autoBindInteractors is true, XRRayInteractors under leftControllerRoot will be used.")]
	public Behaviour[] leftFarInteractorsToDisable;

	[Tooltip("If empty and autoBindInteractors is true, XRRayInteractors under rightControllerRoot will be used.")]
	public Behaviour[] rightFarInteractorsToDisable;

	[Header("Recording")]
	[Tooltip("Trajectory samples per second while holding the starting object.")]
	public float samplingRate = 30f;

	[Tooltip("Minimum surface normal alignment with +Y to count as 'underneath' (0..1). 0.5 ~= 60 degrees.")]
	[Range(0f, 1f)]
	public float minUpNormal = 0.5f;

	[Header("State (read-only)")]
	[SerializeField] private bool isArmed;
	[SerializeField] private bool isRecordingTrajectory;
	[SerializeField] private bool isWaitingForEndpoint;

	[Header("Results")]
	[SerializeField] private GameObject startingObject;
	[SerializeField] private Vector3 startingObjectInitialPosition;
	[SerializeField] private Quaternion startingObjectInitialRotation = Quaternion.identity;
	[SerializeField] private HandUsed controllerUsed = HandUsed.Unknown;
	[SerializeField] private Vector3 endPoint;
	[SerializeField] private bool hasEndPoint;
	[SerializeField] private bool _startingRbWasKinematic;
	[SerializeField] private bool _startingRbUsedGravity;
	[SerializeField] private RigidbodyConstraints _startingRbConstraints;

	[SerializeField] private List<TrajectorySample> trajectory = new();

	[Serializable]
	public struct TrajectorySample
	{
		public float t;
		public Vector3 position;
		public Quaternion rotation;

		public TrajectorySample(float t, Vector3 position, Quaternion rotation)
		{
			this.t = t;
			this.position = position;
			this.rotation = rotation;
		}
	}

	public bool IsArmed => isArmed;
	public bool IsRecordingTrajectory => isRecordingTrajectory;
	public bool IsWaitingForEndpoint => isWaitingForEndpoint;

	public GameObject StartingObject => startingObject;
	public Vector3 StartingObjectInitialPosition => startingObjectInitialPosition;
	public Quaternion StartingObjectInitialRotation => startingObjectInitialRotation;
	public HandUsed ControllerUsed => controllerUsed;
	public bool HasEndPoint => hasEndPoint;
	public Vector3 EndPoint => endPoint;
	public IReadOnlyList<TrajectorySample> Trajectory => trajectory;

	public event Action<GameObject, HandUsed> StartingObjectGrabbed;
	public event Action TrajectoryRecordingStarted;
	public event Action TrajectoryRecordingStopped;
	public event Action<Vector3> EndPointCaptured;

	/// <summary>
	/// Binds a concrete starting object reference (e.g., after loading a saved task recording).
	/// This does not change the saved start pose/end point/trajectory samples.
	/// </summary>
	public void BindStartingObject(GameObject obj)
	{
		if (obj == null)
			return;

		startingObject = obj;
		var spawned = startingObject.GetComponent<SpawnedObject>();
		if (spawned != null)
			spawned.enabled = true;
	}

	private Coroutine _samplingRoutine;
	private Transform _trackedControllerPose;
	private float _grabStartTime;
	private UnityEngine.XR.Interaction.Toolkit.Interactables.IXRSelectInteractable _startingInteractable;
	private Rigidbody _startingRigidbody;
	private ReleasedCollisionReporter _collisionReporter;
	private bool _interactorsHooked;
	private readonly List<XRBaseInteractor> _selectEventInteractors = new();
	private bool _isTaskMode;
	private PlayManager _playManager;
	private bool _farOverrideActive;
	private readonly List<DisabledBehaviourState> _farDisabledCache = new();
	private readonly List<NearFarFarCastingState> _nearFarCastingCache = new();

	private struct NearFarFarCastingState
	{
		public NearFarInteractor interactor;
		public bool wasFarCastingEnabled;
		public NearFarFarCastingState(NearFarInteractor interactor, bool wasFarCastingEnabled)
		{
			this.interactor = interactor;
			this.wasFarCastingEnabled = wasFarCastingEnabled;
		}
	}

	private struct DisabledBehaviourState
	{
		public Behaviour behaviour;
		public bool wasEnabled;
		public DisabledBehaviourState(Behaviour behaviour, bool wasEnabled)
		{
			this.behaviour = behaviour;
			this.wasEnabled = wasEnabled;
		}
	}

	void Awake()
	{
		TryAutoBindInteractors();
		_playManager = FindFirstObjectByType<PlayManager>();
	}

	void OnValidate()
	{
		if (!Application.isPlaying)
			TryAutoBindInteractors();
	}

	void OnEnable()
	{
		if (ModeManager.Instance != null)
			ModeManager.Instance.OnModeChanged += HandleModeChanged;

		ApplyMode(ModeManager.Instance != null ? ModeManager.Instance.CurrentMode : Mode.Task);
	}

	void OnDisable()
	{
		if (ModeManager.Instance != null)
			ModeManager.Instance.OnModeChanged -= HandleModeChanged;

		HookInteractors(false);
		RestoreFarInteractors();
		StopAllCoroutines();
		_samplingRoutine = null;
	}

	private void HandleModeChanged(Mode newMode)
	{
		ApplyMode(newMode);
	}

	private void ApplyMode(Mode newMode)
	{
		bool wasTaskMode = _isTaskMode;
		_isTaskMode = newMode == Mode.Task;
		TryAutoBindInteractors();
		HookInteractors(_isTaskMode);

		if (!_isTaskMode)
		{
			// Outside Task mode we stop listening/recording, but we don't auto-clear the scene.
			isArmed = false;
			isWaitingForEndpoint = false;
			RestoreFarInteractors();
			StopTrajectorySampling();

			// If TaskMode temporarily altered rigidbody state (e.g., recording), make sure the
			// SpawnedObject's persisted settings are re-applied when leaving TaskMode.
			if (wasTaskMode)
				ReapplyStartingObjectSavedSettings();
		}
	}

	private void ReapplyStartingObjectSavedSettings()
	{
		if (startingObject == null)
			return;

		var spawned = startingObject.GetComponentInParent<SpawnedObject>() ?? startingObject.GetComponent<SpawnedObject>();
		if (spawned != null)
			spawned.ApplySavedSettings();
	}

	private void TryAutoBindInteractors()
	{
		if (!autoBindInteractors)
			return;

		if (leftInteractor == null && leftControllerRoot != null)
			leftInteractor = PickDefaultInteractor(leftControllerRoot);

		if (rightInteractor == null && rightControllerRoot != null)
			rightInteractor = PickDefaultInteractor(rightControllerRoot);
	}

	private static XRBaseInteractor PickDefaultInteractor(Transform controllerRoot)
	{
		if (controllerRoot == null)
			return null;

		// Prefer direct/near interactions for "grab" semantics, then fall back.
		var direct = controllerRoot.GetComponentInChildren<XRDirectInteractor>(true);
		if (direct != null) return direct;
		var nearFar = controllerRoot.GetComponentInChildren<NearFarInteractor>(true);
		if (nearFar != null) return nearFar;
		var ray = controllerRoot.GetComponentInChildren<XRRayInteractor>(true);
		if (ray != null) return ray;
		return controllerRoot.GetComponentInChildren<XRBaseInteractor>(true);
	}

	private void HookInteractors(bool hook)
	{
		if (hook == _interactorsHooked)
			return;

		_interactorsHooked = hook;

		if (hook)
		{
			if (leftInteractor == null || rightInteractor == null)
				TryAutoBindInteractors();

			// Hook ALL interactors under each controller root so recording works regardless of
			// which specific interactor (Direct / Ray / NearFar) actually selects the starting object.
			var candidates = new HashSet<XRBaseInteractor>();
			if (leftControllerRoot != null)
				candidates.UnionWith(leftControllerRoot.GetComponentsInChildren<XRBaseInteractor>(true));
			if (rightControllerRoot != null)
				candidates.UnionWith(rightControllerRoot.GetComponentsInChildren<XRBaseInteractor>(true));
			if (leftInteractor != null)
				candidates.Add(leftInteractor);
			if (rightInteractor != null)
				candidates.Add(rightInteractor);

			_selectEventInteractors.Clear();
			foreach (var interactor in candidates)
			{
				if (interactor == null)
					continue;
				interactor.selectEntered.AddListener(HandleAnySelectEntered);
				interactor.selectExited.AddListener(HandleAnySelectExited);
				_selectEventInteractors.Add(interactor);
			}

			if (_selectEventInteractors.Count == 0)
				Debug.LogWarning("[RECORDING] TaskManager: No interactors found to hook; recording will not start from grabs.");
			else
				Debug.Log($"[RECORDING] TaskManager: Hooked {_selectEventInteractors.Count} interactors for grab detection.");
		}
		else
		{
			foreach (var interactor in _selectEventInteractors)
			{
				if (interactor == null)
					continue;
				interactor.selectEntered.RemoveListener(HandleAnySelectEntered);
				interactor.selectExited.RemoveListener(HandleAnySelectExited);
			}
			_selectEventInteractors.Clear();
		}
	}

	// -------- Public API --------
	// Kept as snake_case for your external "start_rec" API requirement.
	public void start_rec() => StartRec();
	public void stop_rec() => StopRec();
	public void clear() => ClearTask();

	public void StartRec()
	{
		if (ModeManager.Instance != null && !ModeManager.Instance.IsTaskMode)
		{
			Debug.LogWarning("[RECORDING] TaskManager: StartRec ignored because ModeManager is not in Task mode.");
			return;
		}

		ResetRecordingState();
		isArmed = true;
		DisableFarInteractors();

		Debug.Log("[RECORDING] TaskManager: Armed for next grab (starting object).");
	}

	/// <summary>
	/// Loads a previously saved task recording (start pose, endpoint, and trajectory samples).
	/// This does not require a startingObject reference; it is used for visualization/replay.
	/// </summary>
	public void LoadSavedRecording(Vector3 savedStartPosition, Quaternion savedStartRotation, Vector3 savedEndPoint, IReadOnlyList<TrajectorySample> savedTrajectory)
	{
		ResetRecordingState();
		isArmed = false;
		isWaitingForEndpoint = false;
		isRecordingTrajectory = false;

		startingObject = null;
		startingObjectInitialPosition = savedStartPosition;
		startingObjectInitialRotation = savedStartRotation;
		controllerUsed = HandUsed.Unknown;

		hasEndPoint = true;
		endPoint = savedEndPoint;

		trajectory.Clear();
		if (savedTrajectory != null)
		{
			for (int i = 0; i < savedTrajectory.Count; i++)
				trajectory.Add(savedTrajectory[i]);
		}
	}

	public void StopRec()
	{
		isArmed = false;
		RestoreFarInteractors();
		StopTrajectorySampling();
		isWaitingForEndpoint = false;
		Debug.Log("TaskManager: StopRec called.");
	}

	public void ResetRecordingState()
	{
		RestoreFarInteractors();
		StopTrajectorySampling();

		isArmed = false;
		isWaitingForEndpoint = false;

		startingObject = null;
		startingObjectInitialPosition = Vector3.zero;
		startingObjectInitialRotation = Quaternion.identity;
		controllerUsed = HandUsed.Unknown;
		_startingInteractable = null;
		_trackedControllerPose = null;
		_grabStartTime = 0f;

		hasEndPoint = false;
		endPoint = Vector3.zero;
		trajectory.Clear();

		if (_collisionReporter != null)
			Destroy(_collisionReporter);
		_collisionReporter = null;
		_startingRigidbody = null;
		_startingRbWasKinematic = false;
		_startingRbUsedGravity = false;
		_startingRbConstraints = RigidbodyConstraints.None;
	}

	/// <summary>
	/// Cancels the current/last task recording and attempts to restore the starting object
	/// to its initial pose + re-enable interactions.
	/// </summary>
	public void ClearTask()
	{
		RestoreFarInteractors();
		StopTrajectorySampling();
		isArmed = false;
		isWaitingForEndpoint = false;

		RestoreStartingObjectToInitialPose(reenableInteractions: true);

		if (_collisionReporter != null)
			Destroy(_collisionReporter);
		_collisionReporter = null;

		hasEndPoint = false;
		endPoint = Vector3.zero;
		trajectory.Clear();
		controllerUsed = HandUsed.Unknown;
		_startingInteractable = null;
		_trackedControllerPose = null;
		_grabStartTime = 0f;

		Debug.Log("[RECORDING] TaskManager: Cleared task/recording state.");
	}

	// -------- XR Event Handling --------
	private void HandleAnySelectEntered(SelectEnterEventArgs args)
	{
		var interactorComponent = args != null ? (args.interactorObject as Component) : null;
		var hand = DetermineHand(interactorComponent);
		HandleSelectEntered(args, hand, interactorComponent as XRBaseInteractor);
	}

	private void HandleAnySelectExited(SelectExitEventArgs args) => HandleSelectExited(args);

	private void HandleSelectEntered(SelectEnterEventArgs args, HandUsed hand, XRBaseInteractor interactor)
	{
		if (!isArmed)
			return;

		if (startingObject != null)
			return; // already have our starting object

		if (args == null || args.interactableObject == null)
			return;

		var interactableComponent = args.interactableObject as Component;
		if (interactableComponent == null)
			return;

		// Store all data before consuming arm
		_startingInteractable = args.interactableObject;
		startingObject = interactableComponent.gameObject;

		// Prefer the SpawnedObject root as the canonical starting object.
		var spawnedRoot = startingObject.GetComponentInParent<SpawnedObject>();
		if (spawnedRoot != null)
			startingObject = spawnedRoot.gameObject;

		// Immediately share the starting object with PlayManager so PlayMode always has a concrete reference.
		if (_playManager == null)
			_playManager = FindFirstObjectByType<PlayManager>();
		if (_playManager != null)
			_playManager.SetStartingObject(startingObject);

		startingObjectInitialPosition = startingObject.transform.position;
		startingObjectInitialRotation = startingObject.transform.rotation;

		// Remember starting object ID for PlayMode rebinding.
		if (spawnedRoot != null && !string.IsNullOrWhiteSpace(spawnedRoot.persistentId))
		{
			PlayerPrefs.SetString("LastTaskStartingObjectPersistentId", spawnedRoot.persistentId);
			PlayerPrefs.Save();
		}

		controllerUsed = hand;
		_trackedControllerPose = DetermineControllerPose(interactor);

		if (_trackedControllerPose == null)
		{
			Debug.LogError($"[RECORDING] TaskManager: Failed to determine controller pose for {hand}. Aborting grab.");
			startingObject = null;
			_startingInteractable = null;
			return;
		}

		_startingRigidbody = startingObject.GetComponentInParent<Rigidbody>() ?? startingObject.GetComponent<Rigidbody>();
		if (_startingRigidbody != null)
		{
			_startingRbWasKinematic = _startingRigidbody.isKinematic;
			_startingRbUsedGravity = _startingRigidbody.useGravity;
			_startingRbConstraints = _startingRigidbody.constraints;
		}

		isArmed = false; // consume the arm
		StartingObjectGrabbed?.Invoke(startingObject, controllerUsed);

		StartTrajectorySampling();

		Debug.Log($"[RECORDING] TaskManager: Starting object grabbed: '{startingObject.name}' hand={controllerUsed} pose={(_trackedControllerPose != null ? _trackedControllerPose.name : "NULL")}");
	}

	private void HandleSelectExited(SelectExitEventArgs args)
	{
		if (_startingInteractable == null)
		{
			if (args != null && args.interactableObject != null)
				Debug.LogWarning($"[RECORDING] TaskManager: Release detected but no starting interactable set. Ignoring release of {args.interactableObject}");
			return;
		}

		if (args == null || args.interactableObject == null)
		{
			Debug.LogWarning("[RECORDING] TaskManager: Release event has null args or interactableObject.");
			return;
		}

		if (!ReferenceEquals(args.interactableObject, _startingInteractable))
		{
			Debug.LogWarning("[RECORDING] TaskManager: Release from different object, ignoring.");
			return; // not our starting object
		}

		// Keep sampling trajectory after release; recording completes on first "underneath" collision.
		ArmEndpointCapture();

		Debug.Log("[RECORDING] TaskManager: Starting object released, waiting for endpoint collision.");
	}

	private HandUsed DetermineHand(Component interactorComponent)
	{
		if (interactorComponent == null)
			return HandUsed.Unknown;

		Transform t = interactorComponent.transform;

		if (leftControllerRoot != null && t.IsChildOf(leftControllerRoot))
			return HandUsed.Left;

		if (rightControllerRoot != null && t.IsChildOf(rightControllerRoot))
			return HandUsed.Right;

		// Best-effort fallback (name-based)
		string n = t.name.ToLowerInvariant();
		if (n.Contains("left")) return HandUsed.Left;
		if (n.Contains("right")) return HandUsed.Right;
		return HandUsed.Unknown;
	}

	private Transform DetermineControllerPose(XRBaseInteractor baseInteractor)
	{
		if (baseInteractor == null)
			return null;

		if (baseInteractor.attachTransform != null)
			return baseInteractor.attachTransform;

		return baseInteractor.transform;
	}

	// -------- Trajectory Sampling --------
	private void StartTrajectorySampling()
	{
		if (_trackedControllerPose == null)
		{
			Debug.LogWarning("TaskManager: No controller pose to track; trajectory will be empty.");
			return;
		}

		if (samplingRate <= 0f)
		{
			Debug.LogWarning("TaskManager: samplingRate must be > 0.");
			return;
		}

		if (_samplingRoutine != null)
			StopCoroutine(_samplingRoutine);

		DisableFarInteractors();

		trajectory.Clear();
		_grabStartTime = Time.time;
		isRecordingTrajectory = true;
		TrajectoryRecordingStarted?.Invoke();
		_samplingRoutine = StartCoroutine(SampleTrajectoryRoutine());

		Debug.Log($"[RECORDING] Started trajectory sampling at {samplingRate} Hz.");
	}

	private void StopTrajectorySampling()
	{
		if (_samplingRoutine != null)
			StopCoroutine(_samplingRoutine);
		_samplingRoutine = null;

		if (isRecordingTrajectory)
		{

			Debug.Log($"[RECORDING] Stopped trajectory sampling. Total samples: {trajectory.Count}");
			isRecordingTrajectory = false;
			TrajectoryRecordingStopped?.Invoke();
		}
	}

	private IEnumerator SampleTrajectoryRoutine()
	{
		float interval = 1f / samplingRate;
		var wait = new WaitForSeconds(interval);

		while (isRecordingTrajectory && _trackedControllerPose != null)
		{
			float t = Time.time - _grabStartTime;
			trajectory.Add(new TrajectorySample(t, _trackedControllerPose.position, _trackedControllerPose.rotation));
			yield return wait;
		}
	}

	// -------- Endpoint Capture --------
	private void ArmEndpointCapture()
	{
		if (startingObject == null)
			return;

		isWaitingForEndpoint = true;

		// Ensure we have a Rigidbody reference (used to check downward velocity)
		if (_startingRigidbody == null)
			_startingRigidbody = startingObject.GetComponentInParent<Rigidbody>() ?? startingObject.GetComponent<Rigidbody>();

		_collisionReporter = startingObject.GetComponent<ReleasedCollisionReporter>();
		if (_collisionReporter == null)
			_collisionReporter = startingObject.AddComponent<ReleasedCollisionReporter>();

		_collisionReporter.Arm(this, minUpNormal);
	}

	internal void NotifyEndpointFromCollision(Vector3 point)
	{
		if (!isWaitingForEndpoint)
			return;

		hasEndPoint = true;
		endPoint = point;
		isWaitingForEndpoint = false;
		StopTrajectorySampling();
		// Task completed: keep the captured endpoint, but restore the starting object to its original pose/properties.
		RestoreStartingObjectToInitialPose(reenableInteractions: true);
		EndPointCaptured?.Invoke(point);
		RestoreFarInteractors();

		Debug.Log($"[RECORDING] TaskManager: End point captured at {point}. Total trajectory samples: {trajectory.Count}");
	}

	private void DisableFarInteractors()
	{
		if (!disableFarInteractorsDuringRecording)
			return;

		if (_farOverrideActive)
			return;

		if (autoBindInteractors)
			TryAutoBindFarInteractors();

		_farOverrideActive = true;
		_farDisabledCache.Clear();
		_nearFarCastingCache.Clear();

		DisableBehaviours(leftFarInteractorsToDisable);
		DisableBehaviours(rightFarInteractorsToDisable);

		// Near/Far Interactor setups: disable far casting (keep near casting enabled) on both controllers.
		DisableFarCastingUnderRoot(GetLeftSearchRoot());
		DisableFarCastingUnderRoot(GetRightSearchRoot());
	}

	private void RestoreFarInteractors()
	{
		if (!_farOverrideActive)
			return;

		foreach (var state in _farDisabledCache)
		{
			if (state.behaviour != null)
				state.behaviour.enabled = state.wasEnabled;
		}

		foreach (var state in _nearFarCastingCache)
		{
			if (state.interactor != null)
				state.interactor.enableFarCasting = state.wasFarCastingEnabled;
		}

		_farDisabledCache.Clear();
		_nearFarCastingCache.Clear();
		_farOverrideActive = false;
	}

	private void DisableNearFarFarCasting(NearFarInteractor nearFar)
	{
		if (nearFar == null)
			return;

		_nearFarCastingCache.Add(new NearFarFarCastingState(nearFar, nearFar.enableFarCasting));
		nearFar.enableFarCasting = false;
	}

	private Transform GetLeftSearchRoot()
	{
		if (leftControllerRoot != null)
			return leftControllerRoot;
		return leftInteractor != null ? leftInteractor.transform : null;
	}

	private Transform GetRightSearchRoot()
	{
		if (rightControllerRoot != null)
			return rightControllerRoot;
		return rightInteractor != null ? rightInteractor.transform : null;
	}

	private void DisableFarCastingUnderRoot(Transform root)
	{
		if (root == null)
			return;

		var nearFars = root.GetComponentsInChildren<NearFarInteractor>(true);
		foreach (var nf in nearFars)
			DisableNearFarFarCasting(nf);
	}

	private void DisableBehaviours(Behaviour[] behaviours)
	{
		if (behaviours == null)
			return;

		foreach (var b in behaviours)
		{
			if (b == null)
				continue;

			// Never disable the interactors we rely on for select events.
			if (ReferenceEquals(b, leftInteractor) || ReferenceEquals(b, rightInteractor))
				continue;

			_farDisabledCache.Add(new DisabledBehaviourState(b, b.enabled));
			b.enabled = false;
		}
	}

	private void TryAutoBindFarInteractors()
	{
		// If the user assigned them explicitly, respect that.
		if (leftFarInteractorsToDisable != null && leftFarInteractorsToDisable.Length > 0 &&
			rightFarInteractorsToDisable != null && rightFarInteractorsToDisable.Length > 0)
			return;

		// XRRayInteractor is the typical "far" interactor in XRI.
		// With NearFarInteractor setups, the far interaction is often visualized by XRInteractorLineVisual/ReticleVisual.
		Transform leftSearch = leftControllerRoot != null ? leftControllerRoot : (leftInteractor != null ? leftInteractor.transform : null);
		Transform rightSearch = rightControllerRoot != null ? rightControllerRoot : (rightInteractor != null ? rightInteractor.transform : null);

		if (leftSearch != null && (leftFarInteractorsToDisable == null || leftFarInteractorsToDisable.Length == 0))
		{
			var list = new List<Behaviour>();
			list.AddRange(leftSearch.GetComponentsInChildren<XRRayInteractor>(true));
			list.AddRange(leftSearch.GetComponentsInChildren<XRInteractorLineVisual>(true));
			list.AddRange(leftSearch.GetComponentsInChildren<XRInteractorReticleVisual>(true));
			leftFarInteractorsToDisable = list.ToArray();
		}

		if (rightSearch != null && (rightFarInteractorsToDisable == null || rightFarInteractorsToDisable.Length == 0))
		{
			var list = new List<Behaviour>();
			list.AddRange(rightSearch.GetComponentsInChildren<XRRayInteractor>(true));
			list.AddRange(rightSearch.GetComponentsInChildren<XRInteractorLineVisual>(true));
			list.AddRange(rightSearch.GetComponentsInChildren<XRInteractorReticleVisual>(true));
			rightFarInteractorsToDisable = list.ToArray();
		}
	}




	private void RestoreStartingObjectToInitialPose(bool reenableInteractions)
	{
		if (startingObject == null)
			return;

		startingObject.transform.position = startingObjectInitialPosition;
		startingObject.transform.rotation = startingObjectInitialRotation;

		var rb = _startingRigidbody != null
			? _startingRigidbody
			: (startingObject.GetComponentInParent<Rigidbody>() ?? startingObject.GetComponent<Rigidbody>());

		if (rb != null)
		{
			rb.linearVelocity = Vector3.zero;
			rb.angularVelocity = Vector3.zero;
			rb.isKinematic = _startingRbWasKinematic;
			rb.useGravity = _startingRbUsedGravity;
			rb.constraints = _startingRbConstraints;
		}

		if (reenableInteractions)
		{
			foreach (var xrInteractable in startingObject.GetComponentsInChildren<XRBaseInteractable>(true))
			{
				if (xrInteractable != null)
					xrInteractable.enabled = true;
			}
		}
	}
}

