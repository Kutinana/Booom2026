using System.Collections;
using QFramework;
using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// 关卡中的可收集星星。请在星星上配置 <b>Trigger</b> 碰撞体（2D 或 3D），玩家进入触发范围即视为收集。
/// </summary>
[DisallowMultipleComponent]
public class CollectiveStar : MonoBehaviour
{
    [Header("Animation")]
    [SerializeField] private Animator animator;

    [Header("Lifecycle")]
    [SerializeField] private bool destroyAfterCollect = true;
    [SerializeField, Min(0f)] private float fallbackCollectDuration = 0.35f;

    [Header("Events")]
    public UnityEvent OnCollected;

    public bool IsCollected { get; private set; }

    private Collider pickupCollider3D;
    private Collider2D pickupCollider2D;
    private Coroutine collectRoutine;

    private void Awake()
    {
        pickupCollider3D = GetComponent<Collider>();
        pickupCollider2D = GetComponent<Collider2D>();
        if (animator == null)
        {
            animator = GetComponentInChildren<Animator>();
        }
    }

    private void OnTriggerEnter2D(Collider2D other)
    {
        TryCollect(other);
    }

    private void OnTriggerEnter(Collider other)
    {
        TryCollect(other);
    }

    private void TryCollect(Component other)
    {
        if (IsCollected || other == null)
        {
            return;
        }

        PlayerController player = other.GetComponentInParent<PlayerController>();
        if (player == null)
        {
            return;
        }

        if (collectRoutine != null)
        {
            return;
        }

        collectRoutine = StartCoroutine(CollectRoutine(player));
    }

    private IEnumerator CollectRoutine(PlayerController player)
    {
        IsCollected = true;

        SetPickupCollidersEnabled(false);

        TypeEventSystem.Global.Send(new CollectiveStarCollectedEvent(this, player));
        OnCollected?.Invoke();

        float waitSeconds = fallbackCollectDuration;
        if (animator != null)
        {
            animator.SetTrigger("Collected");
            yield return null;
            AnimatorStateInfo state = animator.GetCurrentAnimatorStateInfo(0);
            if (state.length > 0f)
            {
                waitSeconds = state.length;
            }
        }

        yield return new WaitForSeconds(waitSeconds);

        // Destroy 会立刻停掉 Animator；若动画正在写材质 alpha 等，停写的那一帧会回到 Renderer 上的默认值，容易闪一帧。
        SetSubtreeRenderersEnabled(transform, false);

        if (destroyAfterCollect)
        {
            Destroy(gameObject);
        }
        else
        {
            gameObject.SetActive(false);
        }

        collectRoutine = null;
    }

    private static void SetSubtreeRenderersEnabled(Transform root, bool enabled)
    {
        Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < renderers.Length; i++)
        {
            renderers[i].enabled = enabled;
        }
    }

    private void SetPickupCollidersEnabled(bool enabled)
    {
        if (pickupCollider3D != null)
        {
            pickupCollider3D.enabled = enabled;
        }

        if (pickupCollider2D != null)
        {
            pickupCollider2D.enabled = enabled;
        }
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        if (pickupCollider3D == null)
        {
            pickupCollider3D = GetComponent<Collider>();
        }

        if (pickupCollider2D == null)
        {
            pickupCollider2D = GetComponent<Collider2D>();
        }

        if (pickupCollider3D != null && !pickupCollider3D.isTrigger)
        {
            Debug.LogWarning($"CollectiveStar '{name}': 3D Collider 建议勾选 Is Trigger，否则 OnTriggerEnter 不会触发。", this);
        }

        if (pickupCollider2D != null && !pickupCollider2D.isTrigger)
        {
            Debug.LogWarning($"CollectiveStar '{name}': 2D Collider 建议勾选 Is Trigger。", this);
        }
    }
#endif
}

public readonly struct CollectiveStarCollectedEvent
{
    public readonly CollectiveStar Star;
    public readonly PlayerController Player;

    public CollectiveStarCollectedEvent(CollectiveStar star, PlayerController player)
    {
        Star = star;
        Player = player;
    }
}
