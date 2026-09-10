using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace ARNav.Navigation
{
    /// <summary>
    /// Universal sensor-based AR fallback for devices without Google Play Services for AR.
    /// Uses WebCamTexture for passthrough video, Gyroscope for 3D orientation,
    /// and Step Detection (pedometer / accelerometer peak detection) for indoor dead reckoning.
    /// </summary>
    public class SensorARFallback : MonoBehaviour
    {
        [Header("Camera Video Fallback")]
        [SerializeField] private RawImage cameraBackgroundUI;
        private WebCamTexture _webCamTexture;

        public enum PhonePose { Holding, PocketOrSwinging }

        [Header("Movement Settings")]
        [SerializeField] private float stepLengthMeters = 0.68f; // Standard walking stride
        [SerializeField] private float stepThreshold = 1.15f;    // Base acceleration peak threshold
        
        [Header("State (Read Only)")]
        [SerializeField] private PhonePose _currentPose = PhonePose.Holding;

        // Sensor tracking state
        private bool _isActive = false;
        private Vector3 _virtualPosition = Vector3.zero;
        private float _lastStepTime = 0f;
        private Vector3 _smoothedHeading = Vector3.forward;

        // Adaptive step detection state
        private Queue<float> _accelMagHistory = new Queue<float>();
        private const int WINDOW_SIZE = 50;

        // ZUPT — Zero Velocity Update (Paper 3: IEEE Sensors 2022)
        // When the phone is completely still, stop ghost steps from vibrations.
        private float _stillnessTimer = 0f;
        private const float ZUPT_STILL_THRESHOLD = 0.04f;  // Low variance = phone is on a desk
        private const float ZUPT_STILL_SECONDS  = 0.8f;   // Must be still for this long to trigger ZUPT

        // 1D Kalman Filter state for heading angle (Paper 4: MDPI Sensors 2022)
        private float _kalmanHeadingAngle = 0f;
        private float _kalmanErrorCov      = 1f;
        private const float KALMAN_PROCESS_NOISE = 0.01f;
        private const float KALMAN_MEAS_NOISE    = 0.1f;

        // Stair detection accumulator (Paper 4: MDPI Sensors 2022)
        // Sustained vertical acceleration over several steps indicates going up/down stairs.
        private Queue<float> _verticalAccelHistory = new Queue<float>();
        private const int STAIR_WINDOW = 20;

        public bool IsActive => _isActive;
        public Vector3 VirtualPosition => _virtualPosition;

        public void ActivateFallback()
        {
            if (_isActive) return;
            _isActive = true;

            Debug.Log("[SensorARFallback] Activating Gyroscope + WebCam + Step Detection Fallback...");

            // 1. Enable Hardware Sensors
            Input.gyro.enabled = true;
            Input.compass.enabled = true;

            if (Camera.main != null)
            {
                _smoothedHeading = Vector3.ProjectOnPlane(Camera.main.transform.forward, Vector3.up).normalized;
                if (_smoothedHeading == Vector3.zero) _smoothedHeading = Vector3.forward;
            }

            // 2. Start WebCam Passthrough Video
            StartWebCamPassthrough();
        }

        private void StartWebCamPassthrough()
        {
            StartCoroutine(StartWebCamRoutine());
        }

        private IEnumerator StartWebCamRoutine()
        {
#if UNITY_ANDROID
            if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Camera))
            {
                UnityEngine.Android.Permission.RequestUserPermission(UnityEngine.Android.Permission.Camera);
                float waitTime = 5f;
                while (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Camera) && waitTime > 0f)
                {
                    waitTime -= 0.2f;
                    yield return new WaitForSeconds(0.2f);
                }
            }
