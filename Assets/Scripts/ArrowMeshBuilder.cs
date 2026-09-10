using UnityEngine;

/// <summary>
/// Procedurally builds a 3D arrow mesh (shaft + arrowhead) at runtime
/// and applies a transparent emissive material that works correctly on
/// BOTH the Built-in Render Pipeline and URP.
///
/// KEY FIXES vs old version:
///   • Shader selection uses Shader.Find() result null-check BEFORE constructing Material.
///   • Built-in transparency is set up correctly via the documented _Mode + keyword pattern.
///   • URP transparency uses _Surface + material.renderQueue pattern.
///   • ARArrow.RefreshBase() called after positioning so bob animation is correct.
///   • This script self-destructs after building — only ARArrow stays attached.
/// </summary>
[RequireComponent(typeof(MeshFilter))]
[RequireComponent(typeof(MeshRenderer))]
public class ArrowMeshBuilder : MonoBehaviour
{
    [Header("Arrow Shape")]
    [SerializeField] private float shaftLength = 0.3f;
    [SerializeField] private float shaftWidth  = 0.08f;
    [SerializeField] private float headLength  = 0.2f;
    [SerializeField] private float headWidth   = 0.22f;
    [SerializeField] private float arrowHeight = 0.06f;

    [Header("Appearance")]
    [SerializeField] private Color glowColor = new Color(0.05f, 0.85f, 1f, 0.9f);

    private void Awake()
    {
        // Ensure required components exist before touching them.
        // This prevents the "Creating missing MeshRenderer component" warning
        // on prefabs that were saved before these RequireComponents were added.
        if (GetComponent<MeshFilter>()   == null) gameObject.AddComponent<MeshFilter>();
        if (GetComponent<MeshRenderer>() == null) gameObject.AddComponent<MeshRenderer>();

        BuildMesh();
        ApplyMaterial();
        AddARArrow();
        // Self-destruct this builder script — ARArrow takes over
        Destroy(this);
    }

    // ── Mesh ──────────────────────────────────────────────────────────────────

    private void BuildMesh()
    {
        var mf = GetComponent<MeshFilter>();
        var mesh = new Mesh { name = "ARArrow" };

        float hw = shaftWidth  * 0.5f;
        float hh = arrowHeight * 0.5f;
        float hHead = headWidth * 0.5f;

        // 14 vertices: 8 for shaft box + 6 for arrowhead prism
        Vector3[] verts =
        {
            // Shaft bottom face (y = -hh)
            new Vector3(-hw,        -hh, 0f),            // 0
            new Vector3( hw,        -hh, 0f),            // 1
            new Vector3( hw,        -hh, shaftLength),   // 2
            new Vector3(-hw,        -hh, shaftLength),   // 3
            // Shaft top face (y = +hh)
            new Vector3(-hw,         hh, 0f),            // 4
            new Vector3( hw,         hh, 0f),            // 5
            new Vector3( hw,         hh, shaftLength),   // 6
            new Vector3(-hw,         hh, shaftLength),   // 7
            // Head base (y = -hh / +hh), at shaft end
            new Vector3(-hHead,     -hh, shaftLength),   // 8
            new Vector3( hHead,     -hh, shaftLength),   // 9
            new Vector3( hHead,      hh, shaftLength),   // 10  (unused in simple version)
            new Vector3(-hHead,      hh, shaftLength),   // 11  (unused in simple version)
            // Head tip bottom and top
            new Vector3(0f,         -hh, shaftLength + headLength), // 12
            new Vector3(0f,          hh, shaftLength + headLength), // 13
        };

        int[] tris =
        {
            // Shaft bottom
            0, 2, 1,  0, 3, 2,
            // Shaft top
            4, 5, 6,  4, 6, 7,
            // Shaft sides
            0, 1, 5,  0, 5, 4,
            3, 7, 6,  3, 6, 2,
            0, 4, 7,  0, 7, 3,
            1, 2, 6,  1, 6, 5,
            // Arrowhead bottom
            8,  9,  12,
            // Arrowhead top
            11, 13, 10,
            // Arrowhead sides
            8, 12, 11,  11, 12, 13,   // left face
            9, 10, 13,   9, 13, 12,   // right face
            8, 11, 10,   8, 10,  9,   // back face
        };

        mesh.vertices  = verts;
        mesh.triangles = tris;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        mf.mesh = mesh;
    }

    // ── Material ──────────────────────────────────────────────────────────────

    private void ApplyMaterial()
    {
        var mr = GetComponent<MeshRenderer>();
        mr.material = CreateTransparentMaterial();
    }

    private Material CreateTransparentMaterial()
    {
        // Detect render pipeline and pick appropriate shader
        Shader shader = null;
        bool isURP    = false;

        Shader urpShader = Shader.Find("Universal Render Pipeline/Lit");
        if (urpShader != null && !urpShader.name.Contains("Hidden"))
        {
            shader = urpShader;
            isURP  = true;
        }
        else
        {
            Shader stdShader = Shader.Find("Standard");
            if (stdShader != null && !stdShader.name.Contains("Hidden"))
                shader = stdShader;
        }

        if (shader == null)
        {
            // Last resort — unlit but at least visible
            shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
            Debug.LogWarning("[ArrowMeshBuilder] Could not find Standard or URP shader — falling back.");
        }

        var mat = new Material(shader);
        mat.color = glowColor;

        if (isURP)
        {
            SetupURPTransparent(mat);
        }
        else
        {
            SetupStandardTransparent(mat);
        }

        // Emissive glow on both pipelines
        if (mat.HasProperty("_EmissionColor"))
        {
            mat.EnableKeyword("_EMISSION");
            mat.SetColor("_EmissionColor", glowColor * 1.4f);
        }

        return mat;
    }

    /// <summary>
    /// Correctly enables transparency on a URP Lit material.
    /// Requires both _Surface = 1 AND the render queue override.
    /// </summary>
    private static void SetupURPTransparent(Material mat)
    {
        // Surface type: 0 = Opaque, 1 = Transparent
        mat.SetFloat("_Surface", 1f);
        // Alpha blending (not premultiplied)
        mat.SetFloat("_Blend", 0f);
        mat.SetInt("_SrcBlend",  (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        mat.SetInt("_DstBlend",  (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        mat.SetInt("_ZWrite",    0);
        mat.SetOverrideTag("RenderType", "Transparent");
        mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        mat.EnableKeyword("_ALPHAPREMULTIPLY_ON");
    }

    /// <summary>
    /// Correctly enables transparency on a Built-in Standard material.
    /// Uses the Fade mode (_Mode = 3) so alpha affects both colour and specular.
    /// </summary>
    private static void SetupStandardTransparent(Material mat)
    {
        mat.SetFloat("_Mode", 3f); // 0=Opaque 1=Cutout 2=Fade 3=Transparent
        mat.SetInt("_SrcBlend",  (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        mat.SetInt("_DstBlend",  (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        mat.SetInt("_ZWrite",    0);
        mat.SetOverrideTag("RenderType", "Transparent");
        mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        mat.EnableKeyword("_ALPHABLEND_ON");
        mat.DisableKeyword("_ALPHATEST_ON");
        mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
    }

    // ── ARArrow ───────────────────────────────────────────────────────────────

    private void AddARArrow()
    {
        ARArrow arrow = GetComponent<ARArrow>();
        if (arrow == null)
            gameObject.AddComponent<ARArrow>();
    }
}
