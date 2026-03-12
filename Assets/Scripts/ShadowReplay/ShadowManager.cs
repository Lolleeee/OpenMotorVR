using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

public class ShadowManager : MonoBehaviour
{
	[Header("Sources (Live XR)")]
	[Tooltip("Live HMD (Camera) transform. If null, will use Camera.main if available.")]
	public Transform hmdSource;
	public Transform leftControllerSource;
	public Transform rightControllerSource;

	[Header("Live Model Sources (Optional)")]
	[Tooltip("Optional: a transform that contains ONLY the left controller visual model (renderers). If empty, ShadowManager will try to auto-find a renderer under leftControllerSource.")]
	public Transform leftControllerModelSource;
	[Tooltip("Optional: a transform that contains ONLY the right controller visual model (renderers). If empty, ShadowManager will try to auto-find a renderer under rightControllerSource.")]
	public Transform rightControllerModelSource;
	[Tooltip("Optional: a transform that contains the LEFT NearFarInteractor visual children you want to replicate (e.g., line visual/reticle objects).")]
	public Transform leftNearInteractorModelSource;
	[Tooltip("Optional: a transform that contains the RIGHT NearFarInteractor visual children you want to replicate (e.g., line visual/reticle objects).")]
	public Transform rightNearInteractorModelSource;

	[Header("Optional Extra Sources")]
	[Tooltip("Any additional objects to record/replay (e.g., the grabbed starting object).")]
	public List<Transform> extraSources = new();

	[Header("Ghost Rig")]
	[Tooltip("If not assigned, will be created at runtime.")]
	public Transform ghostRoot;
	public Transform ghostHmd;
	public Transform ghostLeftController;
	public Transform ghostRightController;
	public bool autoCreateGhostRig = true;

	[Header("Ghost Visuals")]
	[Tooltip("If enabled, clones controller visuals into the ghost rig (renderers only).")]
	public bool cloneControllerModelsFromSources = true;
	[Tooltip("If enabled, clones visuals for extraSources into the ghost rig.")]
	public bool cloneExtraSourceVisuals = true;
	[Tooltip("If true, hides live controller renderers while replaying so only the ghost is visible.")]
	public bool hideLiveControllerModelsDuringReplay = true;
	[Tooltip("If true, hides live extraSources renderers while replaying.")]
	public bool hideLiveExtraSourceModelsDuringReplay = false;
	[Tooltip("If no models can be cloned, show small fallback spheres.")]
	public bool showFallbackMarkersWhenNoModels = true;

	[Header("Recording")]
	[Tooltip("Record at fixed Hz (recommended). If 0, records every frame.")]
	public float recordingRateHz = 60f;
	public bool recordInWorldSpace = true;

	[Header("Persistence")]
	public bool saveReplayToCsv = false;
	public string outputFolderName = "Tracking";
	public string outputFilePrefix = "shadow_replay";

	[Header("Replay")]
	public float replaySpeed = 1f;
	public bool loopReplay = false;
	public bool showGhostWhileReplaying = true;
	[Tooltip("Applies recorded scale to extraSources ghosts. Enable if your task objects are resized during recording.")]
	public bool replayExtraScale = true;
	[Tooltip("Applies recorded HMD position to the ghost HMD.")]
	public bool replayHmdPosition = true;
	[Tooltip("Applies recorded HMD rotation to the ghost HMD. Leave OFF if you want to freely look around while watching the replay.")]
	public bool replayHmdRotation = false;
	[Tooltip("Anchor replay so the first recorded HMD position starts at the current live HMD position.")]
	public bool anchorReplayToCurrentViewer = false;
	[Tooltip("When anchoring, also rotate the replay around the Y axis to match your current facing direction at replay start. Helps if controllers are parented under the camera.")]
	public bool anchorReplayYawToViewer = true;
	[Tooltip("Optional: translate a player rig root during replay (rotation untouched). Useful if you want your viewpoint to follow the recorded translation while still looking around.")]
	public bool translatePlayerRigDuringReplay = false;
	[Tooltip("The transform to translate when translatePlayerRigDuringReplay is enabled (e.g., XROrigin root).")]
	public Transform playerRigTranslationTarget;
	[Tooltip("If true, teleports the player rig so the live HMD starts at the recorded start HMD position when replay begins.")]
	public bool teleportViewerToRecordedStartOnReplay = true;

