using System.Collections.Generic;
using UnityEngine;

namespace ARNav.Utils
{
    /// <summary>
    /// Ramer-Douglas-Peucker (RDP) algorithm for 3D polylines.
    /// Removes collinear / near-collinear points while preserving corners and turns.
    /// Used by PathRecorder to compress recorded walk data before storing in graph edges.
    /// </summary>
    public static class PathSimplifier
    {
        /// <summary>
        /// Simplify a list of 3D world-space points.
        /// </summary>
        /// <param name="points">Input point list (at least 2 points).</param>
        /// <param name="epsilon">Max allowed perpendicular deviation in metres. Default 0.15 m.</param>
        public static List<Vector3> Simplify(List<Vector3> points, float epsilon = 0.15f)
        {
            if (points == null || points.Count < 3)
                return points != null ? new List<Vector3>(points) : new List<Vector3>();

            var keepIndices = new List<int> { 0, points.Count - 1 };
            RDPRecurse(points, 0, points.Count - 1, epsilon, keepIndices);
            keepIndices.Sort();

            var result = new List<Vector3>(keepIndices.Count);
            foreach (int idx in keepIndices)
                result.Add(points[idx]);
            return result;
        }

        // ─── Internals ────────────────────────────────────────────────────────────

        private static void RDPRecurse(List<Vector3> pts, int first, int last,
                                       float epsilon, List<int> keep)
        {
            float maxDist  = 0f;
            int   maxIndex = 0;

            for (int i = first + 1; i < last; i++)
            {
                float d = PerpendicularDistance(pts[i], pts[first], pts[last]);
                if (d > maxDist) { maxDist = d; maxIndex = i; }
            }

            if (maxDist >= epsilon)
            {
                keep.Add(maxIndex);
                RDPRecurse(pts, first,    maxIndex, epsilon, keep);
                RDPRecurse(pts, maxIndex, last,     epsilon, keep);
            }
        }

        /// <summary>
        /// Perpendicular distance from point <paramref name="pt"/> to the line
        /// segment defined by <paramref name="a"/> → <paramref name="b"/>.
        /// </summary>
        private static float PerpendicularDistance(Vector3 pt, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float   lenSq = ab.sqrMagnitude;
            if (lenSq < 1e-6f) return Vector3.Distance(pt, a);

            float   t  = Mathf.Clamp01(Vector3.Dot(pt - a, ab) / lenSq);
            Vector3 proj = a + t * ab;
            return Vector3.Distance(pt, proj);
        }
    }
}
