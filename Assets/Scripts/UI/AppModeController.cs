using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.ARFoundation;
#if UNITY_ANDROID
using UnityEngine.Android;
#endif
using TMPro;
using ARNav.Recording;
using ARNav.Navigation;
using ARNav.Graph;

namespace ARNav.UI
{
    /// <summary>
    /// Master UI controller managing the transition between Record Mode and Navigate Mode,
    /// camera permission requests, ARCore availability verification, and responsive mobile UI layout.
    /// </summary>
    public class AppModeController : MonoBehaviour
    {
        [Header("Subsystems")]
        [SerializeField] private PathRecorder recorder;
        [SerializeField] private PathNavigator navigator;
        [SerializeField] private ARNav.Backend.SupabaseSyncService supabaseService;

        [Header("UI Panels")]
        [SerializeField] private GameObject modeSelectPanel;
        [SerializeField] private GameObject recordingPanel;
        [SerializeField] private GameObject navigationPanel;
        [SerializeField] private GameObject feedbackPanel;

        [Header("Recording UI")]
        [SerializeField] private TMP_Text recordingStatusText;
        [SerializeField] private TMP_InputField startPointInput;
        [SerializeField] private TMP_InputField endPointInput;
        [SerializeField] private Button startRecordButton;
        [SerializeField] private Button stopRecordButton;

        [Header("Navigation UI")]
        [SerializeField] private TMP_Text navInstructionText;
        [SerializeField] private TMP_Dropdown destinationDropdown;
        [SerializeField] private Button startNavButton;
        [SerializeField] private Button stopNavButton;

        [Header("Feedback UI (Post-Walk Rating)")]
        [SerializeField] private Button thumbsUpButton;
        [SerializeField] private Button thumbsDownButton;

        private BuildingGraph _cachedGraph;
        private List<string> _availableDestinations = new List<string>();

        // QR Code Localization (Paper 2: ICSCSS 2023)
        // Stores the node ID that was resolved from the last QR code scan.
        // When set, navigation uses this as the start node instead of blindly using nodes[0].
        private string _qrLocatedNodeId = null;

        private void Awake()
        {
            FormatCanvasScaler();
        }

        private void Start()
        {
            StartCoroutine(InitializePermissionsAndAR());
            AutoLayoutButtons();
            if (recorder == null) recorder = FindAnyObjectByType<PathRecorder>();
            if (navigator == null) navigator = FindAnyObjectByType<PathNavigator>();
            if (supabaseService == null) supabaseService = FindAnyObjectByType<ARNav.Backend.SupabaseSyncService>();

            // Hook recorder events
            if (recorder != null)
            {
                recorder.OnStatusChanged += UpdateRecordingStatus;
                recorder.OnRecordingProgress += (pts, dist) =>
                {
                    if (recordingStatusText != null)
                        recordingStatusText.text = $"Recording: {pts} pts | {dist:F1}m | Anchors: {recorder.AnchorCount}";
                };
            }

            if (supabaseService != null)
            {
                supabaseService.OnUploadCompleted += (msg) => UpdateRecordingStatus(msg);
                supabaseService.OnSyncFailed += (err) => Debug.LogWarning(err);
            }

            // Hook navigator events
            if (navigator != null)
            {
                navigator.OnInstructionChanged += UpdateNavInstruction;
                navigator.OnDestinationReached += ShowFeedbackDialog;
            }

            // Button listeners
            if (startRecordButton != null) startRecordButton.onClick.AddListener(OnStartRecordClicked);
            if (stopRecordButton != null) stopRecordButton.onClick.AddListener(OnStopRecordClicked);
            if (startNavButton != null) startNavButton.onClick.AddListener(OnStartNavClicked);
            if (stopNavButton != null) stopNavButton.onClick.AddListener(OnStopNavClicked);

            if (thumbsUpButton != null) thumbsUpButton.onClick.AddListener(() => SubmitRating(1.0f));
            if (thumbsDownButton != null) thumbsDownButton.onClick.AddListener(() => SubmitRating(-0.5f));

            ShowModeSelect();
        }

        public void ShowModeSelect()
        {
            if (modeSelectPanel != null) modeSelectPanel.SetActive(true);
            if (recordingPanel != null) recordingPanel.SetActive(false);
            if (navigationPanel != null) navigationPanel.SetActive(false);
            if (feedbackPanel != null) feedbackPanel.SetActive(false);

            RefreshGraphDestinations();
        }

