using System.Collections;
using UnityEngine;

public class Barrier : MonoBehaviour
{
    public bool DefaultOpen = false;
    public GameObject BarrierClosed;
    public Animator Animator;
    public float AnimationDuration = 0.3f;
    [Header("Block Detection")]
    public LayerMask BlockingMask = ~0;
    [Min(0f)]
    public float BlockingPadding = 0.04f;
    [Min(0f)]
    public float ClearanceTimeThreshold = 0.1f;

    private static readonly int BlendToOn = UnityEngine.Animator.StringToHash("BlendToOn");
    private static readonly int BlendToOff = UnityEngine.Animator.StringToHash("BlendToOff");

    private readonly Collider2D[] overlapHits2D = new Collider2D[16];
    private readonly Collider[] overlapHits3D = new Collider[16];

    private bool currentOpen;
    private bool initialized;
    private float blendValue;
    private float clearanceTimer;
    private bool waitingForClearance;
    private Coroutine animationCoroutine;
    private Collider2D[] blockingVolumeColliders2D;
    private Collider[] blockingVolumeColliders3D;

    private void Awake()
    {
        if (Animator == null)
        {
            Animator = GetComponentInChildren<Animator>();
        }

        CacheBlockingVolumeColliders();
    }

    private void Start()
    {
        UpdateBarrierState(DefaultOpen);
    }

    public bool UpdateBarrierState(bool isOpen)
    {
        if (!CanUpdateBarrierState(isOpen))
        {
            return false;
        }

        ClearClearanceWaitState();

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
            return true;
        }

        bool wasOpen = currentOpen;
        currentOpen = isOpen;

        if (BarrierClosed != null)
        {
            BarrierClosed.SetActive(!isOpen);
        }