	[Header("State (read-only)")]
	[SerializeField] private bool isRecording;
	[SerializeField] private bool isReplaying;
	[SerializeField] private int recordedFrameCount;

	private readonly List<Frame> _frames = new();
	private readonly List<GhostExtra> _ghostExtras = new();
	private Coroutine _recordRoutine;
	private Coroutine _replayRoutine;
	private float _recordStartTime;

	private Vector3 _replayAnchorOrigin;
	private Quaternion _replayYawOffset;
	private Vector3 _replayRecordedStartHmdPos;
	private Vector3 _playerRigStartPos;
	private readonly List<Renderer> _hiddenLiveRenderers = new();

	private bool _hasScheduledAnchorSnapshot;
	private Vector3 _scheduledAnchorHmdPos;
	private Quaternion _scheduledAnchorHmdRot;
	private Vector3 _scheduledPlayerRigPos;
	private bool _disableAnchoringThisReplay;

	[Serializable]
	private struct Pose
	{
		public Vector3 pos;
		public Quaternion rot;
		public Vector3 scale;
		public Pose(Vector3 pos, Quaternion rot, Vector3 scale)
		{
			this.pos = pos;
			this.rot = rot;
			this.scale = scale;
		}

		public static Pose Identity => new Pose(Vector3.zero, Quaternion.identity, Vector3.one);
	}

	[Serializable]
	private struct Frame
	{
		public float t;
		public Pose hmd;
		public Pose left;
		public Pose right;
		public List<Pose> extras;
	}

	[Serializable]
	private class GhostExtra
	{
		public Transform source;
		public Transform ghost;
		public Transform visualRoot;
	}

	void Awake()
	{
		TryAutoBindSources();
		EnsureGhostRig();
		SetGhostActive(false);
	}

	public void TryAutoBindSources()
	{
		if (hmdSource == null && Camera.main != null)
			hmdSource = Camera.main.transform;
	}

	public void EnsureExtraSource(Transform t)
	{
		if (t == null)
			return;
		if (extraSources == null)
			extraSources = new List<Transform>();
		if (!extraSources.Contains(t))
			extraSources.Add(t);
		EnsureGhostExtras();
	}

	public void BeginRecording()
	{
		TryAutoBindSources();
		EnsureGhostRig();

		_frames.Clear();
		_recordStartTime = Time.time;
		isRecording = true;
		recordedFrameCount = 0;

		if (_recordRoutine != null)
			StopCoroutine(_recordRoutine);
		_recordRoutine = StartCoroutine(RecordRoutine());
	}

	public void StopRecording()
	{
		isRecording = false;
		if (_recordRoutine != null)
			StopCoroutine(_recordRoutine);
		_recordRoutine = null;
		recordedFrameCount = _frames.Count;

		if (saveReplayToCsv)
			SaveToCsv();
	}

	public void ClearRecording()
	{
		_frames.Clear();
		recordedFrameCount = 0;
	}

	public void ReplayLast(float delaySeconds = 0f)
	{
		if (_frames.Count == 0)
		{
			Debug.LogWarning("ShadowManager: No frames recorded; cannot replay.");
			return;
		}

		TryAutoBindSources();
		SnapshotReplayAnchor();

		if (_replayRoutine != null)
			StopCoroutine(_replayRoutine);
		_replayRoutine = StartCoroutine(ReplayRoutine(delaySeconds));
	}

	public void StopReplay()
	{
		isReplaying = false;
		if (_replayRoutine != null)
			StopCoroutine(_replayRoutine);
		_replayRoutine = null;
		EndHideLiveModels();
		SetGhostActive(false);
	}

	private IEnumerator RecordRoutine()
	{
		float interval = recordingRateHz > 0f ? (1f / recordingRateHz) : 0f;
		WaitForSeconds wait = interval > 0f ? new WaitForSeconds(interval) : null;

		while (isRecording)
		{
			CaptureFrame();
			if (wait != null)
				yield return wait;
			else
				yield return null;
		}
	}

