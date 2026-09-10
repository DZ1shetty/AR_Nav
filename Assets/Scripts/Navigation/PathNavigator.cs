using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using ARNav.Graph;

namespace ARNav.Navigation
{
    /// <summary>
    /// Loads a local building graph, runs A* routing, and renders the path with:
    ///  - Animated ground-level LineRenderer
    ///  - 3D sphere waypoint markers at each node (like IndoorNavPlaceNote diamond markers)
    ///  - On-screen compass HUD arrow always pointing toward the next waypoint
    /// </summary>
    public class PathNavigator : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private Transform arCamera;
        [SerializeField] private LineRenderer pathLineRenderer;
        [SerializeField] private GameObject arrowPrefab;

        [Header("Path Rendering Settings")]
        [SerializeField] private float pathLineWidth = 0.25f;
        [SerializeField] private Color pathColor = new Color(0.1f, 0.8f, 1f, 0.8f);
        [SerializeField] private float floorYOffset = -0.3f;

        [Header("Waypoint Marker Settings")]
        [SerializeField] private float markerSize = 0.35f;
        [SerializeField] private Color markerColor = new Color(0.1f, 0.9f, 1f, 0.85f);

        [Header("Navigation Logic")]
        [SerializeField] private float waypointReachRadius = 1.2f;

        // --- Internal State ---
        private BuildingGraph _activeGraph;
        private List<GraphEdge> _currentRoute = new List<GraphEdge>();
        private List<Vector3> _flattenedRoutePoints = new List<Vector3>();
        private int _currentPointIndex = 0;
        private bool _isNavigating = false;

        // Waypoint sphere markers (IndoorNavPlaceNote style)
        private readonly List<GameObject> _waypointMarkers = new List<GameObject>();

        // HUD compass arrow
        private GameObject _compassHUDCanvas;
        private RectTransform _arrowImageRT;
        private TextMeshProUGUI _compassDistText;

        // Path animation (UV scroll)
        private Material _lineMaterial;
        private float _uvOffset = 0f;

        public bool IsNavigating => _isNavigating;
        public event Action<string> OnInstructionChanged;
        public event Action OnDestinationReached;

        // ─── Lifecycle ────────────────────────────────────────────────────────────

        private void Start()
        {
            if (arCamera == null && Camera.main != null)
                arCamera = Camera.main.transform;

            EnsureLineRenderer();
            EnsureCompassHUD();
        }

        private void Update()
        {
            if (!_isNavigating || _flattenedRoutePoints.Count == 0 || arCamera == null) return;

            // Animate path texture
            AnimatePathLine();

            Vector3 userPos = arCamera.position;
            Vector3 targetPt = _flattenedRoutePoints[_currentPointIndex];

            float groundDistance = Vector2.Distance(
                new Vector2(userPos.x, userPos.z),
                new Vector2(targetPt.x, targetPt.z)
            );

            if (groundDistance <= waypointReachRadius)
            {
                // Remove the marker we just reached
                RemoveFirstMarker();

                if (_currentPointIndex < _flattenedRoutePoints.Count - 1)
                {
                    _currentPointIndex++;
                    RenderPathLine();
                    UpdateGuidanceText();
                }
                else
                {
                    // Arrived!
                    _isNavigating = false;
                    pathLineRenderer.positionCount = 0;
                    ClearWaypointMarkers();
                    SetCompassHUDVisible(false);
                    OnInstructionChanged?.Invoke("You have arrived at your destination! 🎉");
                    OnDestinationReached?.Invoke();
                }
            }
            else
            {
                // Continuously tie start of line to camera floor position
                if (pathLineRenderer.positionCount > 0)
                {
                    Vector3 userGround = arCamera.position + Vector3.up * floorYOffset;
                    pathLineRenderer.SetPosition(0, userGround);
                }

                // Update compass HUD
                UpdateCompassHUD(targetPt);
            }

            UpdateGuidanceText();
        }

        private void OnDestroy()
        {
            ClearWaypointMarkers();
        }

        // ─── Public API ───────────────────────────────────────────────────────────

