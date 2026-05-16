using System.Collections;
using UnityEngine;

public class FollowTransform : MonoBehaviour
{
    public Transform target;

    [Header("Follow Formula")]
    public Vector3 referencePosition;
    public float multiplier = 1f;
    public Vector3 offset;

    [Header("Additional Settings")]
    public bool followX = true;
    public bool followY = true;
    public bool followZ = true;

    public float thresholdDistance = 0.1f;

    [Header("Smooth Follow Settings")]
    public bool smoothFollow = false;
    public float smoothSpeed = 0.125f;

    private Coroutine m_SmoothFollowCoroutine;

    void FixedUpdate()
    {
        if (target == null) return;
        if (m_SmoothFollowCoroutine != null) return;

        Vector3 targetPosition = GetTargetPosition();
        Vector3 currentPosition = transform.position;

        if (Vector3.Distance(currentPosition, targetPosition) > thresholdDistance && smoothFollow)
        {
            m_SmoothFollowCoroutine = StartCoroutine(SmoothFollowCoroutine());
        }
        else if (!smoothFollow)
        {
            ApplyPosition(targetPosition);
        }
    }

    Vector3 GetTargetPosition()
    {
        return ((target.position - referencePosition) * multiplier) + offset;
    }

    void ApplyPosition(Vector3 position)
    {
        transform.position = new Vector3(
            followX ? position.x : transform.position.x,
            followY ? position.y : transform.position.y,
            followZ ? position.z : transform.position.z
        );
    }

    private IEnumerator SmoothFollowCoroutine()
    {
        Vector3 targetPosition = GetTargetPosition();

        while (Vector3.Distance(transform.position, targetPosition) > 0.01f)
        {
            Vector3 newPosition = Vector3.Lerp(
                transform.position,
                targetPosition,
                smoothSpeed
            );

            ApplyPosition(newPosition);

            yield return new WaitForFixedUpdate();

            targetPosition = GetTargetPosition();
        }

        m_SmoothFollowCoroutine = null;
    }
}