using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using ARNav.Graph;

namespace ARNav.Navigation
{
    /// <summary>
    /// Executes turn-by-turn indoor AR navigation.
    ///
    /// What it does:
    ///   • Loads the building graph and runs A* between two named nodes.
    ///   • Renders an animated ground-level LineRenderer path.
    ///   • Spawns glowing sphere / arrow-prefab waypoint markers.
    ///   • Shows an on-screen compass HUD arrow that always points toward
    ///     the next waypoint (rotates in 2-D like a minimap compass).
    ///   • Fires OnInstructionChanged with turn-by-turn text.
    ///   • Fires OnDestinationReached when the user arrives.
    ///
    /// KEY FIXES vs old version:
    ///   • AnimateIn() IS called on every spawned arrow prefab.
    ///   • ARArrow.RefreshBase() called after placement so bob Y is correct.
    ///   • PulsateMarkers coroutine respects ARArrow — only runs on fallback spheres.
    ///   • arCamera null-checked everywhere it is accessed mid-navigation.
    ///   • Line material uses Unlit/Color (always available, always scrolls correctly).
    ///   • Compass HUD built cleanly — no Image created-then-destroyed pattern.
    /// </summary>
    public class PathNavigator : MonoBehaviour
    {
        // ── Inspector ─────────────────────────────────────────────────────────────
        [Header("References")]
        [SerializeField] public Transform   arCamera;
        [SerializeField] private LineRenderer pathLine;
        [SerializeField] public  GameObject  arrowPrefab;  // optional — spheres used as fallback

        [Header("Path Line")]
        [SerializeField] private float pathLineWidth = 0.22f;
        [SerializeField] private Color pathColor     = new Color(0.1f, 0.85f, 1f, 0.85f);
        [SerializeField] private float floorYOffset  = -0.25f; // drop line below eye level

        [Header("Waypoint Markers")]
        [SerializeField] private float markerSize  = 0.35f;
        [SerializeField] private Color markerColor = new Color(0.1f, 0.9f, 1f, 0.9f);

        [Header("Navigation Logic")]
        [SerializeField] private float waypointRadius = 1.2f; // metres to count as "reached"

        // ── Events ────────────────────────────────────────────────────────────────
        public event Action<string> OnInstructionChanged;
        public event Action         OnDestinationReached;

        // ── Public state ──────────────────────────────────────────────────────────
        public bool IsNavigating => _navigating;

        // ── Private state ─────────────────────────────────────────────────────────
        private BuildingGraph       _graph;
        private List<Vector3>       _path       = new List<Vector3>();
        private int                 _pathIndex  = 0;
        private bool                _navigating = false;

        // Waypoint marker GameObjects (one per remaining path point)
        private readonly List<GameObject> _markers = new List<GameObject>();
        private bool _markersUseArrowPrefab = false;

        // Path line
        private Material _lineMat;
        private float    _uvOffset;

        // Compass HUD
        private GameObject         _hudRoot;
        private RectTransform      _hudArrowRT;
        private TextMeshProUGUI    _hudDistText;

        // ── Lifecycle ─────────────────────────────────────────────────────────────

        private void Start()
        {
            if (arCamera == null && Camera.main != null)
                arCamera = Camera.main.transform;

            SetupLineRenderer();
            BuildCompassHUD();
        }

        private void Update()
        {
            if (!_navigating || arCamera == null) return;
            if (_path.Count == 0) return;

            // Animate scrolling line
            _uvOffset -= Time.deltaTime * 0.8f;
            if (_lineMat != null)
                _lineMat.mainTextureOffset = new Vector2(_uvOffset, 0f);

            // Keep line start glued to user position
            if (pathLine != null && pathLine.positionCount > 0)
                pathLine.SetPosition(0, arCamera.position + Vector3.up * floorYOffset);

            // Update compass HUD
            if (_pathIndex < _path.Count)
                UpdateCompass(_path[_pathIndex]);

            // Check if the user reached the current waypoint
            Vector3 target = _path[_pathIndex];
            float dist = Vector2.Distance(
                new Vector2(arCamera.position.x, arCamera.position.z),
                new Vector2(target.x, target.z));

            if (dist <= waypointRadius)
                AdvanceWaypoint();
            else
                EmitInstruction();
        }

