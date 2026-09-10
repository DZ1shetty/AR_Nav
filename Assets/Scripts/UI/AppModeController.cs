using System.Collections;
using System.Collections.Generic;
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
using ARNav.Backend;

namespace ARNav.UI
{
    /// <summary>
    /// Master UI controller for AR-NAV.
    ///
    /// Responsibilities:
    ///   • Camera permission request + ARCore availability check on startup.
    ///   • Activates SensorARFallback on devices without ARCore.
    ///   • Manages three UI panels: ModeSelect → Recording → Navigation.
    ///   • Correct recording flow: open panel → fill name → tap Start → walk → tap Finish.
    ///   • Populates start-location and destination dropdowns from the saved graph.
    ///   • Post-navigation feedback thumbs up/down adjusts edge confidence scores.
    ///
    /// KEY FIXES vs old version:
    ///   • Recording does NOT start automatically when the panel opens.
    ///   • startRecordButton is correctly wired.
    ///   • feedbackPanel IS auto-located by TryFindPanel.
    ///   • EnsureStartNodeDropdown builds a complete TMP_Dropdown with required template.
    ///   • QR stub clearly tells the user it's not yet implemented (no silent timeout).
    ///   • buildingId and floor exposed so multi-building recording is possible.
    /// </summary>
    public class AppModeController : MonoBehaviour
    {
        // ── Inspector ─────────────────────────────────────────────────────────────
        [Header("Subsystems (auto-found if null)")]
        [SerializeField] private PathRecorder      recorder;
        [SerializeField] private PathNavigator     navigator;
        [SerializeField] private SupabaseSyncService supabase;

        [Header("UI Panels (auto-found if null)")]
        [SerializeField] private GameObject modeSelectPanel;
        [SerializeField] private GameObject recordingPanel;
        [SerializeField] private GameObject navigationPanel;
        [SerializeField] private GameObject feedbackPanel;

        [Header("Recording UI")]
        [SerializeField] private TMP_Text       recordingStatusText;
        [SerializeField] private TMP_InputField startPointInput;
        [SerializeField] private TMP_InputField endPointInput;
        [SerializeField] private Button         startRecordButton;
        [SerializeField] private Button         stopRecordButton;

        [Header("Navigation UI")]
        [SerializeField] private TMP_Text     navInstructionText;
        [SerializeField] private TMP_Dropdown destinationDropdown;
        [SerializeField] private TMP_Dropdown startNodeDropdown;
        [SerializeField] private Button       startNavButton;
        [SerializeField] private Button       stopNavButton;

        [Header("Feedback UI")]
        [SerializeField] private Button thumbsUpButton;
        [SerializeField] private Button thumbsDownButton;

        [Header("Recording Settings")]
        [SerializeField] private string buildingId = "main_building";
        [SerializeField] private int    floor      = 1;

        // ── Private state ─────────────────────────────────────────────────────────
        private BuildingGraph _graph;
        private List<string>  _nodeIds   = new List<string>();   // parallel to dropdown options
        private string        _qrNodeId  = null;                 // from QR scan (if implemented)

        // ── Lifecycle ─────────────────────────────────────────────────────────────

        private void Awake()
        {
            // Apply a responsive canvas scaler
            CanvasScaler cs = GetComponentInParent<CanvasScaler>()
                           ?? FindAnyObjectByType<CanvasScaler>();
            if (cs != null)
            {
                cs.uiScaleMode         = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                cs.referenceResolution = new Vector2(1080, 1920);
                cs.matchWidthOrHeight  = 0.5f;
            }
        }

        private void Start()
        {
            // Auto-find subsystems
            if (recorder  == null) recorder  = FindAnyObjectByType<PathRecorder>();
            if (navigator == null) navigator = FindAnyObjectByType<PathNavigator>();
            if (supabase  == null) supabase  = FindAnyObjectByType<SupabaseSyncService>();

            // Wire events
            if (recorder != null)
            {
                recorder.OnStatusChanged += SetRecordingStatus;
                recorder.OnProgress      += (pts, dist) =>
                    SetRecordingStatus($"🔴 {pts} pts | {dist:F1} m | {recorder.AnchorCount} anchors");
            }

            if (navigator != null)
            {
                navigator.OnInstructionChanged += SetNavInstruction;
                navigator.OnDestinationReached += OnArrived;
            }

            if (supabase != null)
            {
                supabase.OnUploadCompleted += msg => Debug.Log($"[Supabase] {msg}");
                supabase.OnSyncFailed      += err => Debug.LogWarning($"[Supabase] {err}");
            }

            WireAllPanels();
            StartCoroutine(InitAR());
        }

        // ── AR Init ───────────────────────────────────────────────────────────────

