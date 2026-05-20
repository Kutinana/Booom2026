using System.Collections;
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

    private void Awake()
    {
        CacheTargetSpriteRenderer();
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

    private void CacheTargetSpriteRenderer()
    {
        targetSpriteRenderer = null;
        if (Target != null)
        {
            Target.TryGetComponent(out targetSpriteRenderer);
        }
    }

    private void SetTargetSpriteVisible(bool visible)
    {
        if (targetSpriteRenderer == null)
        {
            CacheTargetSpriteRenderer();
        }

        if (targetSpriteRenderer != null)
        {
            targetSpriteRenderer.enabled = visible;
        }
    }

    IEnumerator OpenRoutine()
    {
        SetTargetSpriteVisible(true);


        Vector3 originalScale = Vector3.one;


        Target.localScale = new Vector3(originalScale.x, 0f, originalScale.z);

        float timer = 0f;

        while (timer < OpenDuration)
        {
            timer += Time.deltaTime;

            float t = timer / OpenDuration;

            t = 1f - Mathf.Pow(1f - t, 3f);


            float y = Mathf.Lerp(0f, OvershootScaleY, t);

            Target.localScale = new Vector3(
                originalScale.x,
                y,
                originalScale.z
            );

            yield return null;
        }


        Target.localScale = originalScale;
    }

    IEnumerator CloseRoutine()
    {
        Vector3 originalScale = Target.localScale;

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

        SetTargetSpriteVisible(false);
    }
}