        public bool StartNavigation(string startNodeId, string targetNodeId, string graphPath = null)
        {
            string path = string.IsNullOrEmpty(graphPath)
                ? Path.Combine(Application.persistentDataPath, "indoor_graph.json")
                : graphPath;

            _activeGraph = BuildingGraph.LoadFromFile(path);

            if (_activeGraph.nodes.Count == 0)
            {
                OnInstructionChanged?.Invoke("No graph found. Record a route first!");
                return false;
            }

            _currentRoute = _activeGraph.FindRoute(startNodeId, targetNodeId);

            if (_currentRoute == null || _currentRoute.Count == 0)
            {
                OnInstructionChanged?.Invoke("No path found between selected locations.");
                return false;
            }

            // Build flattened list of world-space waypoints from all route edges
            _flattenedRoutePoints.Clear();
            string currentNodeId = startNodeId;

            foreach (var edge in _currentRoute)
            {
                bool isForward = (edge.nodeA == currentNodeId);
                List<SerializableVector3> points = edge.pathPoints;

                if (isForward)
                {
                    for (int i = 0; i < points.Count; i++)
                        _flattenedRoutePoints.Add(points[i].ToVector3() + Vector3.up * floorYOffset);
                    currentNodeId = edge.nodeB;
                }
                else
                {
                    for (int i = points.Count - 1; i >= 0; i--)
                        _flattenedRoutePoints.Add(points[i].ToVector3() + Vector3.up * floorYOffset);
                    currentNodeId = edge.nodeA;
                }
            }

            _currentPointIndex = 0;
            // Skip start waypoint if user is already standing on it
            if (_flattenedRoutePoints.Count > 1 && arCamera != null)
            {
                float distToStart = Vector2.Distance(
                    new Vector2(arCamera.position.x, arCamera.position.z),
                    new Vector2(_flattenedRoutePoints[0].x, _flattenedRoutePoints[0].z)
                );
                if (distToStart <= waypointReachRadius)
                    _currentPointIndex = 1;
            }

            _isNavigating = true;

            // Render everything
            RenderPathLine();
            SpawnWaypointMarkers();
            SetCompassHUDVisible(true);
            UpdateGuidanceText();
            return true;
        }

        public void StopNavigation()
        {
            _isNavigating = false;
            if (pathLineRenderer != null) pathLineRenderer.positionCount = 0;
            ClearWaypointMarkers();
            SetCompassHUDVisible(false);
            OnInstructionChanged?.Invoke("Navigation stopped.");
        }

        // ─── Line Renderer ────────────────────────────────────────────────────────

        private void EnsureLineRenderer()
        {
            if (pathLineRenderer == null)
            {
                pathLineRenderer = GetComponent<LineRenderer>();
                if (pathLineRenderer == null)
                    pathLineRenderer = gameObject.AddComponent<LineRenderer>();
            }

            pathLineRenderer.startWidth = pathLineWidth;
            pathLineRenderer.endWidth = pathLineWidth * 0.5f;

            // Scrolling "follow me" material
            _lineMaterial = new Material(Shader.Find("Sprites/Default"));
            _lineMaterial.color = pathColor;
            pathLineRenderer.material = _lineMaterial;
            pathLineRenderer.startColor = pathColor;
            pathLineRenderer.endColor = new Color(pathColor.r, pathColor.g, pathColor.b, 0.2f);
            pathLineRenderer.textureMode = LineTextureMode.Tile;
            pathLineRenderer.positionCount = 0;
        }

        private void RenderPathLine()
        {
            if (pathLineRenderer == null || _flattenedRoutePoints.Count == 0) return;

            int remainingCount = _flattenedRoutePoints.Count - _currentPointIndex;
            pathLineRenderer.positionCount = remainingCount + 1;

            Vector3 userGround = arCamera.position + Vector3.up * floorYOffset;
            pathLineRenderer.SetPosition(0, userGround);

            for (int i = 0; i < remainingCount; i++)
                pathLineRenderer.SetPosition(i + 1, _flattenedRoutePoints[_currentPointIndex + i]);
        }

        private void AnimatePathLine()
        {
            if (_lineMaterial == null) return;
            _uvOffset -= Time.deltaTime * 0.8f;
            _lineMaterial.mainTextureOffset = new Vector2(_uvOffset, 0f);
        }

        // ─── Waypoint Markers (IndoorNavPlaceNote style) ──────────────────────────

