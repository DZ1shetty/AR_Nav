using UnityEngine;

/// <summary>
/// Programmatically creates the AR Arrow mesh prefab at runtime.
/// Attach to an empty GameObject. The arrow will be created as a child.
/// Use this script's GameObject as your arrowPrefab in SequentialNavigator.
/// </summary>
public class ArrowMeshBuilder : MonoBehaviour
{
    [Header("Arrow Dimensions (meters)")]
    [SerializeField] private float shaftLength   = 0.5f;
    [SerializeField] private float shaftWidth    = 0.12f;
    [SerializeField] private float headLength    = 0.3f;
    [SerializeField] private float headWidth     = 0.35f;
    [SerializeField] private float thickness     = 0.04f;

    [Header("Glow Color")]
    [SerializeField] private Color glowColor = new Color(0.1f, 0.85f, 1f, 0.92f); // Cyan

    private MeshFilter   _meshFilter;
    private MeshRenderer _meshRenderer;

    private void Awake()
    {
        BuildArrowMesh();
        AddARArrowComponent();
    }

    private void BuildArrowMesh()
    {
        _meshFilter   = gameObject.AddComponent<MeshFilter>();
        _meshRenderer = gameObject.AddComponent<MeshRenderer>();

        Mesh mesh = new Mesh();
        mesh.name = "ARArrowMesh";

        // Arrow shape: shaft + triangular head (flat, lying on XZ plane)
        //
        //          ___________
        //         /           \    ← Head (triangle)
        //    ____/             \____
        //   |    Shaft              |
        //   |_______________________|

        float hw  = shaftWidth  * 0.5f;
        float hh  = headWidth   * 0.5f;
        float y   = thickness   * 0.5f;

        // Total length
        float totalLen = shaftLength + headLength;

        Vector3[] verts = new Vector3[]
        {
            // Shaft — bottom face
            new Vector3(-hw,  -y, 0),             // 0
            new Vector3( hw,  -y, 0),             // 1
            new Vector3( hw,  -y, shaftLength),   // 2
            new Vector3(-hw,  -y, shaftLength),   // 3

            // Shaft — top face
            new Vector3(-hw,   y, 0),             // 4
            new Vector3( hw,   y, 0),             // 5
            new Vector3( hw,   y, shaftLength),   // 6
            new Vector3(-hw,   y, shaftLength),   // 7

            // Head — bottom face (triangle)
            new Vector3(-hh,  -y, shaftLength),   // 8
            new Vector3( hh,  -y, shaftLength),   // 9
            new Vector3(  0,  -y, totalLen),       // 10

            // Head — top face
            new Vector3(-hh,   y, shaftLength),   // 11
            new Vector3( hh,   y, shaftLength),   // 12
            new Vector3(  0,   y, totalLen),       // 13
        };

        int[] tris = new int[]
        {
            // Shaft bottom
            0, 2, 1,  0, 3, 2,
            // Shaft top
            4, 5, 6,  4, 6, 7,
            // Shaft sides
            0, 1, 5,  0, 5, 4,   // front
            3, 7, 6,  3, 6, 2,   // back
            0, 4, 7,  0, 7, 3,   // left
            1, 2, 6,  1, 6, 5,   // right

            // Head bottom
            8, 10, 9,
            // Head top
            11, 12, 13,
            // Head sides
            8, 9, 12,  8, 12, 11,   // back face
            8, 11, 13, 8, 13, 10,   // left face
            9, 10, 13, 9, 13, 12,   // right face
        };

        mesh.vertices  = verts;
        mesh.triangles = tris;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        _meshFilter.mesh = mesh;

        // Material — use URP Lit or built-in transparent
        Material mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        if (mat.shader == null || mat.shader.name.Contains("Hidden"))
        {
            // Fallback to Standard if URP not found
            mat = new Material(Shader.Find("Standard"));
        }

        mat.color = glowColor;

        // Enable transparency
        mat.SetFloat("_Surface", 1);       // URP transparent mode
        mat.SetFloat("_Mode", 3);          // Standard fade mode
        mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        mat.SetInt("_ZWrite", 0);
        mat.EnableKeyword("_ALPHABLEND_ON");
        mat.renderQueue = 3000;

        // Emissive glow
        mat.EnableKeyword("_EMISSION");
        mat.SetColor("_EmissionColor", glowColor * 1.5f);

        _meshRenderer.material = mat;
    }

    private void AddARArrowComponent()
    {
        // ARArrow component handles animation
        if (GetComponent<ARArrow>() == null)
            gameObject.AddComponent<ARArrow>();
    }
}
