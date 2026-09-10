using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using ARNav.Graph;
using ARNav.Utils;

namespace ARNav.Recording
{
    /// <summary>
    /// Records user paths by sampling AR Camera position/rotation at distance and angle thresholds,
    /// segments paths with anchors every 5-10 meters to eliminate drift, simplifies points using RDP,
    /// and saves to the local building graph.
    /// </summary>
    public class PathRecorder : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private Transform arCamera;

        [Header("Sampling Settings")]
        [Tooltip("Minimum distance in meters between captured waypoints.")]
        [SerializeField] private float minDistanceSample = 0.5f;

        [Tooltip("Minimum rotation change in degrees to capture a waypoint (e.g. sharp turns).")]
        [SerializeField] private float minAngleSample = 15f;

        [Tooltip("Distance in meters between automatic anchor checkpoints.")]
        [SerializeField] private float anchorIntervalMeters = 8f;

        [Tooltip("Tolerance for Douglas-Peucker simplification in meters.")]
        [SerializeField] private float simplificationTolerance = 0.15f;

        // Runtime Recording State
        private bool _isRecording = false;
        private Vector3 _lastRecordedPos;
        private Quaternion _lastRecordedRot;
        private float _distanceSinceLastAnchor = 0f;
        private int _anchorCount = 0;

        private List<Vector3> _currentSegmentPoints = new List<Vector3>();
        private List<GraphNode> _sessionNodes = new List<GraphNode>();
        private List<GraphEdge> _sessionEdges = new List<GraphEdge>();
        private GraphNode _lastNode = null;

        public bool IsRecording => _isRecording;
        public int RecordedPointCount => _currentSegmentPoints.Count;
        public int AnchorCount => _anchorCount;

        public event Action<string> OnStatusChanged;
        public event Action<int, float> OnRecordingProgress; // (pointCount, totalDistance)
        private float _totalDistanceWalked = 0f;

        private void Start()
        {
            if (arCamera == null && Camera.main != null)
            {
                arCamera = Camera.main.transform;
            }
        }

        public void StartRecording(string startNodeName = "StartPoint", string buildingId = "main_building", int floor = 1)
        {
            if (arCamera == null)
            {
                OnStatusChanged?.Invoke("Error: AR Camera reference not found!");
                return;
            }

            _isRecording = true;
            _totalDistanceWalked = 0f;
            _distanceSinceLastAnchor = 0f;
            _anchorCount = 0;
            _currentSegmentPoints.Clear();
            _sessionNodes.Clear();
            _sessionEdges.Clear();

            _lastRecordedPos = arCamera.position;
            _lastRecordedRot = arCamera.rotation;

            // Create initial start node with anchor
            _anchorCount++;
            string startAnchorId = $"anchor_{Guid.NewGuid().ToString().Substring(0, 8)}";
            string startNodeId = $"node_{_anchorCount}";
            _lastNode = new GraphNode(startNodeId, startNodeName, buildingId, floor, _lastRecordedPos, startAnchorId);
            _sessionNodes.Add(_lastNode);

            _currentSegmentPoints.Add(_lastRecordedPos);

            OnStatusChanged?.Invoke($"Recording started at {_lastNode.name} (Anchor: {startAnchorId})");
        }

        private void Update()
        {
            if (!_isRecording || arCamera == null) return;

            Vector3 currentPos = arCamera.position;
            Quaternion currentRot = arCamera.rotation;

            float distDelta = Vector3.Distance(currentPos, _lastRecordedPos);
            float angleDelta = Quaternion.Angle(currentRot, _lastRecordedRot);

            if (distDelta >= minDistanceSample || angleDelta >= minAngleSample)
            {
                _currentSegmentPoints.Add(currentPos);
                _totalDistanceWalked += distDelta;
                _distanceSinceLastAnchor += distDelta;

                _lastRecordedPos = currentPos;
                _lastRecordedRot = currentRot;

                OnRecordingProgress?.Invoke(_currentSegmentPoints.Count, _totalDistanceWalked);

                // Auto-drop intermediate anchor every 5-10m
                if (_distanceSinceLastAnchor >= anchorIntervalMeters)
                {
                    CommitIntermediateAnchor();
                }
            }
        }

        private void CommitIntermediateAnchor()
        {
            _anchorCount++;
            string anchorId = $"anchor_{Guid.NewGuid().ToString().Substring(0, 8)}";
            string nodeId = $"node_{_anchorCount}";

            GraphNode newNode = new GraphNode(nodeId, $"Waypoint {_anchorCount}", _lastNode.buildingId, _lastNode.floor, _lastRecordedPos, anchorId);
            _sessionNodes.Add(newNode);

            // Simplify segment points before storing into edge
            List<Vector3> simplified = PathSimplifier.Simplify(_currentSegmentPoints, simplificationTolerance);
            string edgeId = $"edge_{_lastNode.id}_{newNode.id}";
            GraphEdge edge = new GraphEdge(edgeId, _lastNode.id, newNode.id, simplified);
            _sessionEdges.Add(edge);

            // Reset segment buffer for next anchor
            _currentSegmentPoints.Clear();
            _currentSegmentPoints.Add(_lastRecordedPos); // Start next segment at current position
            _lastNode = newNode;
            _distanceSinceLastAnchor = 0f;

            OnStatusChanged?.Invoke($"Anchor {_anchorCount} dropped ({simplified.Count} pts simplified)");
        }

        public BuildingGraph StopAndSaveRecording(string endNodeName = "Destination", string savePath = null)
        {
            if (!_isRecording) return null;
            _isRecording = false;

            // Commit final destination node
            _anchorCount++;
            string endAnchorId = $"anchor_{Guid.NewGuid().ToString().Substring(0, 8)}";
            string endNodeId = $"node_{_anchorCount}";

            GraphNode endNode = new GraphNode(endNodeId, endNodeName, _lastNode.buildingId, _lastNode.floor, _lastRecordedPos, endAnchorId);
            _sessionNodes.Add(endNode);

            List<Vector3> simplified = PathSimplifier.Simplify(_currentSegmentPoints, simplificationTolerance);
            string edgeId = $"edge_{_lastNode.id}_{endNode.id}";
            GraphEdge finalEdge = new GraphEdge(edgeId, _lastNode.id, endNode.id, simplified);
            _sessionEdges.Add(finalEdge);

            // Merge into persistent graph
            string targetPath = string.IsNullOrEmpty(savePath) 
                ? Path.Combine(Application.persistentDataPath, "indoor_graph.json") 
                : savePath;

            BuildingGraph graph = BuildingGraph.LoadFromFile(targetPath);
            foreach (var node in _sessionNodes) graph.AddNode(node);
            foreach (var edge in _sessionEdges) graph.AddEdge(edge);

            graph.SaveToFile(targetPath);

            OnStatusChanged?.Invoke($"Saved route to {targetPath}: {_sessionNodes.Count} nodes, {_sessionEdges.Count} edges, {_totalDistanceWalked:F1}m");
            return graph;
        }
    }
}