	private void CaptureFrame()
	{
		float t = Time.time - _recordStartTime;

		var frame = new Frame
		{
			t = t,
			hmd = ReadPose(hmdSource),
			left = ReadPose(leftControllerSource),
			right = ReadPose(rightControllerSource),
			extras = new List<Pose>(extraSources != null ? extraSources.Count : 0)
		};

		if (extraSources != null)
		{
			for (int i = 0; i < extraSources.Count; i++)
				frame.extras.Add(ReadPose(extraSources[i]));
		}

		_frames.Add(frame);
		recordedFrameCount = _frames.Count;
	}

	private Pose ReadPose(Transform t)
	{
		if (t == null)
			return Pose.Identity;

		Vector3 scale = recordInWorldSpace ? t.lossyScale : t.localScale;

		return recordInWorldSpace
			? new Pose(t.position, t.rotation, scale)
			: new Pose(t.localPosition, t.localRotation, scale);
	}

	private IEnumerator ReplayRoutine(float delaySeconds)
	{
		isReplaying = true;
		SetGhostActive(showGhostWhileReplaying);
		BeginHideLiveModels();
		_disableAnchoringThisReplay = false;

		if (delaySeconds > 0f)
			yield return new WaitForSeconds(delaySeconds);

		TryTeleportViewerToRecordedStart();
		InitializeReplayAnchors();

		float speed = Mathf.Max(0.01f, replaySpeed);

		do
		{
			float replayStart = Time.time;
			for (int i = 0; i < _frames.Count && isReplaying; i++)
			{
				ApplyFrame(_frames[i]);

				float nextT = (i + 1 < _frames.Count) ? _frames[i + 1].t : _frames[i].t;
				float dt = Mathf.Max(0f, nextT - _frames[i].t);
				float scaled = dt / speed;
				if (scaled <= 0f)
				{
					yield return null;
					continue;
				}
				// We use realtime-ish waiting so replay doesn't depend on frame rate.
				float until = Time.time + scaled;
				while (Time.time < until && isReplaying)
					yield return null;
			}

			// Avoid tight loop if speed is absurd.
			if (Time.time - replayStart < 0.01f)
				yield return null;

		} while (loopReplay && isReplaying);

		isReplaying = false;
		EndHideLiveModels();
		_disableAnchoringThisReplay = false;
		SetGhostActive(false);
	}

	private void ApplyFrame(Frame f)
	{
		if (ghostHmd != null)
			WritePoseSelective(ghostHmd, f.hmd, applyPosition: replayHmdPosition, applyRotation: replayHmdRotation);
		WritePoseSelective(ghostLeftController, f.left, applyPosition: true, applyRotation: true);
		WritePoseSelective(ghostRightController, f.right, applyPosition: true, applyRotation: true);

		if (translatePlayerRigDuringReplay && playerRigTranslationTarget != null && recordInWorldSpace)
		{
			Vector3 delta = f.hmd.pos - _replayRecordedStartHmdPos;
			playerRigTranslationTarget.position = _playerRigStartPos + (_replayYawOffset * delta);
		}

		if (f.extras != null && f.extras.Count > 0)
		{
			EnsureGhostExtras();
			for (int i = 0; i < f.extras.Count && i < _ghostExtras.Count; i++)
				WritePoseSelective(_ghostExtras[i].ghost, f.extras[i], applyPosition: true, applyRotation: true, applyScale: replayExtraScale);
		}
	}

	private void WritePoseSelective(Transform t, Pose p, bool applyPosition, bool applyRotation, bool applyScale = false)
	{
		if (t == null)
			return;

		if (applyScale)
		{
			if (recordInWorldSpace)
			{
				var parent = t.parent;
				t.localScale = parent != null ? ComputeRelativeScale(parent.lossyScale, p.scale) : p.scale;
			}
			else
			{
				t.localScale = p.scale;
			}
		}

		if (recordInWorldSpace)
		{
			if (applyPosition)
				t.position = GetAnchoredWorldPosition(p.pos);
			if (applyRotation)
				t.rotation = GetAnchoredWorldRotation(p.rot);
		}
		else
		{
			if (applyPosition)
				t.localPosition = p.pos;
			if (applyRotation)
				t.localRotation = p.rot;
		}
	}

