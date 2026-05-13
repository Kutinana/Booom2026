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

    private void Reset()
    {
        controller = GetComponent<PlayerController>();
        spriteRenderer = GetComponentInChildren<SpriteRenderer>();
        animator = GetComponentInChildren<Animator>();
    }

    private void Update()
    {
        if (controller == null) return;

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
        animator.SetFloat("speed", isPushing ? 0f : Mathf.Abs(velocity.x));
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
        bool isIdle = !isPushing && contacts.grounded && Mathf.Abs(velocity.x) < 0.01f;

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