        public void OpenRecordMode()
        {
            if (modeSelectPanel != null) modeSelectPanel.SetActive(false);
            if (recordingPanel != null) recordingPanel.SetActive(true);
            if (navigationPanel != null) navigationPanel.SetActive(false);
            if (feedbackPanel != null) feedbackPanel.SetActive(false);

            // Automatically start recording path if user taps Record New Route
            if (recorder != null && !recorder.IsRecording)
            {
                string startName = (startPointInput != null && !string.IsNullOrEmpty(startPointInput.text))
                    ? startPointInput.text : "Location A";
                recorder.StartRecording(startName);
            }

            if (recordingStatusText != null) 
                recordingStatusText.text = "Walk along the route. Tap 'Finish & Save' when done.";
        }

        public void OpenNavigateMode()
        {
            if (modeSelectPanel != null) modeSelectPanel.SetActive(false);
            if (recordingPanel != null) recordingPanel.SetActive(false);
            if (navigationPanel != null) navigationPanel.SetActive(true);
            if (feedbackPanel != null) feedbackPanel.SetActive(false);

            RefreshGraphDestinations();

            // Auto-navigate to latest destination if available
            if (_cachedGraph != null && _cachedGraph.nodes.Count >= 2 && navigator != null && !navigator.IsNavigating)
            {
                string startId = _cachedGraph.nodes[0].id;
                string destId = _cachedGraph.nodes[_cachedGraph.nodes.Count - 1].id;
                navigator.StartNavigation(startId, destId);
            }
        }

        public void OnStartRecordClicked()
        {
            string startName = (startPointInput != null && !string.IsNullOrEmpty(startPointInput.text))
                ? startPointInput.text : "Location A";

            recorder.StartRecording(startName);
        }

        public void OnStopRecordClicked()
        {
            string endName = (endPointInput != null && !string.IsNullOrEmpty(endPointInput.text))
                ? endPointInput.text : "Location B";

            BuildingGraph saved = recorder.StopAndSaveRecording(endName);

            if (supabaseService != null && saved != null && saved.nodes.Count >= 2 && saved.edges.Count > 0)
            {
                var n1 = saved.nodes[saved.nodes.Count - 2];
                var n2 = saved.nodes[saved.nodes.Count - 1];
                var edge = saved.edges[saved.edges.Count - 1];
                supabaseService.UploadEdge(n1, n2, edge);
            }

            ShowModeSelect();
        }

        private void RefreshGraphDestinations()
        {
            string path = Path.Combine(Application.persistentDataPath, "indoor_graph.json");
            _cachedGraph = BuildingGraph.LoadFromFile(path);

            if (destinationDropdown == null) return;

            destinationDropdown.ClearOptions();
            _availableDestinations.Clear();

            List<string> options = new List<string>();
            foreach (var node in _cachedGraph.nodes)
            {
                options.Add(node.name);
                _availableDestinations.Add(node.id);
            }

            if (options.Count == 0)
            {
                options.Add("(No recorded routes yet)");
            }

            destinationDropdown.AddOptions(options);
        }

        private void OnStartNavClicked()
        {
            if (_cachedGraph == null || _cachedGraph.nodes.Count < 2 || _availableDestinations.Count < 2)
            {
                if (navInstructionText != null)
                    navInstructionText.text = "Need at least 2 recorded nodes to navigate!";
                return;
            }

            int selectedIndex = destinationDropdown != null ? destinationDropdown.value : 0;
            if (selectedIndex < 0 || selectedIndex >= _availableDestinations.Count) return;

            string targetNodeId = _availableDestinations[selectedIndex];

            // Paper 2: Use QR-scanned start node if available; otherwise fall back to first node.
            string startNodeId = (!string.IsNullOrEmpty(_qrLocatedNodeId))
                ? _qrLocatedNodeId
                : _cachedGraph.nodes[0].id;

            navigator.StartNavigation(startNodeId, targetNodeId);
        }