#endif
            yield return new WaitForSeconds(0.1f);

            WebCamDevice[] devices = WebCamTexture.devices;
            if (devices == null || devices.Length == 0)
            {
                Debug.LogError("[SensorARFallback] No camera devices found on phone!");
                yield break;
            }

            string targetCam = devices[0].name;
            for (int i = 0; i < devices.Length; i++)
            {
                if (!devices[i].isFrontFacing)
                {
                    targetCam = devices[i].name;
                    break;
                }
            }

            _webCamTexture = new WebCamTexture(targetCam, 1920, 1080, 30);
            _webCamTexture.Play();

            EnsureBackgroundUI();
        }

        private void EnsureBackgroundUI()
        {
            if (cameraBackgroundUI == null)
            {
                Canvas canvas = FindAnyObjectByType<Canvas>();
                if (canvas == null) return;

                GameObject bgObj = new GameObject("SensorAR_VideoBackground");
                bgObj.transform.SetParent(canvas.transform, false);
                bgObj.transform.SetAsFirstSibling(); // Behind all UI buttons

                cameraBackgroundUI = bgObj.AddComponent<RawImage>();
            }

            if (_webCamTexture != null && cameraBackgroundUI != null)
            {
                cameraBackgroundUI.texture = _webCamTexture;
                cameraBackgroundUI.color = Color.white;
                AdjustVideoOrientation();
            }
        }

        private void AdjustVideoOrientation()
        {
            if (_webCamTexture == null || cameraBackgroundUI == null || _webCamTexture.width < 100) return;

            RectTransform rt = cameraBackgroundUI.rectTransform;
            
            // Center the RectTransform and set its size to the texture's resolution
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(_webCamTexture.width, _webCamTexture.height);
            rt.anchoredPosition = Vector2.zero;

            // 1. Rotate to match sensor hardware angle
            int angle = _webCamTexture.videoRotationAngle;
            rt.localEulerAngles = new Vector3(0, 0, -angle);

            // 2. Calculate scaling to cover the whole screen (Zoom to fill)
            float videoWidth = _webCamTexture.width;
            float videoHeight = _webCamTexture.height;
            
            // If rotated 90 or 270, the video's visual width and height are swapped
            bool isPortrait = angle == 90 || angle == 270;
            float visualVideoWidth = isPortrait ? videoHeight : videoWidth;
            float visualVideoHeight = isPortrait ? videoWidth : videoHeight;

            // Calculate ratios to fit the screen
            float scaleX = (float)Screen.width / visualVideoWidth;
            float scaleY = (float)Screen.height / visualVideoHeight;

            // To ensure it covers the whole screen without stretching, use the maximum of the two scales
            float scale = Mathf.Max(scaleX, scaleY);

            // 3. Fix inverted / flipped mirror axis
            float mirrorY = _webCamTexture.videoVerticallyMirrored ? -1.0f : 1.0f;

            rt.localScale = new Vector3(scale, scale * mirrorY, 1.0f);
        }

        private void Update()
        {
            if (!_isActive) return;

            // Keep orientation adjusted once video starts playing
            if (_webCamTexture != null && _webCamTexture.isPlaying && _webCamTexture.didUpdateThisFrame)
            {
                AdjustVideoOrientation();
            }

            // 1. Update Camera Rotation via Gyroscope
            Camera cam = Camera.main;
            if (cam != null && SystemInfo.supportsGyroscope)
            {
                // Convert Gyro attitude to Unity world space orientation
                Quaternion gyroRot = Input.gyro.attitude;
                Quaternion unityRot = new Quaternion(gyroRot.x, gyroRot.y, -gyroRot.z, -gyroRot.w);
                cam.transform.localRotation = Quaternion.Euler(90, 0, 0) * unityRot;
            }

            // 2. Smooth heading for motion modes where phone swings wildly
            UpdateHeading();

            // 3. Adaptive Step Detection and Pose Recognition
            DetectStep();

            // 4. Move Camera along virtual position
            if (cam != null)
            {
                cam.transform.position = _virtualPosition;
            }
        }

        /// <summary>
        /// Applies a 1D Kalman filter to the camera's Y-axis (yaw/heading) angle
        /// to smooth out gyroscope noise before using it as the walking direction.
        /// (Based on: MDPI Sensors 2022 — Context-Aware 3D PDR with Kalman Filtering)
        /// </summary>
        private void UpdateHeading()
        {
            Camera cam = Camera.main;
            if (cam == null) return;

            Vector3 camForward = cam.transform.forward;
            camForward.y = 0f;
            if (camForward.sqrMagnitude < 0.01f) return;
            camForward.Normalize();

            // Raw measured heading angle (degrees)
            float measuredAngle = Mathf.Atan2(camForward.x, camForward.z) * Mathf.Rad2Deg;

            // --- 1D Kalman Filter ---
            // Predict step
            _kalmanErrorCov += KALMAN_PROCESS_NOISE;
            // Update step
            float kalmanGain = _kalmanErrorCov / (_kalmanErrorCov + KALMAN_MEAS_NOISE);
            _kalmanHeadingAngle += kalmanGain * (measuredAngle - _kalmanHeadingAngle);
            _kalmanErrorCov     *= (1f - kalmanGain);
            // ---

            // Convert filtered angle back to direction vector
            float rad = _kalmanHeadingAngle * Mathf.Deg2Rad;
            _smoothedHeading = new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad)).normalized;
        }

        /// <summary>
        /// Adaptive step detection with:
        ///  - ZUPT: stops counting steps when phone is perfectly still (Paper 3)
        ///  - Motion Mode Recognition: adjusts threshold by pose (Paper 3)
        ///  - Stair Detection: adjusts Y position when walking up/down stairs (Paper 4)
        /// </summary>
        private void DetectStep()
        {
            Vector3 accel = Input.acceleration;
            float mag = accel.magnitude;

            // --- Track magnitude history for variance & pose recognition ---
            _accelMagHistory.Enqueue(mag);
            if (_accelMagHistory.Count > WINDOW_SIZE) _accelMagHistory.Dequeue();

            float sum = 0f;
            foreach (float v in _accelMagHistory) sum += v;
            float mean = sum / Mathf.Max(1, _accelMagHistory.Count);

            float varianceSum = 0f;
            foreach (float v in _accelMagHistory) varianceSum += (v - mean) * (v - mean);
            float variance = varianceSum / Mathf.Max(1, _accelMagHistory.Count);

            // --- ZUPT: Zero Velocity Update (Paper 3) ---
            // If variance is very LOW, the phone is sitting on a desk — stop detecting steps.
            if (variance < ZUPT_STILL_THRESHOLD)
            {
                _stillnessTimer += Time.deltaTime;
                if (_stillnessTimer >= ZUPT_STILL_SECONDS)
                    return; // Phone is stationary — ignore all step signals
            }
            else
            {
                _stillnessTimer = 0f; // User is moving — reset stillness timer
            }

            // --- Heuristic Motion Mode Recognition (Paper 3) ---
            if (variance > 0.35f) _currentPose = PhonePose.PocketOrSwinging;
            else _currentPose = PhonePose.Holding;

            // --- Stair Detection via sustained vertical acceleration (Paper 4) ---
            _verticalAccelHistory.Enqueue(accel.y);
            if (_verticalAccelHistory.Count > STAIR_WINDOW) _verticalAccelHistory.Dequeue();

            float vertSum = 0f;
            foreach (float v in _verticalAccelHistory) vertSum += v;
            float avgVertical = vertSum / Mathf.Max(1, _verticalAccelHistory.Count);
            // avgVertical > 0.15 means going UP stairs; < -0.15 means going DOWN stairs

            // --- Adaptive Step Threshold (Paper 3) ---
            float currentThreshold = stepThreshold;
            if (_currentPose == PhonePose.PocketOrSwinging)
                currentThreshold = Mathf.Max(stepThreshold, 1.4f);

            // --- Detect step peak ---
            if (mag > currentThreshold && (Time.time - _lastStepTime) > 0.35f)
            {
                _lastStepTime = Time.time;

                // Determine horizontal direction based on pose
                Vector3 direction = _smoothedHeading;
                if (_currentPose == PhonePose.Holding && Camera.main != null)
                {
                    direction = Camera.main.transform.forward;
                    direction.y = 0f;
                    if (direction.sqrMagnitude > 0.01f) direction.Normalize();
                    else direction = _smoothedHeading;
                }

                // Apply step with optional stair Y adjustment
                float yStep = 0f;
                if (avgVertical > 0.15f)       yStep =  0.15f; // Going UP one stair riser
                else if (avgVertical < -0.15f) yStep = -0.15f; // Going DOWN one stair riser

                _virtualPosition += direction * stepLengthMeters + Vector3.up * yStep;
            }
        }

        private void OnDestroy()
        {
            if (_webCamTexture != null && _webCamTexture.isPlaying)
            {
                _webCamTexture.Stop();
            }
        }
    }
}
