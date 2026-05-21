using System.Collections;
using TMPro;
using UnityEngine;

public class CRTSpriteEffect : MonoBehaviour
{
    public Transform Target;

    [Header("Open")]
    public float OpenDuration = 0.18f;

    [Header("Close")]
    public float CloseDuration = 0.12f;

    [Header("Overshoot")]
    public float OvershootScaleY = 1.08f;

    private Coroutine currentRoutine;
    private SpriteRenderer targetSpriteRenderer;
    private TMP_Text targetTmpText;
    private Vector3 restLocalScale = Vector3.one;

    private void Awake()
    {
        CacheTargetComponents();
        CacheRestLocalScale();
    }

    public void PlayOpen()
    {
        if (currentRoutine != null)
        {
            StopCoroutine(currentRoutine);
        }

        currentRoutine = StartCoroutine(OpenRoutine());
    }

    public void PlayClose()
    {
        if (currentRoutine != null)
        {
            StopCoroutine(currentRoutine);
        }

        currentRoutine = StartCoroutine(CloseRoutine());
    }

    private void CacheTargetComponents()
    {
        targetSpriteRenderer = null;
        targetTmpText = null;
        if (Target == null)
        {
            return;
        }

        Target.TryGetComponent(out targetSpriteRenderer);
        Target.TryGetComponent(out targetTmpText);
    }

    private void CacheRestLocalScale()
    {
        if (Target == null)
        {
            return;
        }

        var scale = Target.localScale;
        restLocalScale = new Vector3(scale.x, scale.y > 0f ? scale.y : 1f, scale.z);
    }

    private void SetTargetVisible(bool visible)
    {
        if (targetSpriteRenderer == null && targetTmpText == null)
        {
            CacheTargetComponents();
        }

        if (targetSpriteRenderer != null)
        {
            targetSpriteRenderer.enabled = visible;
        }

        if (targetTmpText != null)
        {
            targetTmpText.enabled = visible;
        }
    }

    IEnumerator OpenRoutine()
    {
        SetTargetVisible(true);

        var originalScale = restLocalScale;
        Target.localScale = new Vector3(originalScale.x, 0f, originalScale.z);

        float timer = 0f;

        while (timer < OpenDuration)
        {
            timer += Time.deltaTime;

            float t = timer / OpenDuration;
            t = 1f - Mathf.Pow(1f - t, 3f);

            float y = Mathf.Lerp(0f, OvershootScaleY * originalScale.y, t);

            Target.localScale = new Vector3(
                originalScale.x,
                y,
                originalScale.z
            );

            yield return null;
        }

        Target.localScale = originalScale;
        restLocalScale = originalScale;
    }

    IEnumerator CloseRoutine()
    {
        var originalScale = Target.localScale;
        if (originalScale.y > 0f)
        {
            restLocalScale = originalScale;
        }

        float timer = 0f;

        while (timer < CloseDuration)
        {
            timer += Time.deltaTime;

            float t = timer / CloseDuration;
            t = t * t;

            float y = Mathf.Lerp(originalScale.y, 0f, t);

            Target.localScale = new Vector3(
                originalScale.x,
                y,
                originalScale.z
            );

            yield return null;
        }

        Target.localScale = new Vector3(
            originalScale.x,
            0f,
            originalScale.z
        );

        SetTargetVisible(false);
    }
}