	private void InitializeReplayAnchors()
	{
		_replayAnchorOrigin = Vector3.zero;
		_replayYawOffset = Quaternion.identity;
		_playerRigStartPos = playerRigTranslationTarget != null ? playerRigTranslationTarget.position : Vector3.zero;
		_replayRecordedStartHmdPos = _frames.Count > 0 ? _frames[0].hmd.pos : Vector3.zero;

		if (!recordInWorldSpace)
			return;
		if (_disableAnchoringThisReplay)
			return;
		if (!anchorReplayToCurrentViewer)
			return;
		if (_frames.Count == 0)
			return;

		// IMPORTANT: Use the anchor snapshot taken when ReplayLast was called.
		// This prevents the replay from shifting if the user moves during the delay.
		Vector3 anchorPos;
		Quaternion anchorRot;
		if (_hasScheduledAnchorSnapshot)
		{
			anchorPos = _scheduledAnchorHmdPos;
			anchorRot = _scheduledAnchorHmdRot;
			if (playerRigTranslationTarget != null)
				_playerRigStartPos = _scheduledPlayerRigPos;
		}
		else
		{
			if (hmdSource == null)
				return;
			anchorPos = hmdSource.position;
			anchorRot = hmdSource.rotation;
		}

		_replayAnchorOrigin = anchorPos;
		if (anchorReplayYawToViewer)
		{
			var recForward = _frames[0].hmd.rot * Vector3.forward;
			var curForward = anchorRot * Vector3.forward;
			recForward.y = 0f;
			curForward.y = 0f;
			if (recForward.sqrMagnitude > 1e-6f && curForward.sqrMagnitude > 1e-6f)
				_replayYawOffset = Quaternion.FromToRotation(recForward.normalized, curForward.normalized);
			else
				_replayYawOffset = Quaternion.identity;
		}
	}

	private void SnapshotReplayAnchor()
	{
		_hasScheduledAnchorSnapshot = false;
		if (!recordInWorldSpace)
			return;
		if (!anchorReplayToCurrentViewer)
			return;
		if (hmdSource == null)
			return;

		_scheduledAnchorHmdPos = hmdSource.position;
		_scheduledAnchorHmdRot = hmdSource.rotation;
		_scheduledPlayerRigPos = playerRigTranslationTarget != null ? playerRigTranslationTarget.position : Vector3.zero;
		_hasScheduledAnchorSnapshot = true;
	}

	private Vector3 GetAnchoredWorldPosition(Vector3 recordedWorldPos)
	{
		if (!recordInWorldSpace)
			return recordedWorldPos;
		if (_disableAnchoringThisReplay)
			return recordedWorldPos;
		if (!anchorReplayToCurrentViewer || _frames.Count == 0)
			return recordedWorldPos;

		Vector3 delta = recordedWorldPos - _replayRecordedStartHmdPos;
		return _replayAnchorOrigin + (_replayYawOffset * delta);
	}

	private Quaternion GetAnchoredWorldRotation(Quaternion recordedWorldRot)
	{
		if (!recordInWorldSpace)
			return recordedWorldRot;
		if (_disableAnchoringThisReplay)
			return recordedWorldRot;
		if (!anchorReplayToCurrentViewer)
			return recordedWorldRot;
		return _replayYawOffset * recordedWorldRot;
	}

	private void TryTeleportViewerToRecordedStart()
	{
		if (!teleportViewerToRecordedStartOnReplay)
			return;
		if (!recordInWorldSpace)
			return;
		if (_frames.Count == 0)
			return;
		if (hmdSource == null)
			return;

		var rig = GetRigTranslationTarget();
		if (rig == null)
		{
			Debug.LogWarning("ShadowManager: teleportViewerToRecordedStartOnReplay is enabled, but no rig target found. Assign playerRigTranslationTarget.");
			return;
		}

		Vector3 desiredHmdPos = _frames[0].hmd.pos;
		Vector3 delta = desiredHmdPos - hmdSource.position;
		rig.position += delta;

		// When we teleport the viewer to the recorded start, we want world-space replay (no anchoring).
		_disableAnchoringThisReplay = true;

		// If we translate the rig during replay, start from this teleported position.
		if (translatePlayerRigDuringReplay)
			_playerRigStartPos = rig.position;
	}