        private void OnDestroy()
        {
            ClearMarkers();
        }

        // ── Public API ────────────────────────────────────────────────────────────

        /// <summary>
        /// Start navigating from <paramref name="startNodeId"/> to
        /// <paramref name="targetNodeId"/> using the saved building graph.
        /// Returns false if no path exists.
        /// </summary>
        public bool StartNavigation(string startNodeId, string targetNodeId,
                                    string graphPath = null)
        {
            string path = string.IsNullOrEmpty(graphPath)
                ? BuildingGraph.DefaultPath : graphPath;

            _graph = BuildingGraph.LoadFromFile(path);

            if (_graph == null || _graph.nodes.Count == 0)
            {
                OnInstructionChanged?.Invoke("No map recorded yet. Record a route first!");
                return false;
            }

            List<GraphEdge> route = _graph.FindRoute(startNodeId, targetNodeId);

            if (route == null || route.Count == 0)
            {
                OnInstructionChanged?.Invoke("No path found. Try a different start or destination.");
                return false;
            }

            // Flatten all edge path-points into a single ordered list
            _path.Clear();
            string cursor = startNodeId;
            foreach (GraphEdge edge in route)
            {
                bool forward = edge.nodeA == cursor;
                var  pts     = edge.pathPoints;

                if (forward)
                {
                    for (int i = 0; i < pts.Count; i++)
                        _path.Add(pts[i].ToVector3() + Vector3.up * floorYOffset);
                    cursor = edge.nodeB;
                }
                else
                {
                    for (int i = pts.Count - 1; i >= 0; i--)
                        _path.Add(pts[i].ToVector3() + Vector3.up * floorYOffset);
                    cursor = edge.nodeA;
                }
            }

            if (_path.Count == 0) return false;

            _pathIndex = 0;

            // Skip first point if user is already standing on it
            if (arCamera != null && _path.Count > 1)
            {
                float d = Vector2.Distance(
                    new Vector2(arCamera.position.x, arCamera.position.z),
                    new Vector2(_path[0].x, _path[0].z));
                if (d <= waypointRadius) _pathIndex = 1;
            }

            _navigating = true;
            _uvOffset   = 0f;

            DrawPathLine();
            SpawnMarkers();
            SetHUDVisible(true);
            EmitInstruction();
            return true;
        }

        public void StopNavigation()
        {
            _navigating = false;
            if (pathLine != null) pathLine.positionCount = 0;
            ClearMarkers();
            SetHUDVisible(false);
            OnInstructionChanged?.Invoke("Navigation stopped.");
        }

        // ── Path Line ─────────────────────────────────────────────────────────────

        private void SetupLineRenderer()
        {
            if (pathLine == null)
            {
                pathLine = GetComponent<LineRenderer>()
                        ?? gameObject.AddComponent<LineRenderer>();
            }

            pathLine.startWidth = pathLineWidth;
            pathLine.endWidth   = pathLineWidth * 0.4f;
            pathLine.startColor = pathColor;
            pathLine.endColor   = new Color(pathColor.r, pathColor.g, pathColor.b, 0.15f);
            pathLine.textureMode = LineTextureMode.Tile;
            pathLine.positionCount = 0;

            // "Sprites/Default" is available in both Built-in and URP, and it tiles correctly.
            Shader lineShader = Shader.Find("Sprites/Default");
            if (lineShader == null) lineShader = Shader.Find("Unlit/Color");
            _lineMat = new Material(lineShader ?? Shader.Find("Standard")) { color = pathColor };
            pathLine.material = _lineMat;
        }

        private void DrawPathLine()
        {
            if (pathLine == null || _path.Count == 0 || arCamera == null) return;

            int remaining = _path.Count - _pathIndex;
            pathLine.positionCount = remaining + 1;
            pathLine.SetPosition(0, arCamera.position + Vector3.up * floorYOffset);
            for (int i = 0; i < remaining; i++)
                pathLine.SetPosition(i + 1, _path[_pathIndex + i]);
        }

        // ── Waypoint Markers ──────────────────────────────────────────────────────

