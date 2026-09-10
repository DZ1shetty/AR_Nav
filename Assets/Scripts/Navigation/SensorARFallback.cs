using System.Collections;
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

        [Header("Movement Settings")]
        [SerializeField] private float stepLengthMeters = 0.68f; // Standard walking stride
        [SerializeField] private float stepThreshold = 1.15f;    // Acceleration peak threshold

        // Sensor tracking state
        private bool _isActive = false;
        private Vector3 _virtualPosition = Vector3.zero;
        private float _lastStepTime = 0f;

        public bool IsActive => _isActive;
        public Vector3 VirtualPosition => _virtualPosition;

        public void ActivateFallback()
        {
            if (_isActive) return;
            _isActive = true;

            Debug.Log("[SensorARFallback] Activating Gyroscope + WebCam + Step Detection Fallback...");

            // 1. Enable Hardware Gyroscope
            Input.gyro.enabled = true;

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
                RectTransform rt = cameraBackgroundUI.rectTransform;
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.sizeDelta = Vector2.zero;
                rt.anchoredPosition = Vector2.zero;
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
            if (_webCamTexture == null || cameraBackgroundUI == null) return;

            RectTransform rt = cameraBackgroundUI.rectTransform;

            // 1. Rotate to match sensor hardware angle
            int angle = _webCamTexture.videoRotationAngle;
            rt.localEulerAngles = new Vector3(0, 0, -angle);

            // 2. Fix inverted / flipped mirror axis
            float scaleY = _webCamTexture.videoVerticallyMirrored ? -1.0f : 1.0f;
            rt.localScale = new Vector3(1.0f, scaleY, 1.0f);

            // 3. Scale appropriately to fill the screen in portrait mode
            if (angle == 90 || angle == 270)
            {
                // When rotated 90/270 degrees in portrait, swap aspect ratio to prevent letterboxing
                float screenAspect = (float)Screen.width / (float)Screen.height;
                float videoAspect = (float)_webCamTexture.width / (float)_webCamTexture.height;
                if (videoAspect < 1f) videoAspect = 1f / videoAspect;

                float scale = Mathf.Max(1.0f, (1.0f / screenAspect) / videoAspect);
                rt.localScale = new Vector3(scale, scaleY * scale, 1.0f);
            }
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

            // 2. Step Detection using Accelerometer magnitude peaks
            DetectStep();

            // 3. Move Camera along virtual position
            if (cam != null)
            {
                cam.transform.position = _virtualPosition;
            }
        }

        private void DetectStep()
        {
            Vector3 accel = Input.acceleration;
            float mag = accel.magnitude;

            // Detect peak acceleration above threshold with refractory cooldown (min 0.35s between steps)
            if (mag > stepThreshold && (Time.time - _lastStepTime) > 0.35f)
            {
                _lastStepTime = Time.time;

                Camera cam = Camera.main;
                Vector3 forward = cam != null ? cam.transform.forward : Vector3.forward;
                forward.y = 0f; // Keep walking on flat ground
                forward.Normalize();

                _virtualPosition += forward * stepLengthMeters;
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
