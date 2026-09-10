using System.Collections;
using UnityEngine;

/// <summary>
/// Attached to any AR waypoint arrow GameObject.
/// Handles:
///   • Spring-scale fade-in on spawn  (call AnimateIn())
///   • Gentle idle pulse + vertical bob while visible
///   • Smooth fade-out on removal     (call StartCoroutine(AnimateOut()))
///
/// FIX: _baseY is now refreshed every time the object is placed/moved via
/// RefreshBase(), so the bob always oscillates around the correct world Y.
/// </summary>
[RequireComponent(typeof(Renderer))]
public class ARArrow : MonoBehaviour
{
    [Header("Fade")]
    [SerializeField] private float fadeInDuration  = 0.35f;
    [SerializeField] private float fadeOutDuration = 0.25f;

    [Header("Idle Animation")]
    [SerializeField] private float pulseSpeed  = 1.6f;
    [SerializeField] private float pulseAmount = 0.08f;   // ±8% scale
    [SerializeField] private float bobHeight   = 0.03f;   // ±3 cm

    // ── State ──────────────────────────────────────────────────────────────────
    private Renderer[] _renderers;
    private Vector3    _baseScale;
    private float      _baseY;
    private bool       _pulsing;

    // ── Lifecycle ──────────────────────────────────────────────────────────────

    private void Awake()
    {
        _renderers = GetComponentsInChildren<Renderer>(true);
        CaptureBase();
    }

    private void Update()
    {
        if (!_pulsing) return;

        // Scale pulse
        float s = 1f + Mathf.Sin(Time.time * pulseSpeed) * pulseAmount;
        transform.localScale = _baseScale * s;

        // Vertical bob
        Vector3 p = transform.position;
        p.y = _baseY + Mathf.Sin(Time.time * pulseSpeed * 0.7f) * bobHeight;
        transform.position = p;
    }

    // ── Public API ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Call immediately after positioning the arrow.
    /// Re-captures base Y so the bob is correct, then fades in.
    /// </summary>
    public void AnimateIn()
    {
        CaptureBase();
        StopAllCoroutines();
        StartCoroutine(FadeIn());
    }

    /// <summary>
    /// Fades out then destroys the GameObject.
    /// Use: StartCoroutine(arrow.AnimateOut()) — or just call DestroyWithFade().
    /// </summary>
    public IEnumerator AnimateOut()
    {
        _pulsing = false;
        StopAllCoroutines();
        StartCoroutine(FadeOut());
        yield return new WaitForSeconds(fadeOutDuration + 0.05f);
        Destroy(gameObject);
    }

    /// <summary>
    /// Convenience wrapper so callers don't need to manage coroutines.
    /// </summary>
    public void DestroyWithFade()
    {
        StartCoroutine(AnimateOut());
    }

    /// <summary>
    /// Call if the arrow is repositioned after Awake (e.g. by PathNavigator).
    /// Updates _baseY so the bob oscillates around the new world position.
    /// </summary>
    public void RefreshBase()
    {
        CaptureBase();
    }

    // ── Internals ──────────────────────────────────────────────────────────────

    private void CaptureBase()
    {
        _baseScale = transform.localScale;
        _baseY     = transform.position.y;
    }

    private IEnumerator FadeIn()
    {
        SetAlpha(0f);
        transform.localScale = Vector3.zero;
        _pulsing = false;

        float elapsed = 0f;
        while (elapsed < fadeInDuration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / fadeInDuration);
            float e = EaseOutBack(t);
            SetAlpha(Mathf.Clamp01(e));
            transform.localScale = _baseScale * Mathf.Max(0f, e);
            yield return null;
        }

        SetAlpha(1f);
        transform.localScale = _baseScale;
        _pulsing = true;
    }

    private IEnumerator FadeOut()
    {
        float startAlpha = GetAlpha();
        Vector3 startScale = transform.localScale;
        float elapsed = 0f;

        while (elapsed < fadeOutDuration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / fadeOutDuration);
            SetAlpha(Mathf.Lerp(startAlpha, 0f, t));
            transform.localScale = startScale * (1f - t);
            yield return null;
        }

        SetAlpha(0f);
        transform.localScale = Vector3.zero;
    }

    private void SetAlpha(float alpha)
    {
        foreach (Renderer r in _renderers)
        {
            foreach (Material mat in r.materials)
            {
                // URP
                if (mat.HasProperty("_BaseColor"))
                {
                    Color c = mat.GetColor("_BaseColor");
                    c.a = alpha;
                    mat.SetColor("_BaseColor", c);
                }
                // Built-in / Standard
                if (mat.HasProperty("_Color"))
                {
                    Color c = mat.color;
                    c.a = alpha;
                    mat.color = c;
                }
            }
        }
    }

    private float GetAlpha()
    {
        if (_renderers.Length == 0) return 1f;
        Material mat = _renderers[0].material;
        if (mat.HasProperty("_BaseColor")) return mat.GetColor("_BaseColor").a;
        if (mat.HasProperty("_Color"))     return mat.color.a;
        return 1f;
    }

    // Spring-like ease used for scale-in
    private static float EaseOutBack(float t)
    {
        const float c1 = 1.70158f;
        const float c3 = c1 + 1f;
        return 1f + c3 * Mathf.Pow(t - 1f, 3f) + c1 * Mathf.Pow(t - 1f, 2f);
    }
}
