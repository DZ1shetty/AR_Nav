using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace ARNav.Graph
{
    [Serializable]
    public struct SerializableVector3
    {
        public float x;
        public float y;
        public float z;

        public SerializableVector3(float x, float y, float z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }

        public SerializableVector3(Vector3 v)
        {
            x = v.x;
            y = v.y;
            z = v.z;
        }

        public Vector3 ToVector3() => new Vector3(x, y, z);
    }

    [Serializable]
    public class GraphNode
    {
        public string id;
        public string name;
        public string buildingId;
        public int floor;
        public SerializableVector3 localPosition;
        public string anchorId;
        public float confidenceScore = 1.0f;
        public long lastVerified;

        public GraphNode() {}

        public GraphNode(string id, string name, string buildingId, int floor, Vector3 position, string anchorId)
        {
            this.id = id;
            this.name = name;
            this.buildingId = buildingId;
            this.floor = floor;
            this.localPosition = new SerializableVector3(position);
            this.anchorId = anchorId;
            this.confidenceScore = 1.0f;
            this.lastVerified = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }
    }

    [Serializable]
    public class GraphEdge
    {
        public string id;
        public string nodeA;
        public string nodeB;
        public List<SerializableVector3> pathPoints = new List<SerializableVector3>();
        public float confidenceScore = 1.0f;
        public int contributingWalkCount = 1;
        public long lastVerifiedAt;

        public GraphEdge() {}

        public GraphEdge(string id, string nodeA, string nodeB, List<Vector3> points)
        {
            this.id = id;
            this.nodeA = nodeA;
            this.nodeB = nodeB;
            this.pathPoints = new List<SerializableVector3>();
            if (points != null)
            {
                foreach (var p in points)
                    this.pathPoints.Add(new SerializableVector3(p));
            }
            this.confidenceScore = 1.0f;
            this.contributingWalkCount = 1;
            this.lastVerifiedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        public float CalculateLength()
        {
            if (pathPoints == null || pathPoints.Count < 2) return 0f;
            float total = 0f;
            for (int i = 0; i < pathPoints.Count - 1; i++)
            {
                total += Vector3.Distance(pathPoints[i].ToVector3(), pathPoints[i + 1].ToVector3());
            }
            return total;
        }
    }

    [Serializable]
    public class BuildingGraph
    {
        public string buildingId = "main_building";
        public List<GraphNode> nodes = new List<GraphNode>();
        public List<GraphEdge> edges = new List<GraphEdge>();

        public void AddNode(GraphNode node)
        {
            nodes.RemoveAll(n => n.id == node.id);
            nodes.Add(node);
        }

        public void AddEdge(GraphEdge edge)
        {
            edges.RemoveAll(e => e.id == edge.id);
            edges.Add(edge);
        }

        public GraphNode GetNode(string id) => nodes.Find(n => n.id == id);

        public List<GraphEdge> GetConnectedEdges(string nodeId)
        {
            return edges.FindAll(e => e.nodeA == nodeId || e.nodeB == nodeId);
        }

        public List<GraphEdge> FindRoute(string startNodeId, string targetNodeId)
        {
            if (startNodeId == targetNodeId) return new List<GraphEdge>();

            var distances = new Dictionary<string, float>();
            var previousNode = new Dictionary<string, string>();
            var previousEdge = new Dictionary<string, GraphEdge>();
            var unvisited = new HashSet<string>();

            foreach (var node in nodes)
            {
                distances[node.id] = float.MaxValue;
                unvisited.Add(node.id);
            }

            if (!distances.ContainsKey(startNodeId) || !distances.ContainsKey(targetNodeId))
                return null;

            distances[startNodeId] = 0f;

            while (unvisited.Count > 0)
            {
                string current = null;
                float minDistance = float.MaxValue;

                foreach (var candidate in unvisited)
                {
                    if (distances[candidate] < minDistance)
                    {
                        minDistance = distances[candidate];
                        current = candidate;
                    }
                }

                if (current == null || minDistance == float.MaxValue) break;
                if (current == targetNodeId) break;

                unvisited.Remove(current);

                foreach (var edge in GetConnectedEdges(current))
                {
                    string neighbor = (edge.nodeA == current) ? edge.nodeB : edge.nodeA;
                    if (!unvisited.Contains(neighbor)) continue;

                    float edgeCost = edge.CalculateLength();
                    if (edge.confidenceScore > 0f)
                    {
                        edgeCost /= Mathf.Clamp(edge.confidenceScore, 0.2f, 2.0f);
                    }

                    float newDist = distances[current] + edgeCost;
                    if (newDist < distances[neighbor])
                    {
                        distances[neighbor] = newDist;
                        previousNode[neighbor] = current;
                        previousEdge[neighbor] = edge;
                    }
                }
            }

            if (!previousEdge.ContainsKey(targetNodeId))
                return null;

            var route = new List<GraphEdge>();
            string curr = targetNodeId;
            while (curr != startNodeId && previousEdge.TryGetValue(curr, out var edge))
            {
                route.Insert(0, edge);
                curr = previousNode[curr];
            }

            return route;
        }

        public string ToJson() => JsonUtility.ToJson(this, true);

        public static BuildingGraph FromJson(string json)
        {
            try
            {
                return JsonUtility.FromJson<BuildingGraph>(json);
            }
            catch
            {
                return new BuildingGraph();
            }
        }

        public void SaveToFile(string filePath)
        {
            string dir = Path.GetDirectoryName(filePath);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(filePath, ToJson());
        }

        public static BuildingGraph LoadFromFile(string filePath)
        {
            if (!File.Exists(filePath)) return new BuildingGraph();
            string json = File.ReadAllText(filePath);
            return FromJson(json);
        }
    }
}
