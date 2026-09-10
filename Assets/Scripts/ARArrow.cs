using System.Collections;
using UnityEngine;

/// <summary>
/// Component on the AR Arrow prefab.
/// Handles appear/disappear animations and the idle pulse effect.
/// </summary>
[RequireComponent(typeof(Renderer))]
public class ARArrow : MonoBehaviour
{
    [Header("Animation Settings")]
    [SerializeField] private float fadeInDuration  = 0.4f;
    [SerializeField] private float fadeOutDuration = 0.3f;
    [SerializeField] private float pulseSpeed      = 1.5f;
    [SerializeField] private float pulseAmount     = 0.08f;  // Scale variation (±8%)
    [SerializeField] private float bobHeight       = 0.03f;  // Vertical bob (3cm)

    private Renderer[]  _renderers;
    private Vector3     _baseScale;
    private float       _baseY;
    private bool        _isPulsing = false;

    private void Awake()
    {
        _renderers = GetComponentsInChildren<Renderer>(true);
        _baseScale = transform.localScale;
        _baseY     = transform.position.y;
    }

    private void Update()
    {
        if (!_isPulsing) return;

        // Gentle pulse: scale breathes in and out
        float pulse = 1f + Mathf.Sin(Time.time * pulseSpeed) * pulseAmount;
        transform.localScale = _baseScale * pulse;

        // Gentle vertical bob
        Vector3 pos = transform.position;
        pos.y = _baseY + Mathf.Sin(Time.time * pulseSpeed * 0.7f) * bobHeight;
        transform.position = pos;
    }

    /// <summary>Call when arrow spawns — fades in from transparent.</summary>
    public void AnimateIn()
    {
        StartCoroutine(FadeIn());
    }

    /// <summary>Call to fade out and destroy. Awaitable from SequentialNavigator.</summary>
    public IEnumerator AnimateOut()
    {
        _isPulsing = false;
        yield return FadeOut();
        Destroy(gameObject);
    }

    private IEnumerator FadeIn()
    {
        SetAlpha(0f);
        transform.localScale = Vector3.zero;

        float elapsed = 0f;
        while (elapsed < fadeInDuration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / fadeInDuration);
            float eased = EaseOutBack(t);

            SetAlpha(eased);
            transform.localScale = _baseScale * eased;
            yield return null;
        }

        SetAlpha(1f);
        transform.localScale = _baseScale;
        _isPulsing = true;
    }

    private IEnumerator FadeOut()
    {
        float startAlpha = GetAlpha();
        float elapsed = 0f;

        while (elapsed < fadeOutDuration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / fadeOutDuration);
            SetAlpha(Mathf.Lerp(startAlpha, 0f, t));
            transform.localScale = _baseScale * (1f - t);
            yield return null;
        }

        SetAlpha(0f);
    }

    private void SetAlpha(float alpha)
    {
        foreach (Renderer r in _renderers)
        {
            foreach (Material mat in r.materials)
            {
                if (mat.HasProperty("_Color"))
                {
                    Color c = mat.color;
                    c.a = alpha;
                    mat.color = c;
                }
                // For URP/HDRP surface alpha
                if (mat.HasProperty("_BaseColor"))
                {
                    Color c = mat.GetColor("_BaseColor");
                    c.a = alpha;
                    mat.SetColor("_BaseColor", c);
                }
            }
        }
    }

    private float GetAlpha()
    {
        if (_renderers.Length > 0 && _renderers[0].material.HasProperty("_BaseColor"))
            return _renderers[0].material.GetColor("_BaseColor").a;
        if (_renderers.Length > 0 && _renderers[0].material.HasProperty("_Color"))
            return _renderers[0].material.color.a;
        return 1f;
    }

    // Easing function — smooth spring-like appear
    private float EaseOutBack(float t)
    {
        const float c1 = 1.70158f;
        const float c3 = c1 + 1f;
        return 1f + c3 * Mathf.Pow(t - 1f, 3f) + c1 * Mathf.Pow(t - 1f, 2f);
    }
}