        private void SpawnMarkers()
        {
            ClearMarkers();
            _markersUseArrowPrefab = arrowPrefab != null;

            for (int i = _pathIndex; i < _path.Count; i++)
            {
                Vector3 pos = _path[i];
                pos.y += 0.5f; // raise above the floor line

                GameObject marker;
                if (_markersUseArrowPrefab)
                {
                    marker = Instantiate(arrowPrefab, pos, Quaternion.identity);
                    ARArrow arrow = marker.GetComponent<ARArrow>();
                    if (arrow != null)
                    {
                        // Fix: refresh base AFTER placing so bob is correct, then animate in
                        arrow.RefreshBase();
                        arrow.AnimateIn();
                    }
                }
                else
                {
                    // Fallback: glowing sphere — IndoorNavPlaceNote style
                    marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    marker.transform.position   = pos;
                    marker.transform.localScale = Vector3.one * markerSize;
                    Destroy(marker.GetComponent<Collider>());

                    Renderer rend = marker.GetComponent<Renderer>();
                    if (rend != null)
                    {
                        Shader s = Shader.Find("Sprites/Default");
                        var mat  = new Material(s ?? Shader.Find("Standard")) { color = markerColor };
                        rend.material = mat;
                    }
                }

                marker.name = $"Waypoint_{i}";
                _markers.Add(marker);
            }

            // Only pulsate spheres — arrow prefabs pulse themselves via ARArrow.Update
            if (!_markersUseArrowPrefab && _markers.Count > 0)
                StartCoroutine(PulseSpheres());
        }

        /// <summary>
        /// Pulsation coroutine for fallback sphere markers only.
        /// NOT called when using the arrowPrefab (ARArrow handles its own pulse).
        /// </summary>
        private IEnumerator PulseSpheres()
        {
            float t = 0f;
            while (_navigating && _markers.Count > 0)
            {
                t += Time.deltaTime * 2f;
                float scale = markerSize * (0.85f + 0.2f * Mathf.Sin(t));
                foreach (var m in _markers)
                    if (m != null) m.transform.localScale = Vector3.one * scale;
                yield return null;
            }
        }

        private void RemoveFirstMarker()
        {
            if (_markers.Count == 0) return;
            GameObject first = _markers[0];
            _markers.RemoveAt(0);
            if (first == null) return;

            ARArrow arrow = first.GetComponent<ARArrow>();
            if (arrow != null)
                arrow.DestroyWithFade(); // smooth fade-out
            else
                Destroy(first);         // fallback: instant destroy
        }

        private void ClearMarkers()
        {
            foreach (var m in _markers)
            {
                if (m == null) continue;
                ARArrow arrow = m.GetComponent<ARArrow>();
                if (arrow != null) arrow.DestroyWithFade();
                else Destroy(m);
            }
            _markers.Clear();
        }

        // ── Waypoint Advance ──────────────────────────────────────────────────────

        private void AdvanceWaypoint()
        {
            RemoveFirstMarker();
            DrawPathLine();

            if (_pathIndex < _path.Count - 1)
            {
                _pathIndex++;
                EmitInstruction();
            }
            else
            {
                // Destination reached
                _navigating = false;
                if (pathLine != null) pathLine.positionCount = 0;
                ClearMarkers();
                SetHUDVisible(false);
                OnInstructionChanged?.Invoke("🎉 You have arrived at your destination!");
                OnDestinationReached?.Invoke();
            }
        }

        // ── Instruction Text ──────────────────────────────────────────────────────

        private void EmitInstruction()
        {
            if (arCamera == null || _pathIndex >= _path.Count) return;

            Vector3 target = _path[_pathIndex];
            float   dist   = Vector3.Distance(
                new Vector3(arCamera.position.x, 0f, arCamera.position.z),
                new Vector3(target.x,            0f, target.z));

            int remaining = _path.Count - _pathIndex;
            string msg = remaining == 1
                ? $"📍 Destination ahead — {dist:F1} m"
                : $"➡ Head to waypoint {_pathIndex + 1}/{_path.Count} — {dist:F1} m";

            OnInstructionChanged?.Invoke(msg);
        }

        // ── Compass HUD ───────────────────────────────────────────────────────────