        private IEnumerator InitAR()
        {
#if UNITY_ANDROID
            if (!Permission.HasUserAuthorizedPermission(Permission.Camera))
            {
                Permission.RequestUserPermission(Permission.Camera);
                float t = 8f;
                while (!Permission.HasUserAuthorizedPermission(Permission.Camera) && t > 0f)
                { t -= 0.2f; yield return new WaitForSeconds(0.2f); }
            }
#endif

            // Wait for ARSession to report its state
            float waitLimit = 4f;
            while ((ARSession.state == ARSessionState.None ||
                    ARSession.state == ARSessionState.CheckingAvailability) && waitLimit > 0f)
            {
                yield return ARSession.CheckAvailability();
                waitLimit -= 0.1f;
                yield return new WaitForSeconds(0.1f);
            }

            bool needsFallback =
                ARSession.state == ARSessionState.Unsupported  ||
                ARSession.state == ARSessionState.NeedsInstall ||
                ARSession.state == ARSessionState.None;

            if (needsFallback)
            {
                Debug.LogWarning("[AR-NAV] ARCore unavailable — activating sensor fallback.");
                ARSession arSession = FindAnyObjectByType<ARSession>();
                if (arSession != null) arSession.enabled = false;

                ARCameraBackground bg = FindAnyObjectByType<ARCameraBackground>();
                if (bg != null) bg.enabled = false;

                SensorARFallback fallback = FindAnyObjectByType<SensorARFallback>()
                    ?? gameObject.AddComponent<SensorARFallback>();
                fallback.ActivateFallback();
            }

            ShowModeSelect();
        }

        // ── Panel wiring ──────────────────────────────────────────────────────────

        private void WireAllPanels()
        {
            Canvas canvas = GetComponentInParent<Canvas>()
                         ?? FindAnyObjectByType<Canvas>();

            // Auto-locate all four panels
            TryFindPanel(ref modeSelectPanel,  canvas, "ModeSelectPanel");
            TryFindPanel(ref recordingPanel,   canvas, "RecordingPanel");
            TryFindPanel(ref navigationPanel,  canvas, "NavigationPanel");
            TryFindPanel(ref feedbackPanel,    canvas, "FeedbackPanel");   // FIX: was missing

            // ── ModeSelect ─────────────────────────────────────────────────────────
            WireButtons(modeSelectPanel, (btn, label) =>
            {
                if (label.Contains("record"))
                    btn.onClick.AddListener(OpenRecordPanel);
                else if (label.Contains("navigat"))
                    btn.onClick.AddListener(OpenNavPanel);
            });

            // ── Recording panel ────────────────────────────────────────────────────
            // FIX: startRecord button wired explicitly; recording does NOT auto-start
            WireButtons(recordingPanel, (btn, label) =>
            {
                if (label.Contains("start") && !label.Contains("nav"))
                {
                    startRecordButton = btn;
                    btn.onClick.AddListener(OnStartRecordClicked);
                }
                else if (label.Contains("finish") || label.Contains("save") || label.Contains("stop"))
                {
                    stopRecordButton = btn;
                    btn.onClick.AddListener(OnStopRecordClicked);
                }
                else if (label.Contains("cancel") || label.Contains("back"))
                    btn.onClick.AddListener(CancelRecordingAndReturn);
            });

            if (recordingStatusText == null && recordingPanel != null)
                recordingStatusText = recordingPanel.GetComponentInChildren<TMP_Text>(true);

            // ── Navigation panel ───────────────────────────────────────────────────
            WireButtons(navigationPanel, (btn, label) =>
            {
                if (label.Contains("go") || (label.Contains("start") && label.Contains("nav")))
                {
                    startNavButton = btn;
                    btn.onClick.AddListener(OnStartNavClicked);
                }
                else if (label.Contains("stop") || label.Contains("back") || label.Contains("cancel"))
                {
                    stopNavButton = btn;
                    btn.onClick.AddListener(OnStopNavClicked);
                }
            });

            if (navInstructionText == null && navigationPanel != null)
                navInstructionText = navigationPanel.GetComponentInChildren<TMP_Text>(true);

            if (navigationPanel != null)
            {
                var dropdowns = navigationPanel.GetComponentsInChildren<TMP_Dropdown>(true);
                if (dropdowns.Length >= 1 && destinationDropdown == null)
                    destinationDropdown = dropdowns[0];
                if (dropdowns.Length >= 2 && startNodeDropdown == null)
                    startNodeDropdown   = dropdowns[1];
            }

            // ── Feedback panel ─────────────────────────────────────────────────────
            if (thumbsUpButton   != null) thumbsUpButton.onClick.AddListener(()   => SubmitRating(1f));
            if (thumbsDownButton != null) thumbsDownButton.onClick.AddListener(() => SubmitRating(-0.5f));
        }