        /// <summary>
        /// Spawns a glowing sphere at every remaining waypoint along the route,
        /// exactly like the diamond models used in IndoorNavPlaceNote.
        /// </summary>
        private void SpawnWaypointMarkers()
        {
            ClearWaypointMarkers();

            for (int i = _currentPointIndex; i < _flattenedRoutePoints.Count; i++)
            {
                // Raise markers slightly above the floor line
                Vector3 pos = _flattenedRoutePoints[i];
                pos.y += 0.5f;

                GameObject marker;
                if (arrowPrefab != null)
                {
                    marker = Instantiate(arrowPrefab, pos, Quaternion.identity);
                }
                else
                {
                    // Fallback: create a glowing sphere (matches IndoorNavPlaceNote diamond style)
                    marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    marker.transform.position = pos;
                    marker.transform.localScale = Vector3.one * markerSize;

                    // Remove physics collider (not needed in AR overlay)
                    Destroy(marker.GetComponent<Collider>());

                    // Apply emissive-style material
                    Renderer rend = marker.GetComponent<Renderer>();
                    if (rend != null)
                    {
                        Material mat = new Material(Shader.Find("Sprites/Default"));
                        mat.color = markerColor;
                        rend.material = mat;
                    }
                }

                marker.name = $"WaypointMarker_{i}";
                _waypointMarkers.Add(marker);
            }

            // Animate the markers with a pulsating coroutine
            if (_waypointMarkers.Count > 0)
                StartCoroutine(PulsateMarkers());
        }

        private System.Collections.IEnumerator PulsateMarkers()
        {
            float t = 0f;
            while (_isNavigating && _waypointMarkers.Count > 0)
            {
                t += Time.deltaTime * 2f;
                float scale = markerSize * (0.85f + 0.2f * Mathf.Sin(t));

                foreach (var m in _waypointMarkers)
                {
                    if (m != null)
                        m.transform.localScale = Vector3.one * scale;
                }
                yield return null;
            }
        }

        private void RemoveFirstMarker()
        {
            if (_waypointMarkers.Count > 0)
            {
                if (_waypointMarkers[0] != null)
                    Destroy(_waypointMarkers[0]);
                _waypointMarkers.RemoveAt(0);
            }
        }

        private void ClearWaypointMarkers()
        {
            foreach (var m in _waypointMarkers)
                if (m != null) Destroy(m);
            _waypointMarkers.Clear();
        }

        // ─── Compass HUD Arrow ────────────────────────────────────────────────────

        /// <summary>
        /// Creates an on-screen compass arrow (like IndoorNavPlaceNote's direction indicator).
        /// The arrow rotates every frame to point at the next waypoint.
        /// </summary>
        private void EnsureCompassHUD()
        {
            if (_compassHUDCanvas != null) return;

            // Dedicated canvas behind other UI
            _compassHUDCanvas = new GameObject("NavCompassHUD");
            Canvas c = _compassHUDCanvas.AddComponent<Canvas>();
            c.renderMode = RenderMode.ScreenSpaceOverlay;
            c.sortingOrder = 50;
            _compassHUDCanvas.AddComponent<CanvasScaler>();

            // Circular background
            GameObject bgObj = new GameObject("CompassBG");
            bgObj.transform.SetParent(_compassHUDCanvas.transform, false);
            Image bgImg = bgObj.AddComponent<Image>();
            bgImg.color = new Color(0f, 0f, 0f, 0.55f);
            RectTransform bgRT = bgObj.GetComponent<RectTransform>();
            bgRT.anchorMin = new Vector2(0.5f, 1f);
            bgRT.anchorMax = new Vector2(0.5f, 1f);
            bgRT.pivot = new Vector2(0.5f, 1f);
            bgRT.anchoredPosition = new Vector2(0f, -60f);
            bgRT.sizeDelta = new Vector2(130f, 130f);

            // Arrow image (triangle/chevron drawn with text for zero-dependency)
            GameObject arrowObj = new GameObject("CompassArrow");
            arrowObj.transform.SetParent(bgObj.transform, false);
            Image arrowImg = arrowObj.AddComponent<Image>();
            arrowImg.color = new Color(0.1f, 0.9f, 1f, 1f);
            _arrowImageRT = arrowObj.GetComponent<RectTransform>();
            _arrowImageRT.anchorMin = new Vector2(0.5f, 0.5f);
            _arrowImageRT.anchorMax = new Vector2(0.5f, 0.5f);
            _arrowImageRT.pivot = new Vector2(0.5f, 0.5f);
            _arrowImageRT.anchoredPosition = Vector2.zero;
            _arrowImageRT.sizeDelta = new Vector2(60f, 80f);

            // Use Unicode arrow as sprite substitute via TMP
            GameObject arrowTextObj = new GameObject("ArrowGlyph");
            arrowTextObj.transform.SetParent(bgObj.transform, false);
            TextMeshProUGUI arrowTMP = arrowTextObj.AddComponent<TextMeshProUGUI>();
            arrowTMP.text = "▲";
            arrowTMP.fontSize = 52;
            arrowTMP.color = new Color(0.1f, 0.95f, 1f, 1f);
            arrowTMP.alignment = TextAlignmentOptions.Center;
            // Keep a reference to the TMP for rotation
            RectTransform arrowTmpRT = arrowTextObj.GetComponent<RectTransform>();
            arrowTmpRT.anchorMin = new Vector2(0.5f, 0.5f);
            arrowTmpRT.anchorMax = new Vector2(0.5f, 0.5f);
            arrowTmpRT.pivot = new Vector2(0.5f, 0.5f);
            arrowTmpRT.anchoredPosition = new Vector2(0f, 5f);
            arrowTmpRT.sizeDelta = new Vector2(80f, 80f);
            // Store the TMP RT as the thing we'll rotate
            _arrowImageRT = arrowTmpRT;

            // Distance text below arrow
            GameObject distObj = new GameObject("CompassDist");
            distObj.transform.SetParent(bgObj.transform, false);
            _compassDistText = distObj.AddComponent<TextMeshProUGUI>();
            _compassDistText.text = "";
            _compassDistText.fontSize = 18;
            _compassDistText.color = Color.white;
            _compassDistText.alignment = TextAlignmentOptions.Center;
            RectTransform distRT = distObj.GetComponent<RectTransform>();
            distRT.anchorMin = new Vector2(0.5f, 0f);
            distRT.anchorMax = new Vector2(0.5f, 0f);
            distRT.pivot = new Vector2(0.5f, 0f);
            distRT.anchoredPosition = new Vector2(0f, 6f);
            distRT.sizeDelta = new Vector2(120f, 40f);

            // Destroy the unused image arrow (we use text glyph instead)
            Destroy(arrowImg.gameObject);

            _compassHUDCanvas.SetActive(false);
        }

