using UnityEngine;

[RequireComponent(typeof(Animator))]
public class PlayerAnimationController : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private PlayerController controller;
    [SerializeField] private SpriteRenderer spriteRenderer;
    [SerializeField] private Animator animator;

    [Header("Idle Variation")]
    [SerializeField] private float idleTriggerTime = 3f;
    [SerializeField] private Vector2 idleRandomInterval = new Vector2(3f, 6f);

    private float idleTimer;
    private float nextIdleTime;
    private bool wasGrounded;
    private bool wasFalling;
    private bool wasPushing;
    private bool wasDying;

    // controller 中名为 "Crashed" 的 state：被箱子砸死时 force-Play 进入；该 state 没有任何转出过渡，
    // 因此会在最后一帧定格直到场景 reload，刚好契合"砸扁后停留 0.5s 再重置"的需求。
    private static readonly int CrashedStateHash = Animator.StringToHash("Crashed");

    private void Reset()
    {
        controller = GetComponent<PlayerController>();
        spriteRenderer = GetComponentInChildren<SpriteRenderer>();
        animator = GetComponentInChildren<Animator>();
    }

    private void Update()
    {
        if (controller == null) return;

        // 死亡过渡：第一帧进入 IsDying 时强制 Play("Crashed")，之后不再写其它 animator 参数，
        // 避免 speed/Grounded/Pushing 触发到别的 state（虽然 Crashed 本身没有出口转移，但保持
        // 整段死亡帧只播放这一个 clip 更稳）。
        bool isDying = controller.IsDying;
        if (isDying)
        {
            if (!wasDying)
            {
                animator.Play(CrashedStateHash, 0, 0f);
            }
            wasDying = true;
            return;
        }
        wasDying = false;

        var contacts = controller.Contacts;
        var velocity = controller.Velocity;
        bool isPushing = controller.IsPushing;

        UpdateMovementAnimation(contacts, velocity, isPushing);
        UpdateIdleVariation(contacts, velocity, isPushing);
        UpdateFlip(velocity, isPushing);
    }

    private void UpdateMovementAnimation(PlayerController.ContactState contacts, Vector2 velocity, bool isPushing)
    {
        bool grounded = contacts.grounded;

        animator.SetBool("Grounded", grounded);
        // walking 动画依赖 speed > 0.01。直接读 |velocity.x| 会被 ResolveTerrainOverlaps 在某些帧
        // 短暂清零（X 方向 collider 修正），导致 speed 在 [0, 4] 间抖动 → walking 与 idle 反复切换。
        // 改用玩家的输入意图（MoveInput.x）：按方向键持续 ±1，不按为 0，物理修正不影响。
        // 撞墙时玩家原地按方向键，walking 动画"原地踏步"，符合常见 2D 平台游戏的视觉约定。
        float walkIntention = Mathf.Abs(controller.MoveInput.x);
        animator.SetFloat("speed", isPushing ? 0f : walkIntention);
        animator.SetFloat("VerticalVelocity", velocity.y);

        // Landing detection
        if (!wasGrounded && grounded)
        {
            animator.SetTrigger("Land");
        }

        // Falling start detection
        bool isFalling = !grounded && velocity.y < -0.1f;
        if (!wasFalling && isFalling)
        {
            //animator.SetTrigger("FallStart");
        }

        animator.SetBool("Pushing", isPushing);

        wasGrounded = grounded;
        wasFalling = isFalling;
        wasPushing = isPushing;
    }

    private void UpdateIdleVariation(PlayerController.ContactState contacts, Vector2 velocity, bool isPushing)
    {
        // 与 speed 参数同步：用 MoveInput 判断"是否在主动行走"，避免 velocity.x 抖动让 idle variant
        // 计时器被反复重置而触发不到。
        bool isIdle = !isPushing && contacts.grounded && Mathf.Abs(controller.MoveInput.x) < 0.01f;

        if (!isIdle)
        {
            idleTimer = 0f;
            animator.SetInteger("IdleVariant", 0);
            return;
        }

        idleTimer += Time.deltaTime;

        if (idleTimer > nextIdleTime)
        {
            int variant = Random.Range(1, 3); // 1 or 2
            animator.SetInteger("IdleVariant", variant);

            idleTimer = 0f;
            nextIdleTime = Random.Range(idleRandomInterval.x, idleRandomInterval.y);
        }
    }

    private void UpdateFlip(Vector2 velocity, bool isPushing)
    {
        // 推动时即使被夹断（velocity.x ≈ 0）也要按当前推动方向保持朝向。
        if (isPushing && controller != null)
        {
            spriteRenderer.flipX = controller.Contacts.leftBlocked && !controller.Contacts.rightBlocked;
            if (Mathf.Abs(velocity.x) > 0.01f)
            {
                spriteRenderer.flipX = velocity.x < 0f;
            }
            return;
        }

        if (Mathf.Abs(velocity.x) > 0.01f)
        {
            spriteRenderer.flipX = velocity.x < 0f;
        }
    }
}