        // ── Panel visibility ──────────────────────────────────────────────────────

        public void ShowModeSelect()
        {
            SetPanels(modeSelect: true);
            RefreshGraph();
        }

        public void OpenRecordPanel()
        {
            SetPanels(recording: true);
            // FIX: do NOT call StartRecording here — user must fill name and tap Start
            SetRecordingStatus("Enter start location name, then tap Start Recording.");
        }

        public void OpenNavPanel()
        {
            SetPanels(navigation: true);
            RefreshGraph();

            if (navInstructionText != null)
            {
                navInstructionText.text = _graph != null && _graph.nodes.Count >= 2
                    ? "Select your location and destination, then tap Navigate."
                    : "No routes recorded yet. Go back and record a route first.";
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

        // ── Recording flow ────────────────────────────────────────────────────────

        /// <summary>Called when user taps "Start Recording" (not on panel open).</summary>
        public void OnStartRecordClicked()
        {
            if (recorder == null) return;
            string name = (startPointInput != null && !string.IsNullOrEmpty(startPointInput.text))
                ? startPointInput.text.Trim()
                : "Location A";
            recorder.StartRecording(name, buildingId, floor);

            // Disable start button so it can't be tapped twice
            if (startRecordButton != null) startRecordButton.interactable = false;
            if (stopRecordButton  != null) stopRecordButton.interactable  = true;
        }

        public void OnStopRecordClicked()
        {
            if (recorder == null) return;
            string endName = (endPointInput != null && !string.IsNullOrEmpty(endPointInput.text))
                ? endPointInput.text.Trim()
                : "Location B";

            BuildingGraph saved = recorder.StopAndSave(endName);

            if (supabase != null && saved != null)
                supabase.UploadSession(recorder.SessionNodes, recorder.SessionEdges, buildingId);

            // Re-enable buttons for next recording
            if (startRecordButton != null) startRecordButton.interactable = true;
            if (stopRecordButton  != null) stopRecordButton.interactable  = false;

            ShowModeSelect();
        }

        public void CancelRecordingAndReturn()
        {
            recorder?.CancelRecording();
            if (startRecordButton != null) startRecordButton.interactable = true;
            if (stopRecordButton  != null) stopRecordButton.interactable  = false;
            ShowModeSelect();
        }

        // ── Navigation flow ───────────────────────────────────────────────────────

        private void RefreshGraph()
        {
            _graph = BuildingGraph.LoadFromFile(BuildingGraph.DefaultPath);
            _nodeIds.Clear();
            var options = new List<string>();

            if (_graph != null)
            {
                foreach (var node in _graph.nodes)
                {
                    options.Add(node.name);
                    _nodeIds.Add(node.id);
                }
            }

            if (options.Count == 0)
                options.Add("(No routes recorded)");

            // Destination dropdown
            if (destinationDropdown != null)
            {
                destinationDropdown.ClearOptions();
                destinationDropdown.AddOptions(options);
                if (destinationDropdown.options.Count > 1)
                    destinationDropdown.value = destinationDropdown.options.Count - 1;
            }

            // Start-location dropdown
            EnsureStartDropdown();
            if (startNodeDropdown != null)
            {
                startNodeDropdown.ClearOptions();
                startNodeDropdown.AddOptions(new List<string>(options));
                startNodeDropdown.value = 0;

                if (!string.IsNullOrEmpty(_qrNodeId))
                {
                    int qrIdx = _nodeIds.IndexOf(_qrNodeId);
                    if (qrIdx >= 0) startNodeDropdown.value = qrIdx;
                }
            }
        }

        private void OnStartNavClicked()
        {
            if (_graph == null || _nodeIds.Count < 2)
            {
                SetNavInstruction("Need at least 2 recorded nodes to navigate.");
                return;
            }

            int destIdx = destinationDropdown != null ? destinationDropdown.value : _nodeIds.Count - 1;
            if (destIdx < 0 || destIdx >= _nodeIds.Count) return;
            string destId = _nodeIds[destIdx];

            string startId;
            if (!string.IsNullOrEmpty(_qrNodeId))
                startId = _qrNodeId;
            else if (startNodeDropdown != null && startNodeDropdown.value < _nodeIds.Count)
                startId = _nodeIds[startNodeDropdown.value];
            else
                startId = _nodeIds[0];

            if (startId == destId)
            {
                SetNavInstruction("Start and destination are the same. Pick a different destination.");
                return;
            }

            bool ok = navigator != null && navigator.StartNavigation(startId, destId);
            if (!ok) SetNavInstruction("No path found. Try different start/destination.");
        }

        private void OnStopNavClicked()
        {
            navigator?.StopNavigation();
            ShowModeSelect();
        }

        private void OnArrived()
        {
            // Show feedback panel so user can rate the route
            SetPanels(feedback: true);
        }

        // ── Feedback ──────────────────────────────────────────────────────────────

        private void SubmitRating(float delta)
        {
            // Update confidence score of all edges in the last used route
            BuildingGraph g = BuildingGraph.LoadFromFile(BuildingGraph.DefaultPath);
            if (g.edges.Count > 0)
            {
                // Apply delta to every edge navigated (approximate: last N edges)
                foreach (var edge in g.edges)
                {
                    edge.confidenceScore = Mathf.Clamp(edge.confidenceScore + delta * 0.1f, 0.1f, 2f);
                    edge.contributingWalkCount++;
                }
                g.SaveToFile(BuildingGraph.DefaultPath);
            }
            SetPanels(modeSelect: true);
            RefreshGraph();
        }

        // ── QR Localization (placeholder — needs ZXing) ───────────────────────────

        /// <summary>
        /// Scan a QR code to automatically set the start location.
        /// Requires ZXing.Net.Bindings.Unity or similar package.
        /// Currently shows a clear message instead of silently timing out.
        /// </summary>
        public void ScanLocationQR()
        {
            // When ZXing is available, replace this with the actual scan logic.
            SetNavInstruction("QR scanning requires ZXing package.\n" +
                              "Add ZXing via UPM or manually select your start location.");
            Debug.Log("[AR-NAV] QR scan requested — ZXing not yet installed.");
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        private void TryFindPanel(ref GameObject field, Canvas canvas, string panelName)
        {
            if (field != null || canvas == null) return;
            Transform t = canvas.transform.Find(panelName);
            if (t != null) field = t.gameObject;
        }

        private void WireButtons(GameObject panel, System.Action<Button, string> handler)
        {
            if (panel == null) return;
            foreach (Button btn in panel.GetComponentsInChildren<Button>(true))
            {
                string label = btn.GetComponentInChildren<TMP_Text>()?.text?.ToLower()
                            ?? btn.name.ToLower();
                btn.onClick.RemoveAllListeners();
                handler(btn, label);
            }
        }

        /// <summary>
        /// Creates a functional TMP_Dropdown for "You are at:" with a proper template
        /// hierarchy so the dropdown list actually opens at runtime.
        /// </summary>
        private void EnsureStartDropdown()
        {
            if (startNodeDropdown != null || navigationPanel == null) return;
            if (_graph == null || _graph.nodes.Count < 2) return;

            // Find existing destination dropdown to position relative to it
            RectTransform destRT = destinationDropdown?.GetComponent<RectTransform>();
            float yPos = destRT != null ? destRT.anchoredPosition.y + 150f : 200f;

            // ── Label ──────────────────────────────────────────────────────────────
            GameObject labelGO = new GameObject("StartNodeLabel");
            labelGO.transform.SetParent(navigationPanel.transform, false);
            var label = labelGO.AddComponent<TextMeshProUGUI>();
            label.text      = "📍 You are at:";
            label.fontSize  = 26;
            label.color     = new Color(0.7f, 0.95f, 1f);
            label.alignment = TextAlignmentOptions.Center;
            var labelRT = labelGO.GetComponent<RectTransform>();
            labelRT.anchorMin = labelRT.anchorMax = new Vector2(0.5f, 0.5f);
            labelRT.pivot     = new Vector2(0.5f, 0.5f);
            labelRT.anchoredPosition = new Vector2(0f, yPos + 65f);
            labelRT.sizeDelta        = new Vector2(600f, 50f);

            // ── Dropdown root ──────────────────────────────────────────────────────
            // We duplicate the destination dropdown's structure if available —
            // that guarantees a working template. Otherwise create from scratch.
            if (destinationDropdown != null)
            {
                GameObject clone = Instantiate(destinationDropdown.gameObject, navigationPanel.transform);
                clone.name = "StartNodeDropdown";
                startNodeDropdown = clone.GetComponent<TMP_Dropdown>();
                var rt = clone.GetComponent<RectTransform>();
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot     = new Vector2(0.5f, 0.5f);
                rt.anchoredPosition = new Vector2(0f, yPos);
                rt.sizeDelta        = new Vector2(650f, 100f);
            }
            // If there's no destination dropdown to clone we skip dynamic creation
            // (the scene bootstrapper will have provided one).
        }

        private void SetRecordingStatus(string msg)
        {
            if (recordingStatusText != null) recordingStatusText.text = msg;
        }

        private void SetNavInstruction(string msg)
        {
            if (navInstructionText != null) navInstructionText.text = msg;
        }
    }
}
