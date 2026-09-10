using System;
using System.Collections.Generic;
using UnityEngine;
using ARNav.Graph;
using ARNav.Utils;

namespace ARNav.Recording
{
    /// <summary>
    /// Records the user's physical walk via the AR camera transform.
    ///
    /// FLOW (correct order, fixes the old inversion bug):
    ///   1. UI opens the Recording panel — user fills in start name.
    ///   2. User taps "Start Recording" → StartRecording() is called.
    ///   3. User walks to destination.
    ///   4. User taps "Finish & Save" → StopAndSave() is called with the end name.
    ///
    /// KEY FIXES vs old version:
    ///   • Node IDs are GUIDs → no cross-session collision.
    ///   • buildingId is a parameter the caller provides, not hard-coded.
    ///   • StartRecording / StopAndSave are clearly separate methods.
    ///   • All session nodes/edges are uploaded, not just the last one.
    /// </summary>
    public class PathRecorder : MonoBehaviour
    {
        // ── Inspector ─────────────────────────────────────────────────────────────
        [Header("References")]
        [Tooltip("Assign Camera.main's transform, or leave null to auto-find on Start.")]
        [SerializeField] public Transform arCamera;

        [Header("Sampling")]
        [Tooltip("Minimum metres to travel before a new point is recorded.")]
        [SerializeField] private float minDistanceMetres = 0.5f;

        [Tooltip("Minimum degrees the camera must rotate before a new point is recorded.")]
        [SerializeField] private float minAngleDegrees = 15f;

        [Tooltip("An intermediate anchor node is dropped every this many metres.")]
        [SerializeField] private float anchorEveryMetres = 8f;

        [Tooltip("RDP simplification tolerance in metres (lower = more points kept).")]
        [SerializeField] private float simplifyTolerance = 0.15f;

        // ── Public state ──────────────────────────────────────────────────────────
        public bool IsRecording        => _recording;
        public int  AnchorCount        => _anchorCount;
        public int  RecordedPointCount => _segmentPts.Count;
        public float TotalDistance     => _totalDist;

        // ── Events ────────────────────────────────────────────────────────────────
        /// <summary>Fires with a human-readable status string (show in UI).</summary>
        public event Action<string>      OnStatusChanged;
        /// <summary>Fires every sample tick: (pointCount, totalDistanceMetres).</summary>
        public event Action<int, float>  OnProgress;

        // ── Private state ─────────────────────────────────────────────────────────
        private bool   _recording;
        private float  _totalDist;
        private float  _distSinceAnchor;
        private int    _anchorCount;
        private Vector3    _lastPos;
        private Quaternion _lastRot;

        private List<Vector3>   _segmentPts   = new List<Vector3>();
        private List<GraphNode> _sessionNodes = new List<GraphNode>();
        private List<GraphEdge> _sessionEdges = new List<GraphEdge>();
        private GraphNode _tailNode; // last committed anchor node

        // ── Lifecycle ─────────────────────────────────────────────────────────────

        private void Start()
        {
            if (arCamera == null && Camera.main != null)
                arCamera = Camera.main.transform;
        }

        private void Update()
        {
            if (!_recording || arCamera == null) return;

            Vector3    currentPos = arCamera.position;
            Quaternion currentRot = arCamera.rotation;

            float distDelta  = Vector3.Distance(currentPos, _lastPos);
            float angleDelta = Quaternion.Angle(currentRot, _lastRot);

            if (distDelta < minDistanceMetres && angleDelta < minAngleDegrees) return;

            // Record sample
            _segmentPts.Add(currentPos);
            _totalDist       += distDelta;
            _distSinceAnchor += distDelta;
            _lastPos = currentPos;
            _lastRot = currentRot;

            OnProgress?.Invoke(_segmentPts.Count, _totalDist);

            if (_distSinceAnchor >= anchorEveryMetres)
                CommitIntermediateAnchor();
        }

        // ── Public API ────────────────────────────────────────────────────────────