        private void UpdateCompassHUD(Vector3 targetWorldPos)
        {
            if (_arrowImageRT == null || arCamera == null) return;

            // Project the direction to target onto the 2D screen plane
            Vector3 toTarget = targetWorldPos - arCamera.position;
            toTarget.y = 0f;

            if (toTarget.sqrMagnitude < 0.01f) return;

            // Angle in world space between camera forward and direction to target
            float angle = Vector3.SignedAngle(
                Vector3.ProjectOnPlane(arCamera.forward, Vector3.up).normalized,
                toTarget.normalized,
                Vector3.up
            );

            // Rotate the arrow glyph — negative because Unity UI rotates clockwise for positive Z
            _arrowImageRT.localEulerAngles = new Vector3(0f, 0f, -angle);

            float dist = toTarget.magnitude;
            if (_compassDistText != null)
                _compassDistText.text = $"{dist:F1}m";
        }

        private void SetCompassHUDVisible(bool visible)
        {
            if (_compassHUDCanvas != null)
                _compassHUDCanvas.SetActive(visible);
        }

        // ─── Turn-by-Turn Text ────────────────────────────────────────────────────

        /// <summary>
        /// Updates turn-by-turn instructions with pre-announcement of the next turn.
        /// Based on: ISMSIT 2020 — Mobile AR Indoor Navigation System
        /// </summary>
        private void UpdateGuidanceText()
        {
            if (_currentPointIndex >= _flattenedRoutePoints.Count) return;

            Vector3 targetPt = _flattenedRoutePoints[_currentPointIndex];
            float dist = Vector3.Distance(arCamera.position, targetPt);

            Vector3 toTarget = (targetPt - arCamera.position).normalized;
            float angle = Vector3.SignedAngle(arCamera.forward, toTarget, Vector3.up);
            string currentDirection = AngleToDirection(angle);

            bool hasNext = (_currentPointIndex + 1) < _flattenedRoutePoints.Count;
            if (dist <= 3f && hasNext)
            {
                Vector3 nextPt = _flattenedRoutePoints[_currentPointIndex + 1];
                Vector3 incoming = (targetPt - arCamera.position).normalized;
                Vector3 outgoing = (nextPt - targetPt).normalized;
                float nextAngle = Vector3.SignedAngle(incoming, outgoing, Vector3.up);
                string nextDir = AngleToDirection(nextAngle);

                if (nextDir != "Walk straight")
                    OnInstructionChanged?.Invoke($"{currentDirection} ({dist:F1}m) → then {nextDir} ahead");
                else
                    OnInstructionChanged?.Invoke($"{currentDirection} ({dist:F1}m)");
            }
            else
            {
                OnInstructionChanged?.Invoke($"{currentDirection} ({dist:F1}m)");
            }
        }

        private string AngleToDirection(float angle)
        {
            if (angle > 35f && angle < 135f)   return "Turn right";
            if (angle < -35f && angle > -135f)  return "Turn left";
            if (Mathf.Abs(angle) >= 135f)       return "Turn around";
            return "Walk straight";
        }
    }
}
