using System.Collections.Generic;
using UnityEngine;

namespace ARNav.Utils
{
    /// <summary>
    /// Ramer-Douglas-Peucker (RDP) algorithm extended for 3D polylines.
    /// Prunes noise and collinear points while strictly preserving corners and turns.
    /// </summary>
    public static class PathSimplifier
    {
        public static List<Vector3> Simplify(List<Vector3> pointList, float epsilon = 0.15f)
        {
            if (pointList == null || pointList.Count < 3)
                return pointList != null ? new List<Vector3>(pointList) : new List<Vector3>();

            int firstIndex = 0;
            int lastIndex = pointList.Count - 1;
            List<int> pointIndicesToKeep = new List<int>();

            // Always keep start and end
            pointIndicesToKeep.Add(firstIndex);
            pointIndicesToKeep.Add(lastIndex);

            RDPStep(pointList, firstIndex, lastIndex, epsilon, ref pointIndicesToKeep);

            pointIndicesToKeep.Sort();

            List<Vector3> returnPoints = new List<Vector3>(pointIndicesToKeep.Count);
            foreach (int index in pointIndicesToKeep)
            {
                returnPoints.Add(pointList[index]);
            }

            return returnPoints;
        }

        private static void RDPStep(List<Vector3> points, int first, int last, float epsilon, ref List<int> keepIndices)
        {
            float maxDistance = 0f;
            int maxIndex = 0;

            Vector3 p1 = points[first];
            Vector3 p2 = points[last];

            for (int i = first + 1; i < last; i++)
            {
                float dist = PerpendicularDistance3D(points[i], p1, p2);
                if (dist > maxDistance)
                {
                    maxDistance = dist;
                    maxIndex = i;
                }
            }

            if (maxDistance >= epsilon)
            {
                keepIndices.Add(maxIndex);
                RDPStep(points, first, maxIndex, epsilon, ref keepIndices);
                RDPStep(points, maxIndex, last, epsilon, ref keepIndices);
            }
        }

        private static float PerpendicularDistance3D(Vector3 pt, Vector3 lineStart, Vector3 lineEnd)
        {
            Vector3 line = lineEnd - lineStart;
            float lineLenSq = line.sqrMagnitude;

            if (lineLenSq < 1e-6f)
                return Vector3.Distance(pt, lineStart);

            // Project pt onto line segment
            float t = Vector3.Dot(pt - lineStart, line) / lineLenSq;
            t = Mathf.Clamp01(t);

            Vector3 projection = lineStart + t * line;
            return Vector3.Distance(pt, projection);
        }
    }
}