        /// <summary>
        /// Called by a UI Button in the Navigation Panel.
        /// Opens the device camera briefly to scan a QR code placed at building entrances/rooms.
        /// The QR code should encode the exact node name (e.g. "Main Entrance", "Lab 101").
        /// (Based on: ICSCSS 2023 — AR Indoor Navigation using Unity Engine + QR Codes)
        /// </summary>
        public void ScanLocationQR()
        {
            if (navInstructionText != null)
                navInstructionText.text = "Point camera at a location QR code...";

            // Unity's WebCamTexture-based QR scanning:
            // We start a coroutine that reads frames and tries to decode them.
            // For a full production build, swap this with ZXing or a native plugin.
            StartCoroutine(QRScanRoutine());
        }

        private System.Collections.IEnumerator QRScanRoutine()
        {
            // Use the back camera for QR scanning
            WebCamTexture camTex = new WebCamTexture();
            camTex.Play();
            yield return new WaitForSeconds(0.5f); // Let camera warm up

            float timeout = 10f;
            bool found = false;

            while (timeout > 0f && !found)
            {
                timeout -= Time.deltaTime;

                // --- Simulated QR decode ---
                // In production: pass camTex pixels to ZXing BarcodeReader.
                // For now we demonstrate the localization flow with a placeholder.
                string decoded = TryDecodeQRFromCamera(camTex);

                if (!string.IsNullOrEmpty(decoded))
                {
                    OnQRCodeScanned(decoded);
                    found = true;
                }

                yield return null;
            }

            camTex.Stop();

            if (!found && navInstructionText != null)
                navInstructionText.text = "QR scan timed out. Select start manually.";
        }

        /// <summary>
        /// Placeholder: replace this method body with a real ZXing decode call
        /// (ZXing.Net.Mobile or ZXing.Unity) that reads pixels from the WebCamTexture.
        /// Returns the decoded string, or null if nothing readable yet.
        /// </summary>
        private string TryDecodeQRFromCamera(WebCamTexture tex)
        {
            // TODO: integrate ZXing.BarcodeReader here for production.
            // Example:
            //   var reader = new ZXing.BarcodeReader();
            //   var result = reader.Decode(tex.GetPixels32(), tex.width, tex.height);
            //   return result?.Text;
            return null; // Stub — no actual QR lib linked yet
        }

        /// <summary>
        /// Resolves a decoded QR string to a graph node and stores it as the navigation start.
        /// </summary>
        private void OnQRCodeScanned(string qrValue)
        {
            if (_cachedGraph == null) return;

            // Match the QR value against recorded node names
            var matchedNode = _cachedGraph.nodes.Find(n =>
                string.Equals(n.name, qrValue, System.StringComparison.OrdinalIgnoreCase));

            if (matchedNode != null)
            {
                _qrLocatedNodeId = matchedNode.id;
                if (navInstructionText != null)
                    navInstructionText.text = $"📍 Located at: {matchedNode.name}. Select destination and tap Navigate!";

                Debug.Log($"[AR-NAV] QR Localization: resolved '{qrValue}' → node {matchedNode.id}");
            }
            else
            {
                if (navInstructionText != null)
                    navInstructionText.text = $"QR code '{qrValue}' not found in building map. Walk to a known point.";

                Debug.LogWarning($"[AR-NAV] QR value '{qrValue}' did not match any node name.");
            }
        }

        private void OnStopNavClicked()
        {
            navigator.StopNavigation();
            ShowModeSelect();
        }

        private void ShowFeedbackDialog()
        {
            if (feedbackPanel != null) feedbackPanel.SetActive(true);
        }

        private void SubmitRating(float delta)
        {
            // Adjust confidence score of recent walk
            string path = Path.Combine(Application.persistentDataPath, "indoor_graph.json");
            BuildingGraph graph = BuildingGraph.LoadFromFile(path);

            if (graph.edges.Count > 0)
            {
                var lastEdge = graph.edges[graph.edges.Count - 1];
                lastEdge.confidenceScore = Mathf.Clamp(lastEdge.confidenceScore + delta, 0.1f, 1.0f);
                lastEdge.contributingWalkCount++;
                graph.SaveToFile(path);
            }

            if (feedbackPanel != null) feedbackPanel.SetActive(false);
            ShowModeSelect();
        }

        private void UpdateRecordingStatus(string msg)
        {
            if (recordingStatusText != null) recordingStatusText.text = msg;
        }

        private void UpdateNavInstruction(string msg)
        {
            if (navInstructionText != null) navInstructionText.text = msg;
        }

