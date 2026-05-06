using System.Collections;
using UnityEngine;

public class Barrier : MonoBehaviour
{
    public bool DefaultOpen = false;
    public GameObject BarrierClosed;
    public Animator Animator;
    public float AnimationDuration = 0.3f;

    private static readonly int BlendToOn = UnityEngine.Animator.StringToHash("BlendToOn");
    private static readonly int BlendToOff = UnityEngine.Animator.StringToHash("BlendToOff");

    private bool currentOpen;
    private bool initialized;
    private float blendValue;
    private Coroutine animationCoroutine;

    private void Awake()
    {
        if (Animator == null)
        {
            Animator = GetComponentInChildren<Animator>();
        }
    }

    private void Start()
    {
        UpdateBarrierState(DefaultOpen);
    }

    public void UpdateBarrierState(bool isOpen)
    {
        if (!initialized)
        {
            currentOpen = isOpen;
            initialized = true;
            blendValue = isOpen ? 0f : 1f;

            if (BarrierClosed != null)
            {
                BarrierClosed.SetActive(!isOpen);
            }

            SetBarrierBlend(blendValue, !isOpen);
            return;
        }

        bool wasOpen = currentOpen;
        currentOpen = isOpen;

        if (BarrierClosed != null)
        {
            BarrierClosed.SetActive(!isOpen);
        }

        UpdateBarrierAnimation(wasOpen, isOpen);
    }

    private void UpdateBarrierAnimation(bool wasOpen, bool isOpen)
    {
        if (wasOpen == isOpen)
        {
            return;
        }

        if (animationCoroutine != null)
        {
            StopCoroutine(animationCoroutine);
        }

        animationCoroutine = StartCoroutine(BlendBarrierAnimation(isOpen ? 0f : 1f));
    }

    private IEnumerator BlendBarrierAnimation(float targetValue)
    {
        float startValue = blendValue;
        bool blendToOn = targetValue > startValue;

        if (AnimationDuration <= 0f)
        {
            blendValue = targetValue;
            SetBarrierBlend(blendValue, blendToOn);
            animationCoroutine = null;
            yield break;
        }

        float elapsed = 0f;
        while (elapsed < AnimationDuration)
        {
            elapsed += Time.deltaTime;
            blendValue = Mathf.Lerp(startValue, targetValue, Mathf.Clamp01(elapsed / AnimationDuration));
            SetBarrierBlend(blendValue, blendToOn);
            yield return null;
        }

        blendValue = targetValue;
        SetBarrierBlend(blendValue, blendToOn);
        animationCoroutine = null;
    }

    private void SetBarrierBlend(float value, bool blendToOn)
    {
        if (Animator == null)
        {
            return;
        }

        value = Mathf.Clamp01(value);

        if (blendToOn)
        {
            Animator.SetFloat(BlendToOn, value);
            Animator.SetFloat(BlendToOff, 0f);
        }
        else
        {
            Animator.SetFloat(BlendToOn, 0f);
            Animator.SetFloat(BlendToOff, 1f - value);
        }
    }
}
