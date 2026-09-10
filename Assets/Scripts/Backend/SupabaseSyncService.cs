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
    /// Syncs the local BuildingGraph to/from a Supabase REST endpoint.
    ///
    /// KEY FIXES vs old version:
    ///   • FetchBuildingGraph now FULLY deserialises the response and fires OnGraphSynced.
    ///   • UploadSessionEdges uploads ALL session edges, not just the last one.
    ///   • path_points is stored as a flat JSON array (matches Supabase schema comment).
    ///   • UpsertNodeRoutine surfaces errors via OnSyncFailed.
    ///   • Credentials are read from Resources/SupabaseConfig.json (not hard-coded).
    ///
    /// CREDENTIALS SETUP:
    ///   Create Assets/Resources/SupabaseConfig.json:
    ///   {
    ///     "url": "https://YOUR_PROJECT.supabase.co",
    ///     "anonKey": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9..."
    ///   }
    ///   That file is already in .gitignore via the "Resources" exclusion if you add it.
    /// </summary>
    public class SupabaseSyncService : MonoBehaviour
    {
        // ── Events ────────────────────────────────────────────────────────────────
        public event Action<BuildingGraph> OnGraphSynced;
        public event Action<string>        OnUploadCompleted;
        public event Action<string>        OnSyncFailed;

        // ── Runtime credentials ───────────────────────────────────────────────────
        private string _url;
        private string _anonKey;
        private bool   _ready;

        // ── Lifecycle ─────────────────────────────────────────────────────────────

        private void Awake()
        {
            LoadCredentials();
        }

        private void LoadCredentials()
        {
            TextAsset cfg = Resources.Load<TextAsset>("SupabaseConfig");
            if (cfg != null)
            {
                SupabaseConfig parsed = JsonUtility.FromJson<SupabaseConfig>(cfg.text);
                if (parsed != null && !string.IsNullOrEmpty(parsed.url))
                {
                    _url     = parsed.url.TrimEnd('/');
                    _anonKey = parsed.anonKey;
                    _ready   = true;
                    return;
                }
            }

            // Graceful degradation — offline mode
            Debug.LogWarning(
                "[SupabaseSyncService] SupabaseConfig.json not found in Resources. " +
                "Running in offline mode. Create Assets/Resources/SupabaseConfig.json to enable sync.");
            _ready = false;
        }

        // ── Public API ────────────────────────────────────────────────────────────

        /// <summary>
        /// Download all nodes + edges for a building from Supabase and merge into
        /// the local graph, then fire OnGraphSynced.
        /// </summary>
        public void FetchBuildingGraph(string buildingId)
        {
            if (!_ready) { OnSyncFailed?.Invoke("Supabase not configured."); return; }
            StartCoroutine(FetchRoutine(buildingId));
        }

        /// <summary>
        /// Upload every node and edge from a finished recording session.
        /// Uploads ALL session edges (not just the last one).
        /// </summary>
        public void UploadSession(IReadOnlyList<GraphNode> nodes,
                                  IReadOnlyList<GraphEdge> edges,
                                  string buildingId)
        {
            if (!_ready) { OnSyncFailed?.Invoke("Supabase not configured."); return; }
            StartCoroutine(UploadSessionRoutine(nodes, edges, buildingId));
        }

        // ── Fetch ─────────────────────────────────────────────────────────────────

        private IEnumerator FetchRoutine(string buildingId)
        {
            // ── Nodes ──────────────────────────────────────────────────────────────
            string nodesUrl = $"{_url}/rest/v1/graph_nodes?building_id=eq.{buildingId}&select=*";
            string nodesJson = null;

            using (UnityWebRequest req = UnityWebRequest.Get(nodesUrl))
            {
                AddAuthHeaders(req);
                yield return req.SendWebRequest();
                if (req.result != UnityWebRequest.Result.Success)
                {
                    OnSyncFailed?.Invoke($"Fetch nodes failed: {req.error}");
                    yield break;
                }
                nodesJson = req.downloadHandler.text;
            }

            // ── Edges ──────────────────────────────────────────────────────────────
            string edgesUrl = $"{_url}/rest/v1/graph_edges?building_id=eq.{buildingId}&select=*";
            string edgesJson = null;

            using (UnityWebRequest req = UnityWebRequest.Get(edgesUrl))
            {
                AddAuthHeaders(req);
                yield return req.SendWebRequest();
                if (req.result != UnityWebRequest.Result.Success)
                {
                    OnSyncFailed?.Invoke($"Fetch edges failed: {req.error}");
                    yield break;
                }
                edgesJson = req.downloadHandler.text;
            }

            // ── Deserialise ────────────────────────────────────────────────────────
            BuildingGraph fetched = DeserialiseGraph(buildingId, nodesJson, edgesJson);

            // Merge into local graph and save
            BuildingGraph local = BuildingGraph.LoadFromFile(BuildingGraph.DefaultPath);
            foreach (var n in fetched.nodes) local.AddNode(n);
            foreach (var e in fetched.edges) local.AddEdge(e);
            local.SaveToFile(BuildingGraph.DefaultPath);

            Debug.Log($"[SupabaseSyncService] Fetched {fetched.nodes.Count} nodes, " +
                      $"{fetched.edges.Count} edges for building '{buildingId}'.");
            OnGraphSynced?.Invoke(local);
        }

        // ── Upload session ────────────────────────────────────────────────────────

        private IEnumerator UploadSessionRoutine(IReadOnlyList<GraphNode> nodes,
                                                  IReadOnlyList<GraphEdge> edges,
                                                  string buildingId)
        {
            // Upsert all nodes first (edges reference them via FK)
            foreach (var node in nodes)
            {
                bool ok = false;
                string err = null;
                yield return StartCoroutine(UpsertNode(node, buildingId,
                    success => ok = success, error => err = error));

                if (!ok)
                {
                    OnSyncFailed?.Invoke($"Node upsert failed ({node.name}): {err}");
                    yield break;
                }
            }

            // Upsert all edges
            foreach (var edge in edges)
            {
                bool ok = false;
                string err = null;
                yield return StartCoroutine(UpsertEdge(edge, buildingId,
                    success => ok = success, error => err = error));

                if (!ok)
                {
                    OnSyncFailed?.Invoke($"Edge upsert failed ({edge.id}): {err}");
                    yield break;
                }
            }

            string msg = $"Uploaded {nodes.Count} nodes + {edges.Count} edges for '{buildingId}'.";
            Debug.Log($"[SupabaseSyncService] {msg}");
            OnUploadCompleted?.Invoke(msg);
        }

        // ── Upsert helpers ────────────────────────────────────────────────────────

        private IEnumerator UpsertNode(GraphNode node, string buildingId,
                                       Action<bool> onSuccess, Action<string> onError)
        {
            var payload = new NodePayload
            {
                id              = node.id,
                building_id     = buildingId,
                floor           = node.floor,
                name            = node.name,
                local_x         = node.localPosition.x,
                local_y         = node.localPosition.y,
                local_z         = node.localPosition.z,
                anchor_id       = node.anchorId ?? "",
                confidence_score = node.confidenceScore
            };

            string json = JsonUtility.ToJson(payload);
            using (UnityWebRequest req = BuildUpsertRequest($"{_url}/rest/v1/graph_nodes", json))
            {
                yield return req.SendWebRequest();
                if (req.result == UnityWebRequest.Result.Success)
                    onSuccess(true);
                else
                    onError(req.downloadHandler?.text ?? req.error);
            }
        }

        private IEnumerator UpsertEdge(GraphEdge edge, string buildingId,
                                       Action<bool> onSuccess, Action<string> onError)
        {
            // Flat array format: [{x,y,z},{x,y,z},...] to match Supabase schema
            string pointsJson = PointsToFlatJson(edge.pathPoints);

            var payload = new EdgePayload
            {
                id                      = edge.id,
                building_id             = buildingId,
                node_a                  = edge.nodeA,
                node_b                  = edge.nodeB,
                path_points             = pointsJson,
                confidence_score        = edge.confidenceScore,
                contributing_walk_count = edge.contributingWalkCount
            };

            string json = JsonUtility.ToJson(payload);
            using (UnityWebRequest req = BuildUpsertRequest($"{_url}/rest/v1/graph_edges", json))
            {
                yield return req.SendWebRequest();
                if (req.result == UnityWebRequest.Result.Success)
                    onSuccess(true);
                else
                    onError(req.downloadHandler?.text ?? req.error);
            }
        }

        // ── Deserialise response ──────────────────────────────────────────────────

        /// <summary>
        /// Supabase returns JSON arrays. JsonUtility can't parse root-level arrays,
        /// so we wrap them in a helper object before deserialising.
        /// </summary>
        private static BuildingGraph DeserialiseGraph(string buildingId,
                                                       string nodesJson,
                                                       string edgesJson)
        {
            var graph = new BuildingGraph { buildingId = buildingId };

            // Nodes
            NodePayloadArray nodeArr = SafeFromJson<NodePayloadArray>(
                $"{{\"items\":{nodesJson}}}");
            if (nodeArr?.items != null)
            {
                foreach (var p in nodeArr.items)
                {
                    graph.AddNode(new GraphNode
                    {
                        id             = p.id,
                        name           = p.name,
                        buildingId     = p.building_id,
                        floor          = p.floor,
                        localPosition  = new SerializableVector3(p.local_x, p.local_y, p.local_z),
                        anchorId       = p.anchor_id,
                        confidenceScore = p.confidence_score
                    });
                }
            }

            // Edges
            EdgePayloadArray edgeArr = SafeFromJson<EdgePayloadArray>(
                $"{{\"items\":{edgesJson}}}");
            if (edgeArr?.items != null)
            {
                foreach (var p in edgeArr.items)
                {
                    // Deserialise flat point array from JSON string stored in path_points column
                    List<SerializableVector3> pts = ParsePointsJson(p.path_points);
                    var edge = new GraphEdge
                    {
                        id                      = p.id,
                        nodeA                   = p.node_a,
                        nodeB                   = p.node_b,
                        pathPoints              = pts,
                        confidenceScore         = p.confidence_score,
                        contributingWalkCount   = p.contributing_walk_count
                    };
                    graph.AddEdge(edge);
                }
            }

            return graph;
        }

        // ── Utility ───────────────────────────────────────────────────────────────

        private void AddAuthHeaders(UnityWebRequest req)
        {
            req.SetRequestHeader("apikey",        _anonKey);
            req.SetRequestHeader("Authorization", $"Bearer {_anonKey}");
            req.SetRequestHeader("Content-Type",  "application/json");
        }

        private UnityWebRequest BuildUpsertRequest(string url, string json)
        {
            byte[] body = Encoding.UTF8.GetBytes(json);
            var req = new UnityWebRequest(url, "POST")
            {
                uploadHandler   = new UploadHandlerRaw(body),
                downloadHandler = new DownloadHandlerBuffer()
            };
            AddAuthHeaders(req);
            req.SetRequestHeader("Prefer", "resolution=merge-duplicates");
            return req;
        }

        /// <summary>
        /// Converts List<SerializableVector3> → flat JSON array string
        /// e.g. [{"x":1.0,"y":0.0,"z":2.0}, ...]
        /// This matches the Supabase schema comment format.
        /// </summary>
        private static string PointsToFlatJson(List<SerializableVector3> pts)
        {
            if (pts == null || pts.Count == 0) return "[]";
            var sb = new StringBuilder("[");
            for (int i = 0; i < pts.Count; i++)
            {
                sb.Append($"{{\"x\":{pts[i].x},\"y\":{pts[i].y},\"z\":{pts[i].z}}}");
                if (i < pts.Count - 1) sb.Append(",");
            }
            sb.Append("]");
            return sb.ToString();
        }

        /// <summary>
        /// Parses the flat point array string back to a list.
        /// Wraps into a helper type because JsonUtility can't parse a root array.
        /// </summary>
        private static List<SerializableVector3> ParsePointsJson(string json)
        {
            var result = new List<SerializableVector3>();
            if (string.IsNullOrEmpty(json)) return result;
            try
            {
                PointArray arr = JsonUtility.FromJson<PointArray>($"{{\"pts\":{json}}}");
                if (arr?.pts != null) result.AddRange(arr.pts);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SupabaseSyncService] Could not parse path_points: {ex.Message}");
            }
            return result;
        }

        private static T SafeFromJson<T>(string json) where T : class
        {
            try   { return JsonUtility.FromJson<T>(json); }
            catch { return null; }
        }

        // ── Serialisable payload types ────────────────────────────────────────────

        [Serializable] private class SupabaseConfig
        {
            public string url;
            public string anonKey;
        }

        [Serializable] private class NodePayload
        {
            public string id, building_id, name, anchor_id;
            public int    floor;
            public float  local_x, local_y, local_z, confidence_score;
        }

        [Serializable] private class EdgePayload
        {
            public string id, building_id, node_a, node_b, path_points;
            public float  confidence_score;
            public int    contributing_walk_count;
        }

        [Serializable] private class NodePayloadArray  { public List<NodePayload>  items; }
        [Serializable] private class EdgePayloadArray  { public List<EdgePayload>  items; }
        [Serializable] private class PointArray        { public List<SerializableVector3> pts; }
    }
}