        /// <summary>
        /// Builds a compact on-screen compass: circular dark background with a
        /// TMP "▲" glyph that rotates to point at the next waypoint, plus a
        /// distance label below it.
        /// </summary>
        private void BuildCompassHUD()
        {
            if (_hudRoot != null) return;

            _hudRoot = new GameObject("NavCompassHUD");
            Canvas c = _hudRoot.AddComponent<Canvas>();
            c.renderMode   = RenderMode.ScreenSpaceOverlay;
            c.sortingOrder = 50;
            _hudRoot.AddComponent<CanvasScaler>();

            // ── Background circle ──────────────────────────────────────────────
            GameObject bg = new GameObject("BG");
            bg.transform.SetParent(_hudRoot.transform, false);
            Image bgImg = bg.AddComponent<Image>();
            bgImg.color = new Color(0f, 0f, 0f, 0.55f);
            RectTransform bgRT = bg.GetComponent<RectTransform>();
            bgRT.anchorMin        = new Vector2(0.5f, 1f);
            bgRT.anchorMax        = new Vector2(0.5f, 1f);
            bgRT.pivot            = new Vector2(0.5f, 1f);
            bgRT.anchoredPosition = new Vector2(0f, -55f);
            bgRT.sizeDelta        = new Vector2(120f, 120f);

            // ── Arrow glyph (▲) ───────────────────────────────────────────────
            GameObject arrowObj = new GameObject("ArrowGlyph");
            arrowObj.transform.SetParent(bg.transform, false);
            TextMeshProUGUI tmp = arrowObj.AddComponent<TextMeshProUGUI>();
            tmp.text      = "▲";
            tmp.fontSize  = 56;
            tmp.color     = new Color(0.1f, 0.95f, 1f, 1f);
            tmp.alignment = TextAlignmentOptions.Center;
            _hudArrowRT = arrowObj.GetComponent<RectTransform>();
            _hudArrowRT.anchorMin        = new Vector2(0.5f, 0.5f);
            _hudArrowRT.anchorMax        = new Vector2(0.5f, 0.5f);
            _hudArrowRT.pivot            = new Vector2(0.5f, 0.5f);
            _hudArrowRT.anchoredPosition = new Vector2(0f, 6f);
            _hudArrowRT.sizeDelta        = new Vector2(90f, 90f);

            // ── Distance label ────────────────────────────────────────────────
            GameObject distObj = new GameObject("DistLabel");
            distObj.transform.SetParent(bg.transform, false);
            _hudDistText = distObj.AddComponent<TextMeshProUGUI>();
            _hudDistText.text      = "";
            _hudDistText.fontSize  = 20;
            _hudDistText.color     = Color.white;
            _hudDistText.alignment = TextAlignmentOptions.Center;
            RectTransform distRT = distObj.GetComponent<RectTransform>();
            distRT.anchorMin        = new Vector2(0.5f, 0f);
            distRT.anchorMax        = new Vector2(0.5f, 0f);
            distRT.pivot            = new Vector2(0.5f, 0f);
            distRT.anchoredPosition = new Vector2(0f, 4f);
            distRT.sizeDelta        = new Vector2(110f, 38f);

            _hudRoot.SetActive(false);
        }

        private void UpdateCompass(Vector3 targetWorldPos)
        {
            if (_hudArrowRT == null || arCamera == null) return;

            Vector3 toTarget = targetWorldPos - arCamera.position;
            toTarget.y = 0f;
            if (toTarget.sqrMagnitude < 0.001f) return;

            Vector3 camFwd = Vector3.ProjectOnPlane(arCamera.forward, Vector3.up);
            if (camFwd.sqrMagnitude < 0.001f) return;

            float angle = Vector3.SignedAngle(camFwd.normalized, toTarget.normalized, Vector3.up);
            // Negative because UI rotates counter-clockwise for positive Z values
            _hudArrowRT.localEulerAngles = new Vector3(0f, 0f, -angle);

            if (_hudDistText != null)
                _hudDistText.text = $"{toTarget.magnitude:F1} m";
        }

        private void SetHUDVisible(bool visible)
        {
            if (_hudRoot != null) _hudRoot.SetActive(visible);
        }
    }
}