	private Transform GetRigTranslationTarget()
	{
		if (playerRigTranslationTarget != null)
			return playerRigTranslationTarget;
		if (hmdSource == null)
			return null;

		// Try to find an XROrigin/XRRig in the parent chain without hard references.
		for (var t = hmdSource; t != null; t = t.parent)
		{
			if (t.GetComponent("Unity.XR.CoreUtils.XROrigin") != null)
				return t;
			if (t.GetComponent("UnityEngine.XR.Interaction.Toolkit.XRRig") != null)
				return t;
		}

		// Fallback: move the camera's immediate parent.
		return hmdSource.parent;
	}

	private void EnsureGhostRig()
	{
		if (!autoCreateGhostRig)
			return;

		if (ghostRoot == null)
		{
			var rootGo = GameObject.Find("ShadowGhostRig");
			if (rootGo == null)
				rootGo = new GameObject("ShadowGhostRig");
			ghostRoot = rootGo.transform;
		}

		if (ghostHmd == null)
			ghostHmd = EnsureEmpty("GhostHMD", ghostRoot);
		if (ghostLeftController == null)
			ghostLeftController = EnsureEmpty("GhostLeftController", ghostRoot);
		if (ghostRightController == null)
			ghostRightController = EnsureEmpty("GhostRightController", ghostRoot);

		EnsureGhostControllerVisuals();
		if (showFallbackMarkersWhenNoModels)
			EnsureFallbackMarkersIfNeeded();

		EnsureGhostExtras();
	}

	private void EnsureGhostExtras()
	{
		if (extraSources == null)
			return;

		// Keep list sizes aligned.
		while (_ghostExtras.Count < extraSources.Count)
			_ghostExtras.Add(new GhostExtra());
		while (_ghostExtras.Count > extraSources.Count)
			_ghostExtras.RemoveAt(_ghostExtras.Count - 1);

		for (int i = 0; i < extraSources.Count; i++)
		{
			_ghostExtras[i].source = extraSources[i];
			if (_ghostExtras[i].ghost == null && ghostRoot != null)
			{
				_ghostExtras[i].ghost = EnsureEmpty($"GhostExtra_{i}", ghostRoot);
			}

			if (cloneExtraSourceVisuals && _ghostExtras[i].ghost != null && _ghostExtras[i].visualRoot == null)
			{
				var src = extraSources[i];
				var best = FindBestVisualRoot(src);
				if (best != null)
					_ghostExtras[i].visualRoot = CreateVisualClone(best, _ghostExtras[i].ghost, src, cloneName: "Model");
			}
		}
	}

	private static Transform EnsureEmpty(string name, Transform parent)
	{
		var existing = parent != null ? parent.Find(name) : null;
		if (existing != null)
			return existing;

		var go = new GameObject(name);
		if (parent != null)
			go.transform.SetParent(parent, worldPositionStays: true);
		return go.transform;
	}

	private void EnsureFallbackMarkersIfNeeded()
	{
		if (ghostRoot == null)
			return;

		// Intentionally no HMD fallback marker: it tends to be distracting (a sphere in front of the viewer).
		// If a previous version created one, remove it.
		if (ghostHmd != null)
		{
			var old = ghostHmd.Find("FallbackGhostHMD");
			if (old != null)
				Destroy(old.gameObject);
		}
		if (ghostLeftController != null && ghostLeftController.GetComponentInChildren<Renderer>(true) == null)
			EnsureMarkerSphere("FallbackGhostLeft", ghostLeftController, Color.green, 0.05f);
		if (ghostRightController != null && ghostRightController.GetComponentInChildren<Renderer>(true) == null)
			EnsureMarkerSphere("FallbackGhostRight", ghostRightController, Color.magenta, 0.05f);
	}

	private static void EnsureMarkerSphere(string name, Transform parent, Color color, float scale)
	{
		if (parent == null)
			return;
		if (parent.Find(name) != null)
			return;

		var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
		go.name = name;
		go.transform.SetParent(parent, worldPositionStays: false);
		go.transform.localPosition = Vector3.zero;
		go.transform.localRotation = Quaternion.identity;
		go.transform.localScale = Vector3.one * scale;

		var col = go.GetComponent<Collider>();
		if (col != null)
			col.enabled = false;

		var renderer = go.GetComponent<Renderer>();
		if (renderer != null)
		{
			var mat = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
			if (mat == null)
				mat = new Material(Shader.Find("Unlit/Color"));
			if (mat != null)
			{
				if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
				if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
				renderer.sharedMaterial = mat;
			}
		}
	}