        UpdateBarrierAnimation(wasOpen, isOpen);
        return true;
    }

    private bool CanUpdateBarrierState(bool isOpen)
    {
        if (isOpen || !IsBarrierPositionBlocked())
        {
            if (!waitingForClearance || ClearanceTimeThreshold <= 0f)
            {
                return true;
            }

            clearanceTimer += Time.deltaTime;
            return clearanceTimer >= ClearanceTimeThreshold;
        }

        clearanceTimer = 0f;
        waitingForClearance = true;
        return false;
    }

    private void ClearClearanceWaitState()
    {
        clearanceTimer = 0f;
        waitingForClearance = false;
    }

    private void CacheBlockingVolumeColliders()
    {
        GameObject blockingRoot = BarrierClosed != null ? BarrierClosed : gameObject;
        blockingVolumeColliders2D = blockingRoot.GetComponentsInChildren<Collider2D>(true);
        blockingVolumeColliders3D = blockingRoot.GetComponentsInChildren<Collider>(true);
    }

    private bool IsBarrierPositionBlocked()
    {
        if ((blockingVolumeColliders2D == null || blockingVolumeColliders2D.Length == 0) &&
            (blockingVolumeColliders3D == null || blockingVolumeColliders3D.Length == 0))
        {
            CacheBlockingVolumeColliders();
        }

        return IsBarrierPositionBlocked2D() || IsBarrierPositionBlocked3D();
    }

    private bool IsBarrierPositionBlocked2D()
    {
        if (blockingVolumeColliders2D == null)
        {
            return false;
        }

        foreach (Collider2D volumeCollider in blockingVolumeColliders2D)
        {
            if (volumeCollider == null || !volumeCollider.enabled)
            {
                continue;
            }

            if (IsBoxBlocked(volumeCollider))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsBoxBlocked(Collider2D volumeCollider)
    {
        BoxCollider2D boxCollider = volumeCollider as BoxCollider2D;
        if (boxCollider != null)
        {
            Vector2 center = boxCollider.transform.TransformPoint(boxCollider.offset);
            Vector2 size = Vector2.Scale(boxCollider.size, AbsXY(boxCollider.transform.lossyScale));
            if (!TryApplyPadding(ref size))
            {
                return false;
            }

            return HasBlockingOverlap2D(center, size, boxCollider.transform.eulerAngles.z);
        }

        if (!volumeCollider.gameObject.activeInHierarchy)
        {
            return false;
        }

        Bounds bounds = volumeCollider.bounds;
        if (bounds.size == Vector3.zero)
        {
            return false;
        }

        Vector2 boundsSize = bounds.size;
        if (!TryApplyPadding(ref boundsSize))
        {
            return false;
        }

        return HasBlockingOverlap2D(bounds.center, boundsSize, 0f);
    }

    private bool HasBlockingOverlap2D(Vector2 center, Vector2 size, float angle)
    {
        int hitCount = Physics2D.OverlapBoxNonAlloc(center, size, angle, overlapHits2D, BlockingMask);
        for (int i = 0; i < hitCount; i++)
        {
            Collider2D hit = overlapHits2D[i];
            if (IsBlockingCollider(hit))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsBarrierPositionBlocked3D()
    {
        if (blockingVolumeColliders3D == null)
        {
            return false;
        }

        foreach (Collider volumeCollider in blockingVolumeColliders3D)
        {
            if (volumeCollider == null || !volumeCollider.enabled)
            {
                continue;
            }

            if (IsBoxBlocked(volumeCollider))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsBoxBlocked(Collider volumeCollider)
    {
        BoxCollider boxCollider = volumeCollider as BoxCollider;
        if (boxCollider != null)
        {
            Vector3 center = boxCollider.transform.TransformPoint(boxCollider.center);
            Vector3 halfExtents = Vector3.Scale(boxCollider.size * 0.5f, Abs(boxCollider.transform.lossyScale));
            if (!TryApplyPadding(ref halfExtents))
            {
                return false;
            }

            return HasBlockingOverlap3D(center, halfExtents, boxCollider.transform.rotation);
        }

        if (!volumeCollider.gameObject.activeInHierarchy)
        {
            return false;
        }

        Bounds bounds = volumeCollider.bounds;
        if (bounds.size == Vector3.zero)
        {
            return false;
        }

        Vector3 boundsExtents = bounds.extents;
        if (!TryApplyPadding(ref boundsExtents))
        {
            return false;
        }

        return HasBlockingOverlap3D(bounds.center, boundsExtents, Quaternion.identity);
    }

    private bool HasBlockingOverlap3D(Vector3 center, Vector3 halfExtents, Quaternion rotation)
    {
        int hitCount = Physics.OverlapBoxNonAlloc(center, halfExtents, overlapHits3D, rotation, BlockingMask, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < hitCount; i++)
        {
            Collider hit = overlapHits3D[i];
            if (IsBlockingCollider(hit))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsBlockingCollider(Collider2D hit)
    {
        return hit != null &&
            !hit.isTrigger &&
            !hit.CompareTag("Platform") &&
            !hit.transform.IsChildOf(transform) &&
            hit.bounds.size != Vector3.zero;
    }

    private bool IsBlockingCollider(Collider hit)
    {
        return hit != null &&
            !hit.isTrigger &&
            !hit.CompareTag("Platform") &&
            !hit.transform.IsChildOf(transform) &&
            hit.bounds.size != Vector3.zero;
    }

    private bool TryApplyPadding(ref Vector2 size)
    {
        float shrink = Mathf.Max(0f, BlockingPadding) * 2f;
        size.x -= shrink;
        size.y -= shrink;
        return size.x > 0f && size.y > 0f;
    }

    private bool TryApplyPadding(ref Vector3 halfExtents)
    {
        float padding = Mathf.Max(0f, BlockingPadding);
        halfExtents.x -= padding;
        halfExtents.y -= padding;
        halfExtents.z -= padding;
        return halfExtents.x > 0f && halfExtents.y > 0f && halfExtents.z > 0f;
    }

    private static Vector2 AbsXY(Vector3 value)
    {
        return new Vector2(Mathf.Abs(value.x), Mathf.Abs(value.y));
    }

    private static Vector3 Abs(Vector3 value)
    {
        return new Vector3(Mathf.Abs(value.x), Mathf.Abs(value.y), Mathf.Abs(value.z));
    }

    private void UpdateBarrierAnimation(bool wasOpen, bool isOpen)
    {
        if (wasOpen == isOpen)
        {
            return;
        }
        AudioMng.Instance.PlaySfxWithDecay("BarrierOff",
            1f,
            transform.position,
            0.25f);
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

    private void OnValidate()
    {
        if (BlockingPadding < 0f)
        {
            BlockingPadding = 0f;
        }

        if (ClearanceTimeThreshold < 0f)
        {
            ClearanceTimeThreshold = 0f;
        }
    }
}
