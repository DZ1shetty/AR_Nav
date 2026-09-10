using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using ARNav.Graph;

namespace ARNav.Navigation
{
    /// <summary>
    /// Loads a local or cloud building graph, calculates the shortest edge route between nodes,
    /// and renders an animated AR path overlay (LineRenderer / ground chevrons) with turn-by-turn guidance.
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
        [SerializeField] private float floorYOffset = -0.3f; // Project slightly towards the ground

        [Header("Navigation Logic")]
        [SerializeField] private float waypointReachRadius = 1.2f;

        private BuildingGraph _activeGraph;
        private List<GraphEdge> _currentRoute = new List<GraphEdge>();
        private List<Vector3> _flattenedRoutePoints = new List<Vector3>();
        private int _currentPointIndex = 0;
        private bool _isNavigating = false;

        public bool IsNavigating => _isNavigating;
        public event Action<string> OnInstructionChanged;
        public event Action OnDestinationReached;

        private void Start()
        {
            if (arCamera == null && Camera.main != null)
                arCamera = Camera.main.transform;

            EnsureLineRenderer();
        }

        private void EnsureLineRenderer()
        {
            if (pathLineRenderer == null)
            {
                pathLineRenderer = GetComponent<LineRenderer>();
                if (pathLineRenderer == null)
                {
                    pathLineRenderer = gameObject.AddComponent<LineRenderer>();
                }
            }

            pathLineRenderer.startWidth = pathLineWidth;
            pathLineRenderer.endWidth = pathLineWidth;
            pathLineRenderer.material = new Material(Shader.Find("Sprites/Default"));
            pathLineRenderer.startColor = pathColor;
            pathLineRenderer.endColor = pathColor;
            pathLineRenderer.positionCount = 0;
        }

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

            // Build flattened list of points across all edges in the route
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
            // If user is already standing right at the start waypoint, advance to point 1 so it doesn't instantly finish
            if (_flattenedRoutePoints.Count > 1 && arCamera != null)
            {
                float distToStart = Vector2.Distance(
                    new Vector2(arCamera.position.x, arCamera.position.z),
                    new Vector2(_flattenedRoutePoints[0].x, _flattenedRoutePoints[0].z)
                );
                if (distToStart <= waypointReachRadius)
                {
                    _currentPointIndex = 1;
                }
            }

            _isNavigating = true;

            RenderPathLine();
            UpdateGuidanceText();
            return true;
        }

        private void Update()
        {
            if (!_isNavigating || _flattenedRoutePoints.Count == 0 || arCamera == null) return;

            Vector3 userPos = arCamera.position;
            Vector3 targetPt = _flattenedRoutePoints[_currentPointIndex];

            // 2D ground distance check (ignore height differences)
            float groundDistance = Vector2.Distance(
                new Vector2(userPos.x, userPos.z),
                new Vector2(targetPt.x, targetPt.z)
            );

            if (groundDistance <= waypointReachRadius)
            {
                // Advance to next waypoint
                if (_currentPointIndex < _flattenedRoutePoints.Count - 1)
                {
                    _currentPointIndex++;
                    RenderPathLine();
                    UpdateGuidanceText();
                }
                else
                {
                    // Reached final destination!
                    _isNavigating = false;
                    pathLineRenderer.positionCount = 0;
                    OnInstructionChanged?.Invoke("You have arrived at your destination! 🎉");
                    OnDestinationReached?.Invoke();
                    return;
                }
            }
            else
            {
                // Continuously tie start of line to player's current floor position
                if (pathLineRenderer.positionCount > 0)
                {
                    Vector3 userGround = arCamera.position + Vector3.up * floorYOffset;
                    pathLineRenderer.SetPosition(0, userGround);
                }
            }
        }

        private void RenderPathLine()
        {
            if (pathLineRenderer == null || _flattenedRoutePoints.Count == 0) return;

            int remainingCount = _flattenedRoutePoints.Count - _currentPointIndex;
            pathLineRenderer.positionCount = remainingCount + 1;

            // Connect user's current camera floor projection to next waypoint
            Vector3 userGround = arCamera.position + Vector3.up * floorYOffset;
            pathLineRenderer.SetPosition(0, userGround);

            for (int i = 0; i < remainingCount; i++)
            {
                pathLineRenderer.SetPosition(i + 1, _flattenedRoutePoints[_currentPointIndex + i]);
            }
        }

        /// <summary>
        /// Updates turn-by-turn instructions.
        /// When the user is within 3m of the current waypoint, pre-announces the NEXT turn direction
        /// so they have time to react before reaching the junction.
        /// (Based on: ISMSIT 2020 — Mobile AR-based Indoor Navigation System)
        /// </summary>
        private void UpdateGuidanceText()
        {
            if (_currentPointIndex >= _flattenedRoutePoints.Count) return;

            Vector3 targetPt = _flattenedRoutePoints[_currentPointIndex];
            float dist = Vector3.Distance(arCamera.position, targetPt);

            // --- Current turn direction ---
            Vector3 toTarget = (targetPt - arCamera.position).normalized;
            float angle = Vector3.SignedAngle(arCamera.forward, toTarget, Vector3.up);
            string currentDirection = AngleToDirection(angle);

            // --- Look-ahead: pre-announce NEXT turn when within 3m of this waypoint (Paper 5) ---
            bool hasNextWaypoint = (_currentPointIndex + 1) < _flattenedRoutePoints.Count;
            if (dist <= 3f && hasNextWaypoint)
            {
                Vector3 nextPt = _flattenedRoutePoints[_currentPointIndex + 1];
                // The upcoming turn angle at the junction
                Vector3 incomingDir = (targetPt - arCamera.position).normalized;
                Vector3 outgoingDir = (nextPt - targetPt).normalized;
                float nextAngle = Vector3.SignedAngle(incomingDir, outgoingDir, Vector3.up);
                string nextDirection = AngleToDirection(nextAngle);

                if (nextDirection != "Walk straight")
                    OnInstructionChanged?.Invoke($"{currentDirection} ({dist:F1}m) → then {nextDirection} ahead");
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

        public void StopNavigation()
        {
            _isNavigating = false;
            if (pathLineRenderer != null) pathLineRenderer.positionCount = 0;
            OnInstructionChanged?.Invoke("Navigation stopped.");
        }
    }
}