	private void EnsureGhostControllerVisuals()
	{
		if (!cloneControllerModelsFromSources)
			return;
		if (ghostLeftController == null || ghostRightController == null)
			return;

		if (ghostLeftController.Find("Model") == null)
		{
			var src = leftControllerModelSource != null ? leftControllerModelSource : FindBestVisualRoot(leftControllerSource);
			if (src != null && leftControllerSource != null)
				CreateVisualClone(src, ghostLeftController, leftControllerSource, cloneName: "Model");
		}
		if (ghostRightController.Find("Model") == null)
		{
			var src = rightControllerModelSource != null ? rightControllerModelSource : FindBestVisualRoot(rightControllerSource);
			if (src != null && rightControllerSource != null)
				CreateVisualClone(src, ghostRightController, rightControllerSource, cloneName: "Model");
		}

		// Optional: replicate NearFarInteractor visuals (line visuals / reticle visuals) if provided.
		if (leftNearInteractorModelSource != null && ghostLeftController.Find("NearInteractor") == null && leftControllerSource != null)
			CreateVisualClone(leftNearInteractorModelSource, ghostLeftController, leftControllerSource, cloneName: "NearInteractor");
		if (rightNearInteractorModelSource != null && ghostRightController.Find("NearInteractor") == null && rightControllerSource != null)
			CreateVisualClone(rightNearInteractorModelSource, ghostRightController, rightControllerSource, cloneName: "NearInteractor");
	}

	private static Transform FindBestVisualRoot(Transform t)
	{
		if (t == null)
			return null;
		var renderer = t.GetComponentInChildren<Renderer>(true);
		return renderer != null ? renderer.transform : null;
	}

	private static Transform CreateVisualClone(Transform visualSourceRoot, Transform ghostParent, Transform relativeTo, string cloneName)
	{
		if (visualSourceRoot == null || ghostParent == null || relativeTo == null)
			return null;

		var cloneGo = Instantiate(visualSourceRoot.gameObject);
		cloneGo.name = string.IsNullOrWhiteSpace(cloneName) ? "Model" : cloneName;
		var clone = cloneGo.transform;
		clone.SetParent(ghostParent, worldPositionStays: false);

		clone.localPosition = relativeTo.InverseTransformPoint(visualSourceRoot.position);
		clone.localRotation = Quaternion.Inverse(relativeTo.rotation) * visualSourceRoot.rotation;
		clone.localScale = ComputeRelativeScale(relativeTo.lossyScale, visualSourceRoot.lossyScale);

		StripToVisualOnly(cloneGo);
		SetCollidersEnabled(cloneGo, enabled: false);
		return clone;
	}

	private static Vector3 ComputeRelativeScale(Vector3 parentLossyScale, Vector3 childLossyScale)
	{
		float sx = Mathf.Abs(parentLossyScale.x) > 1e-6f ? (childLossyScale.x / parentLossyScale.x) : 1f;
		float sy = Mathf.Abs(parentLossyScale.y) > 1e-6f ? (childLossyScale.y / parentLossyScale.y) : 1f;
		float sz = Mathf.Abs(parentLossyScale.z) > 1e-6f ? (childLossyScale.z / parentLossyScale.z) : 1f;
		return new Vector3(sx, sy, sz);
	}

	private static void StripToVisualOnly(GameObject root)
	{
		if (root == null)
			return;

		var components = root.GetComponentsInChildren<Component>(true);
		for (int i = 0; i < components.Length; i++)
		{
			var c = components[i];
			if (c == null)
				continue;
			if (c is Transform)
				continue;
			if (c is Renderer)
				continue;
			if (c is MeshFilter)
				continue;
			if (c is Animator)
				continue;
			Destroy(c);
		}
	}

	private static void SetCollidersEnabled(GameObject root, bool enabled)
	{
		if (root == null)
			return;
		var cols = root.GetComponentsInChildren<Collider>(true);
		for (int i = 0; i < cols.Length; i++)
			cols[i].enabled = enabled;
		var rbs = root.GetComponentsInChildren<Rigidbody>(true);
		for (int i = 0; i < rbs.Length; i++)
			Destroy(rbs[i]);
	}

