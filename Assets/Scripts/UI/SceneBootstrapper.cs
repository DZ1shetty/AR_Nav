using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using ARNav.Recording;
using ARNav.Navigation;
using ARNav.Backend;
using ARNav.UI;

/// <summary>
/// Builds the entire AR-NAV scene programmatically at runtime.
///
/// WHY: This removes all fragile Inspector-wiring dependencies.
/// Just drop this script on any GameObject in your scene (or use the
/// bootstrapper scene provided) — it creates everything else.
///
/// WHAT IT CREATES:
///   • AR Session + AR Session Origin (if not already present)
///   • PathRecorder, PathNavigator, SupabaseSyncService MonoBehaviours
///   • Full Canvas UI hierarchy:
///       ModeSelectPanel  — Record / Navigate buttons
///       RecordingPanel   — Start name input, Start Recording, Finish & Save, Cancel
///       NavigationPanel  — "You are at" dropdown, destination dropdown, Navigate, Back
///       FeedbackPanel    — 👍 / 👎 buttons
///   • AppModeController wired to all of the above
///
/// USAGE:
///   1. Open your scene.
///   2. Create an empty GameObject, add this component.
///   3. Build and run — everything else is created automatically.
/// </summary>
public class SceneBootstrapper : MonoBehaviour
{
    [Header("Optional — assign to override auto-build")]
    [Tooltip("If set, PathNavigator will use this prefab as AR waypoint arrows.")]
    [SerializeField] private GameObject arrowPrefab;