        /// <summary>
        /// Begin a new recording session.
        /// Call this when the user explicitly taps "Start Recording".
        /// </summary>
        /// <param name="startName">Name of the starting location (shown in dropdowns).</param>
        /// <param name="buildingId">Identifier for the building (used in Supabase grouping).</param>
        /// <param name="floor">Floor number.</param>
        public void StartRecording(string startName, string buildingId = "main_building", int floor = 1)
        {
            if (_recording)
            {
                OnStatusChanged?.Invoke("Already recording — tap Finish first.");
                return;
            }
            if (arCamera == null)
            {
                OnStatusChanged?.Invoke("Error: AR Camera not found. Cannot record.");
                return;
            }

            // Reset all session state
            _recording        = true;
            _totalDist        = 0f;
            _distSinceAnchor  = 0f;
            _anchorCount      = 0;
            _segmentPts.Clear();
            _sessionNodes.Clear();
            _sessionEdges.Clear();

            _lastPos = arCamera.position;
            _lastRot = arCamera.rotation;

            // Create the start node — GUID id, no collision ever
            _tailNode = new GraphNode(startName, buildingId, floor, _lastPos);
            _sessionNodes.Add(_tailNode);
            _anchorCount++;

            _segmentPts.Add(_lastPos);

            OnStatusChanged?.Invoke($"🔴 Recording from '{startName}'. Walk to your destination.");
        }

        /// <summary>
        /// Finish recording, commit the final node, merge into the persistent graph,
        /// and return the updated graph.
        /// Call this when the user taps "Finish & Save".
        /// </summary>
        /// <param name="endName">Name of the destination location.</param>
        /// <param name="overrideSavePath">Optional custom path — null uses default.</param>
        /// <returns>The merged BuildingGraph, or null if not recording.</returns>
        public BuildingGraph StopAndSave(string endName, string overrideSavePath = null)
        {
            if (!_recording)
            {
                OnStatusChanged?.Invoke("Not recording.");
                return null;
            }
            _recording = false;

            // Commit final destination node
            GraphNode endNode = new GraphNode(
                endName,
                _tailNode.buildingId,
                _tailNode.floor,
                _lastPos
            );
            _sessionNodes.Add(endNode);
            _anchorCount++;

            // Commit final segment edge
            List<Vector3> simplified = PathSimplifier.Simplify(_segmentPts, simplifyTolerance);
            _sessionEdges.Add(new GraphEdge(_tailNode.id, endNode.id, simplified));

            // Merge into persistent graph
            string savePath = string.IsNullOrEmpty(overrideSavePath)
                ? BuildingGraph.DefaultPath
                : overrideSavePath;

            BuildingGraph graph = BuildingGraph.LoadFromFile(savePath);
            foreach (var n in _sessionNodes) graph.AddNode(n);
            foreach (var e in _sessionEdges) graph.AddEdge(e);
            graph.SaveToFile(savePath);

            OnStatusChanged?.Invoke(
                $"✅ Saved '{_tailNode.name}' → '{endNode.name}' " +
                $"({_sessionNodes.Count} nodes, {_sessionEdges.Count} edges, {_totalDist:F1} m)"
            );
            return graph;
        }

        /// <summary>Cancel a recording in progress without saving.</summary>
        public void CancelRecording()
        {
            _recording = false;
            _sessionNodes.Clear();
            _sessionEdges.Clear();
            _segmentPts.Clear();
            OnStatusChanged?.Invoke("Recording cancelled.");
        }

        /// <summary>
        /// All nodes recorded this session. Useful for uploading all edges to
        /// Supabase after StopAndSave().
        /// </summary>
        public IReadOnlyList<GraphNode> SessionNodes => _sessionNodes;

        /// <summary>All edges recorded this session.</summary>
        public IReadOnlyList<GraphEdge> SessionEdges => _sessionEdges;

        // ── Internals ─────────────────────────────────────────────────────────────

        private void CommitIntermediateAnchor()
        {
            GraphNode newNode = new GraphNode(
                $"Waypoint {_anchorCount}",
                _tailNode.buildingId,
                _tailNode.floor,
                _lastPos
            );
            _sessionNodes.Add(newNode);
            _anchorCount++;

            List<Vector3> simplified = PathSimplifier.Simplify(_segmentPts, simplifyTolerance);
            _sessionEdges.Add(new GraphEdge(_tailNode.id, newNode.id, simplified));

            _segmentPts.Clear();
            _segmentPts.Add(_lastPos); // new segment starts at current pos
            _tailNode             = newNode;
            _distSinceAnchor      = 0f;

            OnStatusChanged?.Invoke($"Anchor {_anchorCount} dropped ({simplified.Count} pts)");
        }
    }
}
