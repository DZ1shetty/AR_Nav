using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace ARNav.Graph
{
    // ─────────────────────────────────────────────────────────────────────────────
    // SerializableVector3
    // Plain struct wrapper so Unity's JsonUtility can serialize Vector3 values
    // inside Lists (JsonUtility can't serialize UnityEngine.Vector3 in lists).
    // ─────────────────────────────────────────────────────────────────────────────
    [Serializable]
    public struct SerializableVector3
    {
        public float x, y, z;

        public SerializableVector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public SerializableVector3(Vector3 v)                  { x = v.x; y = v.y; z = v.z; }

        public Vector3 ToVector3() => new Vector3(x, y, z);

        public override string ToString() => $"({x:F2}, {y:F2}, {z:F2})";
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // GraphNode
    // Represents a named location (junction, landmark, start/end point) in the
    // building graph. Every node has a unique GUID-based ID so multiple recording
    // sessions never collide when merged into the same JSON file.
    // ─────────────────────────────────────────────────────────────────────────────
    [Serializable]
    public class GraphNode
    {
        public string id;               // GUID — unique across all sessions
        public string name;             // Human-readable label shown in dropdowns
        public string buildingId;       // Groups nodes by building
        public int    floor;            // Floor number
        public SerializableVector3 localPosition; // AR-space position at recording time
        public string anchorId;         // Optional: persistent AR anchor ID
        public float  confidenceScore = 1f;
        public long   lastVerified;     // Unix timestamp

        public GraphNode() {}

        public GraphNode(string name, string buildingId, int floor, Vector3 position, string anchorId = "")
        {
            this.id           = Guid.NewGuid().ToString("N"); // e.g. "a3f7c2d1..."
            this.name         = name;
            this.buildingId   = buildingId;
            this.floor        = floor;
            this.localPosition = new SerializableVector3(position);
            this.anchorId     = anchorId;
            this.confidenceScore = 1f;
            this.lastVerified = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // GraphEdge
    // Directed (but navigable both ways) connection between two nodes.
    // Stores the densely-sampled then RDP-simplified path as world-space points.
    // ─────────────────────────────────────────────────────────────────────────────
    [Serializable]
    public class GraphEdge
    {
        public string id;               // GUID — unique across all sessions
        public string nodeA;            // Start node id
        public string nodeB;            // End node id
        public List<SerializableVector3> pathPoints = new List<SerializableVector3>();
        public float  confidenceScore       = 1f;
        public int    contributingWalkCount = 1;
        public long   lastVerifiedAt;

        public GraphEdge() {}

        public GraphEdge(string nodeA, string nodeB, List<Vector3> points)
        {
            this.id    = Guid.NewGuid().ToString("N");
            this.nodeA = nodeA;
            this.nodeB = nodeB;
            this.pathPoints = new List<SerializableVector3>();
            if (points != null)
                foreach (var p in points)
                    this.pathPoints.Add(new SerializableVector3(p));
            this.confidenceScore       = 1f;
            this.contributingWalkCount = 1;
            this.lastVerifiedAt        = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        /// <summary>Total arc length of the stored path in meters.</summary>
        public float CalculateLength()
        {
            if (pathPoints == null || pathPoints.Count < 2) return 0f;
            float total = 0f;
            for (int i = 0; i < pathPoints.Count - 1; i++)
                total += Vector3.Distance(pathPoints[i].ToVector3(), pathPoints[i + 1].ToVector3());
            return total;
        }

        /// <summary>
        /// Effective cost for A*: longer edges are more expensive; low-confidence
        /// edges are penalised (user reported them as wrong).
        /// </summary>
        public float AStarCost()
        {
            float len = CalculateLength();
            float conf = Mathf.Clamp(confidenceScore, 0.1f, 2f);
            return len / conf;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // BuildingGraph
    // The full in-memory and on-disk representation of all recorded routes for one
    // building. Includes A* path-finding and local JSON persistence.
    // ─────────────────────────────────────────────────────────────────────────────
    [Serializable]
    public class BuildingGraph
    {
        public string           buildingId = "main_building";
        public List<GraphNode>  nodes      = new List<GraphNode>();
        public List<GraphEdge>  edges      = new List<GraphEdge>();

        // ── CRUD ────────────────────────────────────────────────────────────────

        /// <summary>Add or replace a node (matched by GUID id).</summary>
        public void AddNode(GraphNode node)
        {
            nodes.RemoveAll(n => n.id == node.id);
            nodes.Add(node);
        }

        /// <summary>Add or replace an edge (matched by GUID id).</summary>
        public void AddEdge(GraphEdge edge)
        {
            edges.RemoveAll(e => e.id == edge.id);
            edges.Add(edge);
        }

        public GraphNode       GetNode(string id)     => nodes.Find(n => n.id == id);
        public List<GraphEdge> EdgesFor(string nodeId) => edges.FindAll(e => e.nodeA == nodeId || e.nodeB == nodeId);

        // ── A* ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns the list of edges that form the shortest (confidence-weighted)
        /// path from startNodeId to targetNodeId, or null if unreachable.
        /// </summary>
        public List<GraphEdge> FindRoute(string startNodeId, string targetNodeId)
        {
            if (startNodeId == targetNodeId) return new List<GraphEdge>();

            GraphNode startNode  = GetNode(startNodeId);
            GraphNode targetNode = GetNode(targetNodeId);
            if (startNode == null || targetNode == null) return null;

            var gScore  = new Dictionary<string, float>();
            var fScore  = new Dictionary<string, float>();
            var cameFromNode = new Dictionary<string, string>();
            var cameFromEdge = new Dictionary<string, GraphEdge>();
            var openSet  = new HashSet<string>();
            var closedSet = new HashSet<string>();

            foreach (var n in nodes)
            {
                gScore[n.id] = float.MaxValue;
                fScore[n.id] = float.MaxValue;
            }

            gScore[startNodeId] = 0f;
            fScore[startNodeId] = Heuristic(startNode, targetNode);
            openSet.Add(startNodeId);

            while (openSet.Count > 0)
            {
                // Pick lowest fScore in open set
                string current  = null;
                float  minScore = float.MaxValue;
                foreach (string s in openSet)
                {
                    if (fScore.TryGetValue(s, out float fs) && fs < minScore)
                    {
                        minScore = fs;
                        current  = s;
                    }
                }
                if (current == null || current == targetNodeId) break;

                openSet.Remove(current);
                closedSet.Add(current);

                foreach (var edge in EdgesFor(current))
                {
                    string neighbor = edge.nodeA == current ? edge.nodeB : edge.nodeA;
                    if (closedSet.Contains(neighbor)) continue;

                    float tentativeG = gScore[current] + edge.AStarCost();
                    if (!gScore.TryGetValue(neighbor, out float neighborG))
                        neighborG = float.MaxValue;

                    if (tentativeG < neighborG)
                    {
                        cameFromNode[neighbor] = current;
                        cameFromEdge[neighbor] = edge;
                        gScore[neighbor] = tentativeG;
                        fScore[neighbor] = tentativeG + Heuristic(GetNode(neighbor), targetNode);
                        openSet.Add(neighbor);
                    }
                }
            }

            if (!cameFromEdge.ContainsKey(targetNodeId)) return null;

            // Reconstruct path
            var route = new List<GraphEdge>();
            string curr = targetNodeId;
            while (curr != startNodeId && cameFromEdge.TryGetValue(curr, out GraphEdge e))
            {
                route.Insert(0, e);
                curr = cameFromNode[curr];
            }
            return route;
        }

        private static float Heuristic(GraphNode a, GraphNode b)
        {
            if (a == null || b == null) return 0f;
            return Vector3.Distance(a.localPosition.ToVector3(), b.localPosition.ToVector3());
        }

        // ── Persistence ──────────────────────────────────────────────────────────

        public string ToJson()                   => JsonUtility.ToJson(this, prettyPrint: true);
        public static BuildingGraph FromJson(string json)
        {
            try   { return JsonUtility.FromJson<BuildingGraph>(json) ?? new BuildingGraph(); }
            catch { return new BuildingGraph(); }
        }

        public void SaveToFile(string path)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(path, ToJson());
        }

        public static BuildingGraph LoadFromFile(string path)
        {
            if (!File.Exists(path)) return new BuildingGraph();
            return FromJson(File.ReadAllText(path));
        }

        /// <summary>Default on-device save path.</summary>
        public static string DefaultPath =>
            Path.Combine(Application.persistentDataPath, "indoor_graph.json");
    }
}
