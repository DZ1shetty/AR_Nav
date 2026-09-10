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
    /// Master UI controller — manages Record/Navigate modes, camera permissions,
    /// ARCore vs Sensor-AR fallback, and responsive mobile layout.
    ///
    /// IndoorNavPlaceNote-inspired improvements:
    ///  • Start-node dropdown so the user picks where they ARE, not just where they want to go.
    ///  • Named Start/End input before recording so routes are labelled meaningfully.
    ///  • Navigation starts only when a valid start ≠ destination is selected.
    /// </summary>
    public class AppModeController : MonoBehaviour
    {
        // ─── Inspector fields ──────────────────────────────────────────────────────
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
        [SerializeField] private TMP_Text      recordingStatusText;
        [SerializeField] private TMP_InputField startPointInput;
        [SerializeField] private TMP_InputField endPointInput;
        [SerializeField] private Button         startRecordButton;
        [SerializeField] private Button         stopRecordButton;

        [Header("Navigation UI")]
        [SerializeField] private TMP_Text     navInstructionText;
        [SerializeField] private TMP_Dropdown destinationDropdown;    // WHERE you want to go
        [SerializeField] private TMP_Dropdown startNodeDropdown;      // WHERE you are now
        [SerializeField] private Button       startNavButton;
        [SerializeField] private Button       stopNavButton;

        [Header("Feedback UI (Post-Walk Rating)")]
        [SerializeField] private Button thumbsUpButton;
        [SerializeField] private Button thumbsDownButton;

        // ─── Runtime state ─────────────────────────────────────────────────────────
        private BuildingGraph   _cachedGraph;
        private List<string>    _availableNodeIds = new List<string>();

        // QR Code start localization (ICSCSS 2023)
        private string _qrLocatedNodeId = null;

        // ─── Unity lifecycle ───────────────────────────────────────────────────────

        private void Awake()
        {
            FormatCanvasScaler();
        }

        private void Start()
        {
            StartCoroutine(InitializePermissionsAndAR());
            AutoLayoutButtons();
            EnsureUIReferencesAndListeners();

            if (recorder      == null) recorder      = FindAnyObjectByType<PathRecorder>();
            if (navigator     == null) navigator     = FindAnyObjectByType<PathNavigator>();
            if (supabaseService == null) supabaseService = FindAnyObjectByType<ARNav.Backend.SupabaseSyncService>();

            // Recorder events
            if (recorder != null)
            {
                recorder.OnStatusChanged += UpdateRecordingStatus;
                recorder.OnRecordingProgress += (pts, dist) =>
                {
                    if (recordingStatusText != null)
                        recordingStatusText.text =
                            $"🔴 Recording: {pts} pts | {dist:F1}m | Anchors: {recorder.AnchorCount}";
                };
            }

            if (supabaseService != null)
            {
                supabaseService.OnUploadCompleted += (msg) => UpdateRecordingStatus(msg);
                supabaseService.OnSyncFailed      += (err) => Debug.LogWarning(err);
            }

            // Navigator events
            if (navigator != null)
            {
                navigator.OnInstructionChanged += UpdateNavInstruction;
                navigator.OnDestinationReached += ShowFeedbackDialog;
            }

            ShowModeSelect();
        }

        // ─── UI wiring ─────────────────────────────────────────────────────────────

        private void EnsureUIReferencesAndListeners()
        {
            Canvas mainCanvas = GetComponentInParent<Canvas>();
            if (mainCanvas == null) mainCanvas = FindAnyObjectByType<Canvas>();

            // Auto-detect panels
            TryFindPanel(ref modeSelectPanel,  mainCanvas, "ModeSelectPanel");
            TryFindPanel(ref recordingPanel,   mainCanvas, "RecordingPanel");
            TryFindPanel(ref navigationPanel,  mainCanvas, "NavigationPanel");

            // Fix hierarchy: unparent any Button/Dropdown that accidentally ended up
            // nested inside a TMP_Text component
            FixNestedChildren(navigationPanel);
            FixNestedChildren(recordingPanel);

            // Wire ModeSelect buttons
            WirePanel(modeSelectPanel, (b, txt) =>
            {
                if (txt.Contains("record"))
                {
                    b.onClick.AddListener(OpenRecordMode);
                    Debug.Log($"[AppModeController] Wired Record button: {b.name}");
                }
                else if (txt.Contains("navigat"))
                {
                    b.onClick.AddListener(OpenNavigateMode);
                    Debug.Log($"[AppModeController] Wired Navigate button: {b.name}");
                }
            });

            // Wire RecordingPanel
            WirePanel(recordingPanel, (b, txt) =>
            {
                if (txt.Contains("finish") || txt.Contains("save") || txt.Contains("stop"))
                {
                    stopRecordButton = b;
                    b.onClick.AddListener(OnStopRecordClicked);
                    Debug.Log($"[AppModeController] Wired Stop/Finish button: {b.name}");
                }
                else if (txt.Contains("cancel") || txt.Contains("back"))
                {
                    b.onClick.AddListener(CancelRecordingAndReturn);
                }
            });

            if (recordingStatusText == null && recordingPanel != null)
                recordingStatusText = recordingPanel.GetComponentInChildren<TMP_Text>(true);

            // Wire NavigationPanel buttons
            WirePanel(navigationPanel, (b, txt) =>
            {
                if (txt.Contains("start") || txt.Contains("nav") || txt.Contains("go"))
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
            });

            // Ensure a back button exists in nav panel
            if (navigationPanel != null)
                EnsureBackButton(navigationPanel.transform, OnStopNavClicked);

            // Find dropdowns
            if (navigationPanel != null)
            {
                var dropdowns = navigationPanel.GetComponentsInChildren<TMP_Dropdown>(true);
                if (dropdowns.Length >= 1 && destinationDropdown == null)
                    destinationDropdown = dropdowns[0];
                if (dropdowns.Length >= 2 && startNodeDropdown == null)
                    startNodeDropdown = dropdowns[1];

                if (navInstructionText == null)
                    navInstructionText = navigationPanel.GetComponentInChildren<TMP_Text>(true);
            }

            // Wire feedback panel
            if (thumbsUpButton   != null) { thumbsUpButton.onClick.RemoveAllListeners();   thumbsUpButton.onClick.AddListener(() => SubmitRating(1.0f));  }
            if (thumbsDownButton != null) { thumbsDownButton.onClick.RemoveAllListeners(); thumbsDownButton.onClick.AddListener(() => SubmitRating(-0.5f)); }
        }

        // ─── Helper wiring utilities ───────────────────────────────────────────────

        private void TryFindPanel(ref GameObject field, Canvas canvas, string name)
        {
            if (field != null || canvas == null) return;
            var t = canvas.transform.Find(name);
            if (t != null) field = t.gameObject;
        }

        private void FixNestedChildren(GameObject panel)
        {
            if (panel == null) return;
            var all = panel.GetComponentsInChildren<Transform>(true);
            foreach (var child in all)
            {
                if (child == panel.transform) continue;
                if (child.GetComponent<Button>() != null || child.GetComponent<TMP_Dropdown>() != null)
                    child.SetParent(panel.transform, false);
            }
        }

        private void WirePanel(GameObject panel, System.Action<Button, string> handler)
        {
            if (panel == null) return;
            foreach (var b in panel.GetComponentsInChildren<Button>(true))
            {
                string txt = (b.GetComponentInChildren<TMP_Text>()?.text?.ToLower() ?? b.name.ToLower());
                b.onClick.RemoveAllListeners();
                handler(b, txt);
            }
        }

        private void EnsureBackButton(Transform parentPanel, UnityEngine.Events.UnityAction action)
        {
            if (parentPanel.Find("BackButton") != null) return;

            GameObject backObj = new GameObject("BackButton");
            backObj.transform.SetParent(parentPanel, false);

            Image img = backObj.AddComponent<Image>();
            img.color = new Color(0.12f, 0.12f, 0.15f, 0.9f);

            Button btn = backObj.AddComponent<Button>();
            btn.onClick.AddListener(action);

            RectTransform rt = backObj.GetComponent<RectTransform>();
            rt.anchorMin        = new Vector2(0f, 1f);
            rt.anchorMax        = new Vector2(0f, 1f);
            rt.pivot            = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(40f, -40f);
            rt.sizeDelta        = new Vector2(190f, 70f);

            GameObject textObj = new GameObject("Text (TMP)");
            textObj.transform.SetParent(backObj.transform, false);
            var tmp = textObj.AddComponent<TextMeshProUGUI>();
            tmp.text      = "← Back";
            tmp.fontSize  = 26;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color     = Color.white;
            RectTransform textRt = textObj.GetComponent<RectTransform>();
            textRt.anchorMin = Vector2.zero;
            textRt.anchorMax = Vector2.one;
            textRt.sizeDelta = Vector2.zero;
        }

        // ─── Panel visibility ──────────────────────────────────────────────────────

        public void ShowModeSelect()
        {
            SetPanels(modeSelect: true);
            RefreshGraphDestinations();
        }

        public void OpenRecordMode()
        {
            SetPanels(recording: true);
            string startName = (startPointInput != null && !string.IsNullOrEmpty(startPointInput.text))
                ? startPointInput.text : "Start Location";

            if (recorder != null) recorder.StartRecording(startName);

            if (recordingStatusText != null)
                recordingStatusText.text = "🔴 Recording Route... Walk normally.\nTap 'Finish & Save' when done.";
        }

        public void CancelRecordingAndReturn()
        {
            if (recorder != null && recorder.IsRecording)
                recorder.StopAndSaveRecording("Cancelled");
            ShowModeSelect();
        }

        public void OpenNavigateMode()
        {
            SetPanels(navigation: true);
            RefreshGraphDestinations();

            if (navInstructionText != null)
            {
                navInstructionText.text = (_cachedGraph != null && _cachedGraph.nodes.Count >= 2)
                    ? "Pick your location & destination, then tap Start"
                    : "No routes recorded yet. Please go back and Record a route first!";
            }
        }

        private void SetPanels(bool modeSelect = false, bool recording = false,
                               bool navigation = false, bool feedback = false)
        {
            if (modeSelectPanel  != null) modeSelectPanel.SetActive(modeSelect);
            if (recordingPanel   != null) recordingPanel.SetActive(recording);
            if (navigationPanel  != null) navigationPanel.SetActive(navigation);
            if (feedbackPanel    != null) feedbackPanel.SetActive(feedback);
        }

        // ─── Recording flow ────────────────────────────────────────────────────────

        public void OnStartRecordClicked()
        {
            string startName = (startPointInput != null && !string.IsNullOrEmpty(startPointInput.text))
                ? startPointInput.text : "Location A";
            recorder?.StartRecording(startName);
        }

        public void OnStopRecordClicked()
        {
            string endName = (endPointInput != null && !string.IsNullOrEmpty(endPointInput.text))
                ? endPointInput.text : "Location B";

            BuildingGraph saved = recorder?.StopAndSaveRecording(endName);

            if (supabaseService != null && saved != null &&
                saved.nodes.Count >= 2 && saved.edges.Count > 0)
            {
                var n1   = saved.nodes[saved.nodes.Count - 2];
                var n2   = saved.nodes[saved.nodes.Count - 1];
                var edge = saved.edges[saved.edges.Count - 1];
                supabaseService.UploadEdge(n1, n2, edge);
            }

            ShowModeSelect();
        }

        // ─── Navigation flow ───────────────────────────────────────────────────────

        /// <summary>
        /// Populates both the Start-location and Destination dropdowns from the saved graph.
        /// Inspired by IndoorNavPlaceNote where both origin and destination are selectable.
        /// </summary>
        private void RefreshGraphDestinations()
        {
            string path = Path.Combine(Application.persistentDataPath, "indoor_graph.json");
            _cachedGraph = BuildingGraph.LoadFromFile(path);

            _availableNodeIds.Clear();
            var options = new List<string>();

            foreach (var node in _cachedGraph.nodes)
            {
                options.Add(node.name);
                _availableNodeIds.Add(node.id);
            }

            if (options.Count == 0)
                options.Add("(No recorded routes yet)");

            // Destination dropdown
            if (destinationDropdown != null)
            {
                destinationDropdown.ClearOptions();
                destinationDropdown.AddOptions(options);
                // Default: last node as destination
                if (destinationDropdown.options.Count > 1)
                    destinationDropdown.value = destinationDropdown.options.Count - 1;
            }

            // Start node dropdown — same list, default to index 0 (or QR-resolved)
            if (startNodeDropdown != null)
            {
                startNodeDropdown.ClearOptions();
                startNodeDropdown.AddOptions(new List<string>(options));
                startNodeDropdown.value = 0;

                // If QR gave us a node, pre-select it
                if (!string.IsNullOrEmpty(_qrLocatedNodeId))
                {
                    int idx = _availableNodeIds.IndexOf(_qrLocatedNodeId);
                    if (idx >= 0) startNodeDropdown.value = idx;
                }
            }
            else
            {
                // No second dropdown in scene — create one dynamically
                if (navigationPanel != null && _cachedGraph.nodes.Count >= 2)
                    EnsureStartNodeDropdown();
            }
        }

        /// <summary>
        /// Creates a "You are at:" dropdown above the existing destination dropdown when
        /// the scene doesn't have one pre-wired.
        /// </summary>
        private void EnsureStartNodeDropdown()
        {
            if (navigationPanel == null || startNodeDropdown != null) return;

            // Find existing destination dropdown to place above it
            RectTransform destRT = destinationDropdown?.GetComponent<RectTransform>();

            GameObject dropObj = new GameObject("StartNodeDropdown");
            dropObj.transform.SetParent(navigationPanel.transform, false);

            startNodeDropdown = dropObj.AddComponent<TMP_Dropdown>();

            RectTransform rt = dropObj.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot     = new Vector2(0.5f, 0.5f);

            // Place above destination dropdown
            float yPos = destRT != null ? destRT.anchoredPosition.y + 140f : 220f;
            rt.anchoredPosition = new Vector2(0f, yPos);
            rt.sizeDelta        = new Vector2(650f, 110f);

            // Label
            GameObject labelObj = new GameObject("StartLabel");
            labelObj.transform.SetParent(navigationPanel.transform, false);
            TextMeshProUGUI label = labelObj.AddComponent<TextMeshProUGUI>();
            label.text      = "📍 You are at:";
            label.fontSize  = 24;
            label.color     = new Color(0.7f, 0.95f, 1f, 1f);
            label.alignment = TextAlignmentOptions.Center;
            RectTransform labelRT = labelObj.GetComponent<RectTransform>();
            labelRT.anchorMin        = new Vector2(0.5f, 0.5f);
            labelRT.anchorMax        = new Vector2(0.5f, 0.5f);
            labelRT.pivot            = new Vector2(0.5f, 0.5f);
            labelRT.anchoredPosition = new Vector2(0f, yPos + 70f);
            labelRT.sizeDelta        = new Vector2(650f, 50f);

            // Populate options
            var opts = new List<string>();
            foreach (var node in _cachedGraph.nodes) opts.Add(node.name);
            startNodeDropdown.AddOptions(opts);
        }

        private void OnStartNavClicked()
        {
            if (_cachedGraph == null || _cachedGraph.nodes.Count < 2 || _availableNodeIds.Count < 2)
            {
                if (navInstructionText != null)
                    navInstructionText.text = "Need at least 2 recorded nodes to navigate!";
                return;
            }

            // Resolve destination index
            int destIndex = destinationDropdown != null ? destinationDropdown.value : _availableNodeIds.Count - 1;
            if (destIndex < 0 || destIndex >= _availableNodeIds.Count) return;
            string targetNodeId = _availableNodeIds[destIndex];

            // Resolve start node:
            //  1. QR-scanned node  (highest priority)
            //  2. User-selected start dropdown
            //  3. Fallback: first recorded node
            string startNodeId;
            if (!string.IsNullOrEmpty(_qrLocatedNodeId))
            {
                startNodeId = _qrLocatedNodeId;
            }
            else if (startNodeDropdown != null && startNodeDropdown.value >= 0
                     && startNodeDropdown.value < _availableNodeIds.Count)
            {
                startNodeId = _availableNodeIds[startNodeDropdown.value];
            }
            else
            {
                startNodeId = _cachedGraph.nodes[0].id;
            }

            // Guard: start == destination
            if (startNodeId == targetNodeId)
            {
                if (navInstructionText != null)
                    navInstructionText.text = "Start and destination are the same! Please pick a different destination.";
                return;
            }

            bool ok = navigator.StartNavigation(startNodeId, targetNodeId);
            if (!ok && navInstructionText != null)
                navInstructionText.text = "Could not find a path. Try a different destination.";
        }

        private void OnStopNavClicked()
        {
            navigator?.StopNavigation();
            ShowModeSelect();
        }

        // ─── QR code localization ──────────────────────────────────────────────────

        /// <summary>
        /// Opens QR scanner. On success, pre-selects the matching node as start location.
        /// (ICSCSS 2023 — AR Indoor Navigation using Unity + QR Codes)
        /// </summary>
        public void ScanLocationQR()
        {
            if (navInstructionText != null)
                navInstructionText.text = "Point camera at a location QR code...";
            StartCoroutine(QRScanRoutine());
        }

        private IEnumerator QRScanRoutine()
        {
            WebCamTexture camTex = new WebCamTexture();
            camTex.Play();
            yield return new WaitForSeconds(0.5f);

            float timeout = 10f;
            bool found = false;

            while (timeout > 0f && !found)
            {
                timeout -= Time.deltaTime;
                string decoded = TryDecodeQRFromCamera(camTex);
                if (!string.IsNullOrEmpty(decoded)) { OnQRCodeScanned(decoded); found = true; }
                yield return null;
            }

            camTex.Stop();
            if (!found && navInstructionText != null)
                navInstructionText.text = "QR scan timed out. Select start manually.";
        }

        private string TryDecodeQRFromCamera(WebCamTexture tex)
        {
            // TODO: Replace with ZXing.BarcodeReader for production
            // var reader = new ZXing.BarcodeReader();
            // var result = reader.Decode(tex.GetPixels32(), tex.width, tex.height);
            // return result?.Text;
            return null;
        }

        private void OnQRCodeScanned(string qrValue)
        {
            if (_cachedGraph == null) return;
            var matchedNode = _cachedGraph.nodes.Find(n =>
                string.Equals(n.name, qrValue, System.StringComparison.OrdinalIgnoreCase));

            if (matchedNode != null)
            {
                _qrLocatedNodeId = matchedNode.id;
                // Pre-select in start dropdown
                if (startNodeDropdown != null)
                {
                    int idx = _availableNodeIds.IndexOf(matchedNode.id);
                    if (idx >= 0) startNodeDropdown.value = idx;
                }
                if (navInstructionText != null)
                    navInstructionText.text = $"📍 Located at: {matchedNode.name}. Select destination & tap Navigate!";
                Debug.Log($"[AR-NAV] QR Localization → node {matchedNode.id}");
            }
            else
            {
                if (navInstructionText != null)
                    navInstructionText.text = $"QR code '{qrValue}' not found in building map.";
            }
        }

        // ─── Feedback / Rating ─────────────────────────────────────────────────────

        private void ShowFeedbackDialog()
        {
            if (feedbackPanel != null) feedbackPanel.SetActive(true);
        }

        private void SubmitRating(float delta)
        {
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

        // ─── Status helpers ────────────────────────────────────────────────────────

        private void UpdateRecordingStatus(string msg)
        {
            if (recordingStatusText != null) recordingStatusText.text = msg;
        }

        private void UpdateNavInstruction(string msg)
        {
            if (navInstructionText != null) navInstructionText.text = msg;
        }

        // ─── Layout helpers ────────────────────────────────────────────────────────

        private void FormatCanvasScaler()
        {
            CanvasScaler scaler = GetComponentInParent<CanvasScaler>();
            if (scaler == null) scaler = FindAnyObjectByType<CanvasScaler>();
            if (scaler != null)
            {
                scaler.uiScaleMode        = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1080, 1920);
                scaler.matchWidthOrHeight  = 0.5f;
            }
        }

        private void AutoLayoutButtons()
        {
            // ModeSelect panel
            if (modeSelectPanel != null)
            {
                var buttons = modeSelectPanel.GetComponentsInChildren<Button>(true);
                if (buttons.Length >= 2)
                {
                    SetRectTransform(buttons[0].GetComponent<RectTransform>(), new Vector2(540, 130), new Vector2(0,  120));
                    SetRectTransform(buttons[1].GetComponent<RectTransform>(), new Vector2(540, 130), new Vector2(0, -80));
                }
            }

            // RecordingPanel
            if (recordingPanel != null)
            {
                if (recordingStatusText != null)
                {
                    RectTransform rt = recordingStatusText.GetComponent<RectTransform>();
                    rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f);
                    rt.pivot = new Vector2(0.5f, 1f);
                    rt.anchoredPosition = new Vector2(0, -120);
                    rt.sizeDelta = new Vector2(850, 200);
                    recordingStatusText.alignment = TextAlignmentOptions.Center;
                    recordingStatusText.fontSize  = 32;
                }
                if (stopRecordButton != null)
                {
                    SetRectTransform(stopRecordButton.GetComponent<RectTransform>(),
                        new Vector2(540, 130), new Vector2(0, 120),
                        new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f));
                }
            }

            // NavigationPanel
            if (navigationPanel != null)
            {
                if (navInstructionText != null)
                {
                    RectTransform rt = navInstructionText.GetComponent<RectTransform>();
                    rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f);
                    rt.pivot = new Vector2(0.5f, 1f);
                    rt.anchoredPosition = new Vector2(0, -140);
                    rt.sizeDelta = new Vector2(900, 200);
                    navInstructionText.alignment = TextAlignmentOptions.Center;
                    navInstructionText.fontSize  = 30;
                }

                // Destination dropdown — centre of screen
                if (destinationDropdown != null)
                    SetRectTransform(destinationDropdown.GetComponent<RectTransform>(),
                        new Vector2(650, 110), new Vector2(0, -30));

                // Start node dropdown — just above destination
                if (startNodeDropdown != null)
                    SetRectTransform(startNodeDropdown.GetComponent<RectTransform>(),
                        new Vector2(650, 110), new Vector2(0, 110));

                if (startNavButton != null)
                    SetRectTransform(startNavButton.GetComponent<RectTransform>(),
                        new Vector2(540, 130), new Vector2(0, -180));
            }
        }

        private void SetRectTransform(RectTransform rt, Vector2 size, Vector2 pos,
            Vector2 anchorMin = default, Vector2 anchorMax = default, Vector2 pivot = default)
        {
            if (rt == null) return;
            if (anchorMin == default) anchorMin = new Vector2(0.5f, 0.5f);
            if (anchorMax == default) anchorMax = new Vector2(0.5f, 0.5f);
            if (pivot     == default) pivot     = new Vector2(0.5f, 0.5f);
            rt.anchorMin        = anchorMin;
            rt.anchorMax        = anchorMax;
            rt.pivot            = pivot;
            rt.sizeDelta        = size;
            rt.anchoredPosition = pos;
        }

        // ─── ARCore / Permission init ──────────────────────────────────────────────

        private IEnumerator InitializePermissionsAndAR()
        {
#if UNITY_ANDROID
            if (!Permission.HasUserAuthorizedPermission(Permission.Camera))
            {
                Permission.RequestUserPermission(Permission.Camera);
                float t = 10f;
                while (!Permission.HasUserAuthorizedPermission(Permission.Camera) && t > 0f)
                { t -= 0.2f; yield return new WaitForSeconds(0.2f); }
            }
#endif

            float availTimeout = 3f;
            while ((ARSession.state == ARSessionState.None ||
                    ARSession.state == ARSessionState.CheckingAvailability) && availTimeout > 0f)
            {
                yield return ARSession.CheckAvailability();
                availTimeout -= 0.2f;
                yield return new WaitForSeconds(0.2f);
            }

            Debug.Log($"[AR-NAV] ARSession state: {ARSession.state}");

            bool useFallback = ARSession.state == ARSessionState.Unsupported ||
                               ARSession.state == ARSessionState.NeedsInstall ||
                               ARSession.state == ARSessionState.None;

            if (useFallback)
            {
                Debug.LogWarning("[AR-NAV] ARCore not available — activating Sensor AR Fallback...");

                ARSession session = FindAnyObjectByType<ARSession>();
                if (session != null) session.enabled = false;

                ARCameraBackground bgComp = FindAnyObjectByType<ARCameraBackground>();
                if (bgComp != null) bgComp.enabled = false;

                SensorARFallback fallback = FindAnyObjectByType<SensorARFallback>();
                if (fallback == null) fallback = gameObject.AddComponent<SensorARFallback>();
                fallback.ActivateFallback();

                UpdateRecordingStatus("📡 Sensor AR Mode (Gyro + Pedometer). Ready to record!");
                yield break;
            }

            // ARCore path
            ARCameraBackground bg = FindAnyObjectByType<ARCameraBackground>();
            if (bg != null && !bg.enabled) bg.enabled = true;

            ARSession activeSession = FindAnyObjectByType<ARSession>();
            if (activeSession != null)
            {
                activeSession.enabled = false;
                yield return null;
                activeSession.enabled = true;
                activeSession.Reset();
            }

            Debug.Log($"[AR-NAV] ARCore active: {ARSession.state}");
        }
    }
}