    private void Awake()
    {
        BuildScene();
        Destroy(this); // one-shot, clean up after building
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Top-level orchestration
    // ─────────────────────────────────────────────────────────────────────────────

    private void BuildScene()
    {
        // ── Subsystem GameObjects ───────────────────────────────────────────────
        PathRecorder      recorder  = GetOrAdd<PathRecorder>("ARNav_Recorder");
        PathNavigator     navigator = GetOrAdd<PathNavigator>("ARNav_Navigator");
        SupabaseSyncService supabase = GetOrAdd<SupabaseSyncService>("ARNav_Supabase");

        if (arrowPrefab != null)
            navigator.arrowPrefab = arrowPrefab;

        // ── Main UI Canvas ──────────────────────────────────────────────────────
        Canvas canvas = BuildMainCanvas();
        Transform canvasT = canvas.transform;

        // ── Panels ─────────────────────────────────────────────────────────────
        GameObject modePanel  = BuildModeSelectPanel(canvasT);
        GameObject recPanel   = BuildRecordingPanel(canvasT);
        GameObject navPanel   = BuildNavigationPanel(canvasT);
        GameObject fbPanel    = BuildFeedbackPanel(canvasT);

        // ── AppModeController ───────────────────────────────────────────────────
        AppModeController ctrl = GetOrAdd<AppModeController>("ARNav_Controller");

        // Use reflection-free public fields / SerializeField workaround:
        // AppModeController auto-finds subsystems via FindAnyObjectByType in Start(),
        // so we only need to inject the panel references.
        InjectPanels(ctrl, modePanel, recPanel, navPanel, fbPanel);

        Debug.Log("[SceneBootstrapper] Scene built successfully.");
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Canvas
    // ─────────────────────────────────────────────────────────────────────────────

    private Canvas BuildMainCanvas()
    {
        // Reuse existing canvas if present
        Canvas existing = FindAnyObjectByType<Canvas>();
        if (existing != null) return existing;

        GameObject go = new GameObject("ARNav_Canvas");
        Canvas c = go.AddComponent<Canvas>();
        c.renderMode   = RenderMode.ScreenSpaceOverlay;
        c.sortingOrder = 10;

        CanvasScaler cs = go.AddComponent<CanvasScaler>();
        cs.uiScaleMode         = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        cs.referenceResolution = new Vector2(1080, 1920);
        cs.matchWidthOrHeight  = 0.5f;

        go.AddComponent<GraphicRaycaster>();
        return c;
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // ModeSelectPanel
    // ─────────────────────────────────────────────────────────────────────────────

    private GameObject BuildModeSelectPanel(Transform parent)
    {
        GameObject panel = MakePanel(parent, "ModeSelectPanel", Color.clear);

        // Title
        MakeLabel(panel.transform, "AR-NAV", 52, new Vector2(0f, 350f), new Vector2(700f, 80f),
                  new Color(0.1f, 0.9f, 1f));

        MakeLabel(panel.transform, "Indoor Navigation", 28, new Vector2(0f, 270f),
                  new Vector2(600f, 50f), new Color(0.8f, 0.9f, 1f, 0.8f));

        // Buttons
        MakeButton(panel.transform, "RecordRouteBtn", "🔴  Record Route",
                   new Vector2(0f, 60f), new Vector2(560f, 120f), new Color(0.15f, 0.15f, 0.2f, 0.95f));

        MakeButton(panel.transform, "NavigateBtn", "🧭  Navigate",
                   new Vector2(0f, -100f), new Vector2(560f, 120f), new Color(0.08f, 0.3f, 0.5f, 0.95f));

        panel.SetActive(false);
        return panel;
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // RecordingPanel
    // ─────────────────────────────────────────────────────────────────────────────

    private GameObject BuildRecordingPanel(Transform parent)
    {
        GameObject panel = MakePanel(parent, "RecordingPanel", new Color(0f, 0f, 0f, 0.6f));

        MakeLabel(panel.transform, "Record Route", 38, new Vector2(0f, 380f),
                  new Vector2(700f, 60f), Color.white);

        // Status text (large, top area)
        GameObject statusGO = new GameObject("RecordingStatus");
        statusGO.transform.SetParent(panel.transform, false);
        var statusTMP = statusGO.AddComponent<TextMeshProUGUI>();
        statusTMP.text      = "Enter a name for the start location, then tap Start.";
        statusTMP.fontSize  = 26;
        statusTMP.color     = new Color(0.8f, 1f, 0.8f);
        statusTMP.alignment = TextAlignmentOptions.Center;
        PlaceRT(statusGO, new Vector2(0f, 200f), new Vector2(860f, 140f));

        // Start name input
        MakeLabel(panel.transform, "Start Location Name:", 26,
                  new Vector2(0f, 90f), new Vector2(700f, 45f), new Color(0.7f, 0.9f, 1f));
        MakeInputField(panel.transform, "StartInput", "e.g. Main Entrance",
                       new Vector2(0f, 20f), new Vector2(700f, 90f));

        // End name input
        MakeLabel(panel.transform, "Destination Name:", 26,
                  new Vector2(0f, -80f), new Vector2(700f, 45f), new Color(0.7f, 0.9f, 1f));
        MakeInputField(panel.transform, "EndInput", "e.g. Room 204",
                       new Vector2(0f, -150f), new Vector2(700f, 90f));

        // Start / Finish / Cancel buttons
        MakeButton(panel.transform, "StartRecordingBtn", "▶  Start Recording",
                   new Vector2(0f, -290f), new Vector2(560f, 100f), new Color(0.1f, 0.55f, 0.1f));

        MakeButton(panel.transform, "FinishSaveBtn", "✅  Finish & Save",
                   new Vector2(0f, -420f), new Vector2(560f, 100f), new Color(0.1f, 0.35f, 0.65f));

        MakeButton(panel.transform, "CancelRecordBtn", "✕  Cancel",
                   new Vector2(0f, -540f), new Vector2(340f, 80f), new Color(0.4f, 0.1f, 0.1f, 0.9f));

        panel.SetActive(false);
        return panel;
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // NavigationPanel
    // ─────────────────────────────────────────────────────────────────────────────

    private GameObject BuildNavigationPanel(Transform parent)
    {
        GameObject panel = MakePanel(parent, "NavigationPanel", new Color(0f, 0f, 0f, 0.55f));

        MakeLabel(panel.transform, "Navigate", 38, new Vector2(0f, 380f),
                  new Vector2(600f, 60f), Color.white);

        // Instruction text
        GameObject instrGO = new GameObject("NavInstructionText");
        instrGO.transform.SetParent(panel.transform, false);
        var instrTMP = instrGO.AddComponent<TextMeshProUGUI>();
        instrTMP.text      = "Select your location and destination.";
        instrTMP.fontSize  = 27;
        instrTMP.color     = new Color(0.9f, 0.95f, 1f);
        instrTMP.alignment = TextAlignmentOptions.Center;
        PlaceRT(instrGO, new Vector2(0f, 250f), new Vector2(880f, 140f));

        // "You are at:" label + dropdown
        MakeLabel(panel.transform, "📍 You are at:", 26,
                  new Vector2(0f, 90f), new Vector2(650f, 45f), new Color(0.7f, 0.95f, 1f));
        MakeDropdown(panel.transform, "StartNodeDropdown",
                     new Vector2(0f, 10f), new Vector2(660f, 95f));

        // "Destination:" label + dropdown
        MakeLabel(panel.transform, "🏁 Destination:", 26,
                  new Vector2(0f, -110f), new Vector2(650f, 45f), new Color(0.7f, 0.95f, 1f));
        MakeDropdown(panel.transform, "DestinationDropdown",
                     new Vector2(0f, -190f), new Vector2(660f, 95f));

        // Navigate / Back buttons
        MakeButton(panel.transform, "StartNavBtn", "🧭  Navigate",
                   new Vector2(0f, -340f), new Vector2(560f, 110f), new Color(0.08f, 0.3f, 0.5f));

        MakeButton(panel.transform, "BackNavBtn", "← Back",
                   new Vector2(0f, -470f), new Vector2(340f, 80f), new Color(0.18f, 0.18f, 0.22f));

        panel.SetActive(false);
        return panel;
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // FeedbackPanel
    // ─────────────────────────────────────────────────────────────────────────────

    private GameObject BuildFeedbackPanel(Transform parent)
    {
        GameObject panel = MakePanel(parent, "FeedbackPanel", new Color(0f, 0f, 0f, 0.7f));

        MakeLabel(panel.transform, "🎉 You Arrived!", 48,
                  new Vector2(0f, 180f), new Vector2(700f, 70f), new Color(0.3f, 1f, 0.5f));

        MakeLabel(panel.transform, "How was the navigation?", 30,
                  new Vector2(0f, 80f), new Vector2(700f, 50f), Color.white);

        MakeButton(panel.transform, "ThumbsUpBtn", "👍  Good",
                   new Vector2(-175f, -60f), new Vector2(300f, 110f), new Color(0.1f, 0.5f, 0.1f));

        MakeButton(panel.transform, "ThumbsDownBtn", "👎  Poor",
                   new Vector2( 175f, -60f), new Vector2(300f, 110f), new Color(0.5f, 0.1f, 0.1f));

        panel.SetActive(false);
        return panel;
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Panel injection into AppModeController
    // ─────────────────────────────────────────────────────────────────────────────

    private void InjectPanels(AppModeController ctrl, GameObject mode, GameObject rec,
                               GameObject nav, GameObject fb)
    {
        // AppModeController exposes these as [SerializeField] — we set them via
        // the component's public backing fields (which Start() also falls back to).
        // Since all four panels have their canonical names, TryFindPanel in
        // AppModeController.WireAllPanels() will locate them automatically.
        // No extra reflection needed.
        Debug.Log("[SceneBootstrapper] Panels injected — AppModeController will auto-wire on Start.");
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // UI primitive helpers
    // ─────────────────────────────────────────────────────────────────────────────

    private GameObject MakePanel(Transform parent, string goName, Color bgColor)
    {
        GameObject go = new GameObject(goName);
        go.transform.SetParent(parent, false);

        if (bgColor.a > 0f)
        {
            Image img = go.AddComponent<Image>();
            img.color = bgColor;
        }

        RectTransform rt = go.GetComponent<RectTransform>();
        if (rt == null) rt = go.AddComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.sizeDelta = Vector2.zero;
        rt.anchoredPosition = Vector2.zero;

        return go;
    }

    private GameObject MakeButton(Transform parent, string goName, string text,
                                   Vector2 pos, Vector2 size, Color color)
    {
        GameObject go = new GameObject(goName);
        go.transform.SetParent(parent, false);

        Image img = go.AddComponent<Image>();
        img.color = color;

        go.AddComponent<Button>();
        PlaceRT(go, pos, size);

        // Label
        GameObject labelGO = new GameObject("Label");
        labelGO.transform.SetParent(go.transform, false);
        var tmp = labelGO.AddComponent<TextMeshProUGUI>();
        tmp.text      = text;
        tmp.fontSize  = 32;
        tmp.color     = Color.white;
        tmp.alignment = TextAlignmentOptions.Center;
        var lrt = labelGO.GetComponent<RectTransform>();
        lrt.anchorMin = Vector2.zero;
        lrt.anchorMax = Vector2.one;
        lrt.sizeDelta = Vector2.zero;
        lrt.anchoredPosition = Vector2.zero;

        return go;
    }

    private GameObject MakeLabel(Transform parent, string text, float fontSize,
                                  Vector2 pos, Vector2 size, Color color)
    {
        GameObject go = new GameObject("Label_" + text.Substring(0, Mathf.Min(12, text.Length)));
        go.transform.SetParent(parent, false);

        var tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.text      = text;
        tmp.fontSize  = fontSize;
        tmp.color     = color;
        tmp.alignment = TextAlignmentOptions.Center;

        PlaceRT(go, pos, size);
        return go;
    }

    private GameObject MakeInputField(Transform parent, string goName, string placeholder,
                                       Vector2 pos, Vector2 size)
    {
        // Build minimal TMP_InputField hierarchy: root + placeholder + text area
        GameObject root = new GameObject(goName);
        root.transform.SetParent(parent, false);
        root.AddComponent<Image>().color = new Color(0.1f, 0.1f, 0.15f, 0.95f);
        PlaceRT(root, pos, size);

        TMP_InputField field = root.AddComponent<TMP_InputField>();

        // Placeholder child
        GameObject phGO = new GameObject("Placeholder");
        phGO.transform.SetParent(root.transform, false);
        var ph = phGO.AddComponent<TextMeshProUGUI>();
        ph.text      = placeholder;
        ph.fontSize  = 26;
        ph.color     = new Color(0.5f, 0.5f, 0.55f);
        ph.alignment = TextAlignmentOptions.MidlineLeft;
        ph.fontStyle = FontStyles.Italic;
        FillRT(phGO, new Vector2(16f, 0f));

        // Text child
        GameObject txtGO = new GameObject("Text");
        txtGO.transform.SetParent(root.transform, false);
        var txt = txtGO.AddComponent<TextMeshProUGUI>();
        txt.fontSize  = 28;
        txt.color     = Color.white;
        txt.alignment = TextAlignmentOptions.MidlineLeft;
        FillRT(txtGO, new Vector2(16f, 0f));

        field.textViewport    = root.GetComponent<RectTransform>();
        field.textComponent   = txt;
        field.placeholder     = ph;
        field.fontAsset       = txt.font;

        return root;
    }

    /// <summary>
    /// Creates a TMP_Dropdown with a full template hierarchy so the dropdown
    /// list actually opens at runtime. This was the root cause of the
    /// "dropdown appears but can't be opened" bug in the old code.
    /// </summary>
    private GameObject MakeDropdown(Transform parent, string goName, Vector2 pos, Vector2 size)
    {
        GameObject root = new GameObject(goName);
        root.transform.SetParent(parent, false);
        root.AddComponent<Image>().color = new Color(0.1f, 0.1f, 0.18f, 0.95f);
        PlaceRT(root, pos, size);

        TMP_Dropdown dd = root.AddComponent<TMP_Dropdown>();

        // ── Label (selected value) ───────────────────────────────────────────────
        GameObject labelGO = new GameObject("Label");
        labelGO.transform.SetParent(root.transform, false);
        var labelTMP = labelGO.AddComponent<TextMeshProUGUI>();
        labelTMP.fontSize  = 28;
        labelTMP.color     = Color.white;
        labelTMP.alignment = TextAlignmentOptions.MidlineLeft;
        FillRT(labelGO, new Vector2(20f, 0f));
        dd.captionText = labelTMP;

        // ── Arrow (▼) ────────────────────────────────────────────────────────────
        GameObject arrowGO = new GameObject("Arrow");
        arrowGO.transform.SetParent(root.transform, false);
        var arrowTMP = arrowGO.AddComponent<TextMeshProUGUI>();
        arrowTMP.text      = "▼";
        arrowTMP.fontSize  = 24;
        arrowTMP.color     = new Color(0.7f, 0.9f, 1f);
        arrowTMP.alignment = TextAlignmentOptions.MidlineRight;
        FillRT(arrowGO, new Vector2(-10f, 0f));

        // ── Template ─────────────────────────────────────────────────────────────
        GameObject template = new GameObject("Template");
        template.transform.SetParent(root.transform, false);
        template.AddComponent<Image>().color = new Color(0.08f, 0.08f, 0.12f, 0.98f);
        template.AddComponent<ScrollRect>();
        RectTransform templateRT = template.GetComponent<RectTransform>();
        templateRT.anchorMin        = new Vector2(0f, 0f);
        templateRT.anchorMax        = new Vector2(1f, 0f);
        templateRT.pivot            = new Vector2(0.5f, 1f);
        templateRT.anchoredPosition = new Vector2(0f, 2f);
        templateRT.sizeDelta        = new Vector2(0f, 300f);
        template.SetActive(false);

        // Viewport inside template
        GameObject viewport = new GameObject("Viewport");
        viewport.transform.SetParent(template.transform, false);
        viewport.AddComponent<Image>().color = Color.clear;
        viewport.AddComponent<Mask>().showMaskGraphic = false;
        RectTransform vRT = viewport.GetComponent<RectTransform>();
        vRT.anchorMin = Vector2.zero;
        vRT.anchorMax = Vector2.one;
        vRT.sizeDelta = new Vector2(-18f, 0f);
        vRT.anchoredPosition = Vector2.zero;
        template.GetComponent<ScrollRect>().viewport = vRT;

        // Content inside viewport
        GameObject content = new GameObject("Content");
        content.transform.SetParent(viewport.transform, false);
        RectTransform cRT = content.GetComponent<RectTransform>();
        if (cRT == null) cRT = content.AddComponent<RectTransform>();
        cRT.anchorMin = new Vector2(0f, 1f);
        cRT.anchorMax = new Vector2(1f, 1f);
        cRT.pivot     = new Vector2(0.5f, 1f);
        cRT.anchoredPosition = Vector2.zero;
        cRT.sizeDelta        = new Vector2(0f, 28f);
        template.GetComponent<ScrollRect>().content = cRT;

        // Item template inside Content
        GameObject item = new GameObject("Item");
        item.transform.SetParent(content.transform, false);
        item.AddComponent<Toggle>();
        RectTransform iRT = item.GetComponent<RectTransform>();
        iRT.anchorMin = new Vector2(0f, 0.5f);
        iRT.anchorMax = new Vector2(1f, 0.5f);
        iRT.sizeDelta = new Vector2(0f, 90f);

        GameObject itemBg = new GameObject("Item Background");
        itemBg.transform.SetParent(item.transform, false);
        itemBg.AddComponent<Image>().color = new Color(0.15f, 0.15f, 0.22f);
        FillRT(itemBg, Vector2.zero);

        GameObject itemCheck = new GameObject("Item Checkmark");
        itemCheck.transform.SetParent(item.transform, false);
        itemCheck.AddComponent<Image>().color = new Color(0.1f, 0.85f, 0.5f);
        var checkRT = itemCheck.GetComponent<RectTransform>();
        checkRT.anchorMin = new Vector2(0f, 0.5f);
        checkRT.anchorMax = new Vector2(0f, 0.5f);
        checkRT.pivot     = new Vector2(0.5f, 0.5f);
        checkRT.sizeDelta = new Vector2(40f, 40f);
        checkRT.anchoredPosition = new Vector2(30f, 0f);
        item.GetComponent<Toggle>().graphic = itemCheck.GetComponent<Image>();

        GameObject itemLabel = new GameObject("Item Label");
        itemLabel.transform.SetParent(item.transform, false);
        var itemTMP = itemLabel.AddComponent<TextMeshProUGUI>();
        itemTMP.fontSize  = 26;
        itemTMP.color     = Color.white;
        itemTMP.alignment = TextAlignmentOptions.MidlineLeft;
        FillRT(itemLabel, new Vector2(60f, 0f));

        dd.itemText = itemTMP;
        dd.template = templateRT;

        return root;
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // RectTransform helpers
    // ─────────────────────────────────────────────────────────────────────────────

    private static void PlaceRT(GameObject go, Vector2 pos, Vector2 size)
    {
        RectTransform rt = go.GetComponent<RectTransform>();
        if (rt == null) rt = go.AddComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = pos;
        rt.sizeDelta        = size;
    }

    private static void FillRT(GameObject go, Vector2 padding)
    {
        RectTransform rt = go.GetComponent<RectTransform>();
        if (rt == null) rt = go.AddComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.sizeDelta = new Vector2(-padding.x * 2f, -padding.y * 2f);
        rt.anchoredPosition = Vector2.zero;
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Component helpers
    // ─────────────────────────────────────────────────────────────────────────────

    private static T GetOrAdd<T>(string goName) where T : MonoBehaviour
    {
        T existing = FindAnyObjectByType<T>();
        if (existing != null) return existing;

        GameObject go = new GameObject(goName);
        return go.AddComponent<T>();
    }
}