	private void BeginHideLiveModels()
	{
		_hiddenLiveRenderers.Clear();

		if (hideLiveControllerModelsDuringReplay)
		{
			HideRenderersUnder(leftControllerModelSource != null ? leftControllerModelSource : FindBestVisualRoot(leftControllerSource));
			HideRenderersUnder(rightControllerModelSource != null ? rightControllerModelSource : FindBestVisualRoot(rightControllerSource));
		}

		if (hideLiveExtraSourceModelsDuringReplay && extraSources != null)
		{
			for (int i = 0; i < extraSources.Count; i++)
				HideRenderersUnder(FindBestVisualRoot(extraSources[i]));
		}
	}

	private void EndHideLiveModels()
	{
		for (int i = 0; i < _hiddenLiveRenderers.Count; i++)
		{
			if (_hiddenLiveRenderers[i] != null)
				_hiddenLiveRenderers[i].enabled = true;
		}
		_hiddenLiveRenderers.Clear();
	}

	private void HideRenderersUnder(Transform root)
	{
		if (root == null)
			return;
		var renderers = root.GetComponentsInChildren<Renderer>(true);
		for (int i = 0; i < renderers.Length; i++)
		{
			var r = renderers[i];
			if (r == null)
				continue;
			if (!r.enabled)
				continue;
			r.enabled = false;
			_hiddenLiveRenderers.Add(r);
		}
	}

	private void SetGhostActive(bool active)
	{
		if (ghostRoot != null)
			ghostRoot.gameObject.SetActive(active);
	}

	private void SaveToCsv()
	{
		try
		{
			var ci = CultureInfo.InvariantCulture;
			string folder = Path.Combine(Application.persistentDataPath, outputFolderName);
			Directory.CreateDirectory(folder);
			string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", ci);
			string fileName = $"{outputFilePrefix}_{timestamp}.csv";
			string fullPath = Path.Combine(folder, fileName);

			// Unity/.NET profile compatibility: avoid the (string, bool, Encoding) overload.
			using (var fs = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.Read))
			using (var writer = new StreamWriter(fs, Encoding.UTF8))
			{
				writer.WriteLine("t,hmd_px,hmd_py,hmd_pz,hmd_qx,hmd_qy,hmd_qz,hmd_qw,left_px,left_py,left_pz,left_qx,left_qy,left_qz,left_qw,right_px,right_py,right_pz,right_qx,right_qy,right_qz,right_qw");
				for (int i = 0; i < _frames.Count; i++)
				{
					var f = _frames[i];
					writer.WriteLine(
						$"{f.t.ToString("F6", ci)}," +
						$"{f.hmd.pos.x.ToString("F6", ci)},{f.hmd.pos.y.ToString("F6", ci)},{f.hmd.pos.z.ToString("F6", ci)}," +
						$"{f.hmd.rot.x.ToString("F6", ci)},{f.hmd.rot.y.ToString("F6", ci)},{f.hmd.rot.z.ToString("F6", ci)},{f.hmd.rot.w.ToString("F6", ci)}," +
						$"{f.left.pos.x.ToString("F6", ci)},{f.left.pos.y.ToString("F6", ci)},{f.left.pos.z.ToString("F6", ci)}," +
						$"{f.left.rot.x.ToString("F6", ci)},{f.left.rot.y.ToString("F6", ci)},{f.left.rot.z.ToString("F6", ci)},{f.left.rot.w.ToString("F6", ci)}," +
						$"{f.right.pos.x.ToString("F6", ci)},{f.right.pos.y.ToString("F6", ci)},{f.right.pos.z.ToString("F6", ci)}," +
						$"{f.right.rot.x.ToString("F6", ci)},{f.right.rot.y.ToString("F6", ci)},{f.right.rot.z.ToString("F6", ci)},{f.right.rot.w.ToString("F6", ci)}");
				}
			}

			PlayerPrefs.SetString("LastShadowReplayCsvPath", fullPath);
			PlayerPrefs.Save();
			Debug.Log($"ShadowManager: Saved shadow replay CSV -> {fullPath}");
		}
		catch (Exception e)
		{
			Debug.LogError($"ShadowManager: Failed to save replay CSV: {e.Message}");
		}
	}
}

