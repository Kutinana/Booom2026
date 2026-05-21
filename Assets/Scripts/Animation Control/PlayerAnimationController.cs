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

    [Header("Landing")]
    [SerializeField, Tooltip("触发 Land 时竖直速度须不大于此值（避免仍在上穿平台时误触地动画）；与 PlayerController 的 grounded 竖直判定一致思路。")]
    private float landTriggerMaxVerticalVelocity = 0.001f;

    [Header("Sleep")]
    [SerializeField] private float sleepDelay = 60f;

    private float noInputTimer;
    private bool isSleeping;
    private bool isDowned;

    private float idleTimer;
    private float nextIdleTime;
    private bool wasGrounded;
    private bool wasFalling;
    private bool wasDying;
    private bool downHeld;

    private void Reset()
    {
        controller = GetComponent<PlayerController>();
        spriteRenderer = GetComponentInChildren<SpriteRenderer>();
        animator = GetComponentInChildren<Animator>();
    }

    private void Update()
    {
        if (controller == null) return;

        bool hasAnyInput =
        Mathf.Abs(controller.MoveInput.x) > 0.01f ||
        Mathf.Abs(controller.MoveInput.y) > 0.01f ||
        Input.anyKey;

        if (hasAnyInput)//没有任何输入超过sleepDelay时进入sleep动画
        {
            noInputTimer = 0f;

            if (isSleeping)
            {
                isSleeping = false;
                animator.SetBool("Sleeping", false);
            }
        }
        else
        {
            noInputTimer += Time.deltaTime;

            if (!isSleeping && noInputTimer >= sleepDelay)
            {
                isSleeping = true;
                animator.SetBool("Sleeping", true);
            }
        }

       

        bool isDying = controller.IsDying;
        if (isDying)
        {
            if (!wasDying)
            {
                animator.Play("Crashed", 0, 0f);
            }
            wasDying = true;
            return;
        }
        wasDying = false;

        var contacts = controller.Contacts;
        var velocity = controller.Velocity;
        bool isPushing = controller.IsPushing;
        UpdateDownState(contacts, velocity, isPushing);
        UpdateMovementAnimation(contacts, velocity, isPushing);
        UpdateIdleVariation(contacts, velocity, isPushing);
        UpdateFlip(velocity, isPushing);
    }

    private void UpdateDownState(PlayerController.ContactState contacts, Vector2 velocity, bool isPushing)
    {
        bool grounded = contacts.grounded;

        bool canDown =
            grounded &&
            !isPushing &&
            Mathf.Abs(controller.MoveInput.x) < 0.01f &&
            Mathf.Abs(velocity.y) < 0.01f;

        bool holdingDown =
            controller.MoveInput.y < -0.5f;

        if (holdingDown && canDown)
        {
            if (!downHeld)
            {
                animator.SetTrigger("Down");
            }

            downHeld = true;
        }
        else
        {
            if (downHeld)
            {
                animator.SetTrigger("Up");
            }

            downHeld = false;
        }

        animator.SetBool("DownHeld", downHeld);
    }

    private void RestDownUp()
    {
        animator.ResetTrigger("Down");
        animator.ResetTrigger("Up");

        animator.SetBool("DownHeld", false);
        animator.SetBool("Sleeping", false);
    }

    private void UpdateMovementAnimation(PlayerController.ContactState contacts, Vector2 velocity, bool isPushing)
    {
        bool grounded = contacts.grounded;

        animator.SetBool("Grounded", grounded);
        
        float walkIntention = Mathf.Abs(controller.MoveInput.x);
        animator.SetFloat("speed", isPushing ? 0f : walkIntention);
        animator.SetFloat("VerticalVelocity", velocity.y);

        // Landing detection：仍在明显上升时不打 Land（与 PlayerController 按竖直速度收紧 grounded 配套）
        if (!wasGrounded && grounded && velocity.y <= landTriggerMaxVerticalVelocity)
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
    }

    private void UpdateIdleVariation(PlayerController.ContactState contacts, Vector2 velocity, bool isPushing)
    {
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