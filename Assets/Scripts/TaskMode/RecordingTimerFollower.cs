using TMPro;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

public class RecordingTimerFollower : MonoBehaviour
{
    [Header("References")]
    public RightControllerTracker tracker;
    public Transform followTarget;

    [Tooltip("If assigned, used for orientation. If null and autoBindInteractorFromFollowTarget is true, auto-found from followTarget.")]
    public NearFarInteractor nearFarInteractor;

    [Tooltip("Auto-bind NearFarInteractor from followTarget's parent chain (recommended).")]
    public bool autoBindInteractorFromFollowTarget = true;

    [Header("Follow")]
    public Vector3 localOffset = new Vector3(0f, 0.08f, 0.12f);

    public bool orientPerpendicularToInteractor = true;
    public bool invertInteractorForward = true;
    public bool useWorldUp = true;

    [Header("Smoothing")]
    public float positionSmoothTime = 0.05f;
    public float rotationLerpSpeed = 25f;
    public bool snapOnShow = true;
    public float snapDistance = 0.5f;

    [Header("Facing (optional override)")]
    public bool faceCamera = false;
    public Camera cameraToFace;

    [Header("Display")]
    public bool showWhenNotRecording = false;

    private TMP_Text _text;
    private bool _visible;

    private Vector3 _posVel;
    private Vector3 _targetPos;
    private Quaternion _targetRot = Quaternion.identity;

    void Awake()
    {
        _text = GetComponentInChildren<TMP_Text>(true);
        if (cameraToFace == null) cameraToFace = Camera.main;

        EnsureInteractorBound();
        SetVisible(false);
    }

    void OnValidate()
    {
        // Helps keep bindings correct while editing
        if (autoBindInteractorFromFollowTarget)
            EnsureInteractorBound();
    }

    void EnsureInteractorBound()
    {
        if (!autoBindInteractorFromFollowTarget)
            return;

        if (followTarget == null)
            return;

        // Bind from the controller we're following (prevents picking the other hand's interactor)
        if (nearFarInteractor == null || !nearFarInteractor.transform.IsChildOf(followTarget.root))
        {
            nearFarInteractor = followTarget.GetComponentInParent<NearFarInteractor>();
        }

        // If still null, that's fine (we'll fall back to followTarget rotation)
    }

    void Update()
    {
        if (ModeManager.Instance != null && !ModeManager.Instance.IsTaskMode)
        {
            SetVisible(false);
            return;
        }

        if (tracker == null || followTarget == null || _text == null)
        {
            SetVisible(false);
            return;
        }

        EnsureInteractorBound();

        bool shouldShow = tracker.IsRecordingInProgress || showWhenNotRecording;
        SetVisible(shouldShow);
        if (!shouldShow) return;

        _targetPos = followTarget.TransformPoint(localOffset);
        _targetRot = ComputeTargetRotation();
    }

    void LateUpdate()
    {
        if (!_visible)
            return;

        float dt = Time.deltaTime;
        if (dt <= 0f)
            return;

        if (snapOnShow || Vector3.Distance(transform.position, _targetPos) > snapDistance)
        {
            transform.position = _targetPos;
            transform.rotation = _targetRot;
            snapOnShow = false;
            return;
        }

        transform.position = Vector3.SmoothDamp(transform.position, _targetPos, ref _posVel, positionSmoothTime);

        float t = 1f - Mathf.Exp(-rotationLerpSpeed * dt);
        transform.rotation = Quaternion.Slerp(transform.rotation, _targetRot, t);

        if (!tracker.IsTrackingActive && showWhenNotRecording)
            _text.text = "REC: idle";
        else if (tracker.IsPaused)
            _text.text = $"PAUSED  {tracker.ElapsedTimeSeconds:0.00}/{tracker.TrackingDurationSeconds:0.00}s";
        else
            _text.text = $"REC  {tracker.ElapsedTimeSeconds:0.00}/{tracker.TrackingDurationSeconds:0.00}s  ({tracker.SampleCount} samples)";
    }

    Quaternion ComputeTargetRotation()
    {
        if (faceCamera && cameraToFace != null)
        {
            Vector3 toCam = cameraToFace.transform.position - _targetPos;
            if (toCam.sqrMagnitude > 0.0001f)
                return Quaternion.LookRotation(-toCam.normalized, Vector3.up);

            return transform.rotation;
        }

        if (orientPerpendicularToInteractor && nearFarInteractor != null)
        {
            // Use attachTransform (controller pose) when available; it’s less ambiguous than interactor.transform
            Transform basis = nearFarInteractor.attachTransform != null ? nearFarInteractor.attachTransform : nearFarInteractor.transform;

            Vector3 dir = basis.forward;
            if (invertInteractorForward) dir = -dir;

            Vector3 up = useWorldUp ? Vector3.up : basis.up;

            if (dir.sqrMagnitude > 0.0001f)
                return Quaternion.LookRotation(dir.normalized, up);
        }

        return followTarget.rotation;
    }

    void SetVisible(bool visible)
    {
        if (_visible == visible)
            return;

        _visible = visible;

        if (_text != null)
            _text.enabled = visible;

        if (visible)
        {
            snapOnShow = true;
            _posVel = Vector3.zero;
        }
    }
}