using System.Collections;
using System.Collections.Generic;
using Kuchinashi.Utils.Progressable;
using UnityEngine;

public class RippleController : MonoBehaviour
{
    [Header("Settings")]
    public float rippleSpeed = 0.5f;
    public float rippleInterval = 1f;

    [SerializeField] private Progressable progressable;
    private Coroutine rippleCoroutine;

    void OnEnable()
    {
        rippleCoroutine = StartCoroutine(RippleCoroutine());
    }

    void OnDisable()
    {
        StopCoroutine(rippleCoroutine);
    }

    IEnumerator RippleCoroutine()
    {
        while (true)
        {
            progressable.Progress = 0f;
            AudioMng.Instance.PlaySfx("Confuse", 0.8f);
            yield return progressable.LinearTransition(rippleSpeed, delay: 0f);

            yield return new WaitForSeconds(rippleInterval);
        }
    }
}
