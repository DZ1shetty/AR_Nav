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
            EnsureUIReferencesAndListeners();

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

            ShowModeSelect();
        }

        private void EnsureUIReferencesAndListeners()
        {
            Canvas mainCanvas = GetComponentInParent<Canvas>();
            if (mainCanvas == null) mainCanvas = FindAnyObjectByType<Canvas>();

            // Auto-detect panels if unassigned
            if (modeSelectPanel == null && mainCanvas != null)
            {
                var t = mainCanvas.transform.Find("ModeSelectPanel");
                if (t != null) modeSelectPanel = t.gameObject;
            }
            if (recordingPanel == null && mainCanvas != null)
            {
                var t = mainCanvas.transform.Find("RecordingPanel");
                if (t != null) recordingPanel = t.gameObject;
            }
            if (navigationPanel == null && mainCanvas != null)
            {
                var t = mainCanvas.transform.Find("NavigationPanel");
                if (t != null) navigationPanel = t.gameObject;
            }

            // Fix NavigationPanel Hierarchy: unparent dropdown & start button if they were accidentally nested in text!
            if (navigationPanel != null)
            {
                var allChildren = navigationPanel.GetComponentsInChildren<Transform>(true);
                foreach (var child in allChildren)
                {
                    if (child == navigationPanel.transform) continue;
                    // Move direct UI components directly under NavigationPanel
                    if (child.GetComponent<Button>() != null || child.GetComponent<TMP_Dropdown>() != null)
                    {
                        child.SetParent(navigationPanel.transform, false);
                    }
                }
            }

            // Wire ModeSelect buttons
            if (modeSelectPanel != null)
            {
                var btns = modeSelectPanel.GetComponentsInChildren<Button>(true);
                foreach (var b in btns)
                {
                    string txt = b.GetComponentInChildren<TMP_Text>()?.text?.ToLower() ?? b.name.ToLower();
                    b.onClick.RemoveAllListeners();
                    if (txt.Contains("record"))
                    {
                        b.onClick.AddListener(OpenRecordMode);
                        Debug.Log($"[AppModeController] Wired Record Mode button: {b.name}");
                    }
                    else if (txt.Contains("navigat"))
                    {
                        b.onClick.AddListener(OpenNavigateMode);
                        Debug.Log($"[AppModeController] Wired Navigate Mode button: {b.name}");
                    }
                }
            }

            // Wire RecordingPanel Stop/Finish button & Back button
            if (recordingPanel != null)
            {
                var btns = recordingPanel.GetComponentsInChildren<Button>(true);
                foreach (var b in btns)
                {
                    string txt = b.GetComponentInChildren<TMP_Text>()?.text?.ToLower() ?? b.name.ToLower();
                    b.onClick.RemoveAllListeners();
                    if (txt.Contains("finish") || txt.Contains("save") || txt.Contains("stop"))
                    {
                        stopRecordButton = b;
                        b.onClick.AddListener(OnStopRecordClicked);
                        Debug.Log($"[AppModeController] Wired Stop/Finish Record button: {b.name}");
                    }
                    else if (txt.Contains("cancel") || txt.Contains("back"))
                    {
                        b.onClick.AddListener(CancelRecordingAndReturn);
                    }
                }

                if (recordingStatusText == null)
                {
                    recordingStatusText = recordingPanel.GetComponentInChildren<TMP_Text>(true);
                }
            }

            // Wire NavigationPanel buttons and dropdown
            if (navigationPanel != null)
            {
                var navBtns = navigationPanel.GetComponentsInChildren<Button>(true);
                foreach (var b in navBtns)
                {
                    string txt = b.GetComponentInChildren<TMP_Text>()?.text?.ToLower() ?? b.name.ToLower();
                    b.onClick.RemoveAllListeners();
                    if (txt.Contains("start") || txt.Contains("nav"))
                    {
                        startNavButton = b;
                        b.onClick.AddListener(OnStartNavClicked);
                        Debug.Log($"[AppModeController] Wired Start Nav button: {b.name}");
                    }
                    else if (txt.Contains("stop") || txt.Contains("cancel") || txt.Contains("back"))
                    {
                        stopNavButton = b;
                        b.onClick.AddListener(OnStopNavClicked);
                        Debug.Log($"[AppModeController] Wired Stop/Back Nav button: {b.name}");
                    }
                }

                // Add or wire a Back button if one doesn't exist in NavigationPanel
                EnsureBackButton(navigationPanel.transform, OnStopNavClicked);

                if (destinationDropdown == null)
                {
                    destinationDropdown = navigationPanel.GetComponentInChildren<TMP_Dropdown>(true);
                }
                if (navInstructionText == null)
                {
                    navInstructionText = navigationPanel.GetComponentInChildren<TMP_Text>(true);
                }
            }

            if (thumbsUpButton != null)
            {
                thumbsUpButton.onClick.RemoveAllListeners();
                thumbsUpButton.onClick.AddListener(() => SubmitRating(1.0f));
            }
            if (thumbsDownButton != null)
            {
                thumbsDownButton.onClick.RemoveAllListeners();
                thumbsDownButton.onClick.AddListener(() => SubmitRating(-0.5f));
            }
        }

        private void EnsureBackButton(Transform parentPanel, UnityEngine.Events.UnityAction action)
        {
            var existing = parentPanel.Find("BackButton");
            if (existing == null)
            {
                GameObject backObj = new GameObject("BackButton");
                backObj.transform.SetParent(parentPanel, false);
                Image img = backObj.AddComponent<Image>();
                img.color = new Color(0.15f, 0.15f, 0.18f, 0.9f);
                Button btn = backObj.AddComponent<Button>();
                btn.onClick.AddListener(action);

                RectTransform rt = backObj.GetComponent<RectTransform>();
                rt.anchorMin = new Vector2(0f, 1f);
                rt.anchorMax = new Vector2(0f, 1f);
                rt.pivot = new Vector2(0f, 1f);
                rt.anchoredPosition = new Vector2(40, -40);
                rt.sizeDelta = new Vector2(180, 70);

                GameObject textObj = new GameObject("Text (TMP)");
                textObj.transform.SetParent(backObj.transform, false);
                var tmp = textObj.AddComponent<TextMeshProUGUI>();
                tmp.text = "← Back";
                tmp.fontSize = 26;
                tmp.alignment = TextAlignmentOptions.Center;
                tmp.color = Color.white;
                RectTransform textRt = textObj.GetComponent<RectTransform>();
                textRt.anchorMin = Vector2.zero;
                textRt.anchorMax = Vector2.one;
                textRt.sizeDelta = Vector2.zero;
            }
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

            // Start recording cleanly
            if (recorder != null)
            {
                string startName = (startPointInput != null && !string.IsNullOrEmpty(startPointInput.text))
                    ? startPointInput.text : "Start Location";
                recorder.StartRecording(startName);
            }

            if (recordingStatusText != null) 
                recordingStatusText.text = "🔴 Recording Route... Walk normally.\nTap 'Finish & Save' when done.";
        }

        public void CancelRecordingAndReturn()
        {
            if (recorder != null && recorder.IsRecording)
            {
                recorder.StopAndSaveRecording("Cancelled");
            }
            ShowModeSelect();
        }

        public void OpenNavigateMode()
        {
            if (modeSelectPanel != null) modeSelectPanel.SetActive(false);
            if (recordingPanel != null) recordingPanel.SetActive(false);
            if (navigationPanel != null) navigationPanel.SetActive(true);
            if (feedbackPanel != null) feedbackPanel.SetActive(false);

            RefreshGraphDestinations();

            if (navInstructionText != null)
            {
                navInstructionText.text = (_cachedGraph != null && _cachedGraph.nodes.Count >= 2)
                    ? "Select destination above & tap Start Navigation"
                    : "No routes recorded yet. Please tap Back and Record a route first!";
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
            // 1. Format ModeSelectPanel
            if (modeSelectPanel != null)
            {
                var buttons = modeSelectPanel.GetComponentsInChildren<Button>(true);
                if (buttons.Length >= 2)
                {
                    RectTransform rt1 = buttons[0].GetComponent<RectTransform>();
                    RectTransform rt2 = buttons[1].GetComponent<RectTransform>();

                    if (rt1 != null)
                    {
                        rt1.sizeDelta = new Vector2(540, 130);
                        rt1.anchoredPosition = new Vector2(0, 120);
                    }
                    if (rt2 != null)
                    {
                        rt2.sizeDelta = new Vector2(540, 130);
                        rt2.anchoredPosition = new Vector2(0, -80);
                    }
                }
            }

            // 2. Format RecordingPanel
            if (recordingPanel != null)
            {
                if (recordingStatusText != null)
                {
                    RectTransform rtStatus = recordingStatusText.GetComponent<RectTransform>();
                    rtStatus.anchorMin = new Vector2(0.5f, 1f);
                    rtStatus.anchorMax = new Vector2(0.5f, 1f);
                    rtStatus.pivot = new Vector2(0.5f, 1f);
                    rtStatus.anchoredPosition = new Vector2(0, -120);
                    rtStatus.sizeDelta = new Vector2(850, 200);
                    recordingStatusText.alignment = TextAlignmentOptions.Center;
                    recordingStatusText.fontSize = 32;
                }

                if (stopRecordButton != null)
                {
                    RectTransform rtStop = stopRecordButton.GetComponent<RectTransform>();
                    rtStop.anchorMin = new Vector2(0.5f, 0f);
                    rtStop.anchorMax = new Vector2(0.5f, 0f);
                    rtStop.pivot = new Vector2(0.5f, 0f);
                    rtStop.sizeDelta = new Vector2(540, 130);
                    rtStop.anchoredPosition = new Vector2(0, 120);
                }
            }

            // 3. Format NavigationPanel (Prevent any overlapping between instruction text, dropdown, and button)
            if (navigationPanel != null)
            {
                if (navInstructionText != null)
                {
                    RectTransform rtInstruction = navInstructionText.GetComponent<RectTransform>();
                    rtInstruction.anchorMin = new Vector2(0.5f, 1f);
                    rtInstruction.anchorMax = new Vector2(0.5f, 1f);
                    rtInstruction.pivot = new Vector2(0.5f, 1f);
                    rtInstruction.anchoredPosition = new Vector2(0, -140);
                    rtInstruction.sizeDelta = new Vector2(850, 200);
                    navInstructionText.alignment = TextAlignmentOptions.Center;
                    navInstructionText.fontSize = 32;
                }

                if (destinationDropdown != null)
                {
                    RectTransform rtDropdown = destinationDropdown.GetComponent<RectTransform>();
                    rtDropdown.anchorMin = new Vector2(0.5f, 0.5f);
                    rtDropdown.anchorMax = new Vector2(0.5f, 0.5f);
                    rtDropdown.pivot = new Vector2(0.5f, 0.5f);
                    rtDropdown.anchoredPosition = new Vector2(0, 80);
                    rtDropdown.sizeDelta = new Vector2(650, 110);
                }

                if (startNavButton != null)
                {
                    RectTransform rtStart = startNavButton.GetComponent<RectTransform>();
                    rtStart.anchorMin = new Vector2(0.5f, 0.5f);
                    rtStart.anchorMax = new Vector2(0.5f, 0.5f);
                    rtStart.pivot = new Vector2(0.5f, 0.5f);
                    rtStart.anchoredPosition = new Vector2(0, -80);
                    rtStart.sizeDelta = new Vector2(540, 130);
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