        private void FormatCanvasScaler()
        {
            CanvasScaler scaler = GetComponentInParent<CanvasScaler>();
            if (scaler == null) scaler = FindAnyObjectByType<CanvasScaler>();
            if (scaler != null)
            {
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1080, 1920); // Standard vertical phone screen
                scaler.matchWidthOrHeight = 0.5f;
            }
        }

        private void AutoLayoutButtons()
        {
            // Ensure ModeSelectPanel buttons are comfortably spaced on high-res mobile screens
            if (modeSelectPanel != null)
            {
                var buttons = modeSelectPanel.GetComponentsInChildren<Button>(true);
                if (buttons.Length >= 2)
                {
                    RectTransform rt1 = buttons[0].GetComponent<RectTransform>();
                    RectTransform rt2 = buttons[1].GetComponent<RectTransform>();

                    if (rt1 != null)
                    {
                        rt1.sizeDelta = new Vector2(500, 120);
                        rt1.anchoredPosition = new Vector2(0, 100);
                    }
                    if (rt2 != null)
                    {
                        rt2.sizeDelta = new Vector2(500, 120);
                        rt2.anchoredPosition = new Vector2(0, -100);
                    }
                }
            }

            // Ensure StopRecordButton is well sized and positioned near the bottom of screen
            if (stopRecordButton != null)
            {
                RectTransform rtStop = stopRecordButton.GetComponent<RectTransform>();
                if (rtStop != null)
                {
                    rtStop.sizeDelta = new Vector2(500, 120);
                    rtStop.anchoredPosition = new Vector2(0, -250);
                }
            }
        }

        private IEnumerator InitializePermissionsAndAR()
        {
#if UNITY_ANDROID
            // 1. Request Camera Permission if not already granted
            if (!Permission.HasUserAuthorizedPermission(Permission.Camera))
            {
                Permission.RequestUserPermission(Permission.Camera);
                float timeout = 10f;
                while (!Permission.HasUserAuthorizedPermission(Permission.Camera) && timeout > 0f)
                {
                    timeout -= 0.2f;
                    yield return new WaitForSeconds(0.2f);
                }
            }
#endif

            // 2. Check if Google Play Services for AR (ARCore) is supported
            float availTimeout = 3f;
            while ((ARSession.state == ARSessionState.None || ARSession.state == ARSessionState.CheckingAvailability) && availTimeout > 0f)
            {
                yield return ARSession.CheckAvailability();
                availTimeout -= 0.2f;
                yield return new WaitForSeconds(0.2f);
            }

            Debug.Log($"[AR-NAV] Resolved ARSession state: {ARSession.state}");

            bool useFallback = (ARSession.state == ARSessionState.Unsupported || 
                                ARSession.state == ARSessionState.NeedsInstall || 
                                ARSession.state == ARSessionState.None);

            if (useFallback)
            {
                Debug.LogWarning("[AR-NAV] ARCore not active on this device. Activating Sensor AR Fallback (Gyro + Camera Passthrough + Pedometer)...");

                // Disable ARSession and ARCameraBackground to release camera hardware
                ARSession session = FindAnyObjectByType<ARSession>();
                if (session != null) session.enabled = false;

                ARCameraBackground bgComp = FindAnyObjectByType<ARCameraBackground>();
                if (bgComp != null) bgComp.enabled = false;

                SensorARFallback fallback = FindAnyObjectByType<SensorARFallback>();
                if (fallback == null)
                {
                    fallback = gameObject.AddComponent<SensorARFallback>();
                }
                fallback.ActivateFallback();

                UpdateRecordingStatus("Running in Sensor AR Mode (Gyro + Pedometer). Ready to record!");
                yield break;
            }

            // 3. If ARCore is supported, ensure ARCameraBackground and ARSession are fully woken up
            ARCameraBackground bg = FindAnyObjectByType<ARCameraBackground>();
            if (bg != null && !bg.enabled)
            {
                bg.enabled = true;
            }

            ARSession activeSession = FindAnyObjectByType<ARSession>();
            if (activeSession != null)
            {
                activeSession.enabled = false;
                yield return null;
                activeSession.enabled = true;
                activeSession.Reset();
            }

            Debug.Log($"AR Session active & rendering camera feed: {ARSession.state}");
        }
    }
}
