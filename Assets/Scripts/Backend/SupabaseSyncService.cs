using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using ARNav.Graph;

namespace ARNav.Backend
{
    /// <summary>
    /// Lightweight Supabase client using UnityWebRequest.
    /// Handles syncing nodes and edges with Supabase PostgREST endpoints without bulky SDK dependencies.
    /// </summary>
    public class SupabaseSyncService : MonoBehaviour
    {
        [Header("Supabase Configuration")]
        [SerializeField] private string supabaseUrl = "https://lytfjvhyojxgsncqpbvy.supabase.co";
        [SerializeField] private string supabaseAnonKey = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6Imx5dGZqdmh5b2p4Z3NuY3FwYnZ5Iiwicm9sZSI6ImFub24iLCJpYXQiOjE3ODg5NzA1MTYsImV4cCI6MjEwNDU0NjUxNn0.FD-1Vzz1argcPl3zJCtzGmgXzmN4StjKlNNt9SQfl-I";

        public event Action<BuildingGraph> OnGraphSynced;
        public event Action<string> OnSyncFailed;
        public event Action<string> OnUploadCompleted;

        [Serializable]
        private class SupabaseNodePayload
        {
            public string id;
            public string building_id;
            public int floor;
            public string name;
            public float local_x;
            public float local_y;
            public float local_z;
            public string anchor_id;
            public float confidence_score;
        }

        [Serializable]
        private class SupabaseEdgePayload
        {
            public string id;
            public string building_id;
            public int floor;
            public string node_a;
            public string node_b;
            public string path_points; // JSON string of points
            public float confidence_score;
            public int contributing_walk_count;
        }

        /// <summary>
        /// Fetches all nodes and edges for a building from Supabase.
        /// </summary>
        public void FetchBuildingGraph(string buildingId)
        {
            StartCoroutine(FetchGraphRoutine(buildingId));
        }

        private IEnumerator FetchGraphRoutine(string buildingId)
        {
            string nodesUrl = $"{supabaseUrl}/rest/v1/graph_nodes?building_id=eq.{buildingId}&select=*";
            using (UnityWebRequest req = UnityWebRequest.Get(nodesUrl))
            {
                req.SetRequestHeader("apikey", supabaseAnonKey);
                req.SetRequestHeader("Authorization", $"Bearer {supabaseAnonKey}");

                yield return req.SendWebRequest();

                if (req.result != UnityWebRequest.Result.Success)
                {
                    OnSyncFailed?.Invoke($"Failed to fetch nodes: {req.error}");
                    yield break;
                }

                // In production, deserialize array of nodes
                Debug.Log($"Fetched nodes from Supabase: {req.downloadHandler.text}");
            }

            string edgesUrl = $"{supabaseUrl}/rest/v1/graph_edges?building_id=eq.{buildingId}&select=*";
            using (UnityWebRequest req = UnityWebRequest.Get(edgesUrl))
            {
                req.SetRequestHeader("apikey", supabaseAnonKey);
                req.SetRequestHeader("Authorization", $"Bearer {supabaseAnonKey}");

                yield return req.SendWebRequest();

                if (req.result != UnityWebRequest.Result.Success)
                {
                    OnSyncFailed?.Invoke($"Failed to fetch edges: {req.error}");
                    yield break;
                }

                Debug.Log($"Fetched edges from Supabase: {req.downloadHandler.text}");
            }
        }

        /// <summary>
        /// Uploads a newly recorded edge and its endpoints to Supabase.
        /// </summary>
        public void UploadEdge(GraphNode startNode, GraphNode endNode, GraphEdge edge)
        {
            StartCoroutine(UploadEdgeRoutine(startNode, endNode, edge));
        }

        private IEnumerator UploadEdgeRoutine(GraphNode startNode, GraphNode endNode, GraphEdge edge)
        {
            // 1. Upsert Start Node
            yield return StartCoroutine(UpsertNodeRoutine(startNode));

            // 2. Upsert End Node
            yield return StartCoroutine(UpsertNodeRoutine(endNode));

            // 3. Upsert Edge
            string edgeUrl = $"{supabaseUrl}/rest/v1/graph_edges";
            var payload = new SupabaseEdgePayload
            {
                id = edge.id,
                building_id = startNode.buildingId,
                floor = startNode.floor,
                node_a = edge.nodeA,
                node_b = edge.nodeB,
                path_points = JsonUtility.ToJson(new SerializablePointsList { points = edge.pathPoints }),
                confidence_score = edge.confidenceScore,
                contributing_walk_count = edge.contributingWalkCount
            };

            string json = JsonUtility.ToJson(payload);

            using (UnityWebRequest req = new UnityWebRequest(edgeUrl, "POST"))
            {
                byte[] bodyRaw = Encoding.UTF8.GetBytes(json);
                req.uploadHandler = new UploadHandlerRaw(bodyRaw);
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                req.SetRequestHeader("apikey", supabaseAnonKey);
                req.SetRequestHeader("Authorization", $"Bearer {supabaseAnonKey}");
                req.SetRequestHeader("Prefer", "resolution=merge-duplicates");

                yield return req.SendWebRequest();

                if (req.result == UnityWebRequest.Result.Success)
                {
                    OnUploadCompleted?.Invoke($"Successfully uploaded edge {edge.id} to Supabase!");
                }
                else
                {
                    OnSyncFailed?.Invoke($"Edge upload error: {req.error} ({req.downloadHandler.text})");
                }
            }
        }

        private IEnumerator UpsertNodeRoutine(GraphNode node)
        {
            string url = $"{supabaseUrl}/rest/v1/graph_nodes";
            var payload = new SupabaseNodePayload
            {
                id = node.id,
                building_id = node.buildingId,
                floor = node.floor,
                name = node.name,
                local_x = node.localPosition.x,
                local_y = node.localPosition.y,
                local_z = node.localPosition.z,
                anchor_id = node.anchorId,
                confidence_score = node.confidenceScore
            };

            string json = JsonUtility.ToJson(payload);

            using (UnityWebRequest req = new UnityWebRequest(url, "POST"))
            {
                byte[] bodyRaw = Encoding.UTF8.GetBytes(json);
                req.uploadHandler = new UploadHandlerRaw(bodyRaw);
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                req.SetRequestHeader("apikey", supabaseAnonKey);
                req.SetRequestHeader("Authorization", $"Bearer {supabaseAnonKey}");
                req.SetRequestHeader("Prefer", "resolution=merge-duplicates");

                yield return req.SendWebRequest();
            }
        }

        [Serializable]
        private class SerializablePointsList
        {
            public List<SerializableVector3> points;
        }
    }
}
