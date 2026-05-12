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

        UpdateMovementAnimation(contacts, velocity);
        UpdateIdleVariation(contacts, velocity);
        UpdateFlip(velocity);
    }

    private void UpdateMovementAnimation(PlayerController.ContactState contacts, Vector2 velocity)
    {
        bool grounded = contacts.grounded;

        animator.SetBool("Grounded", grounded);
        animator.SetFloat("speed", Mathf.Abs(velocity.x));
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

        wasGrounded = grounded;
        wasFalling = isFalling;
    }

    private void UpdateIdleVariation(PlayerController.ContactState contacts, Vector2 velocity)
    {
        bool isIdle = contacts.grounded && Mathf.Abs(velocity.x) < 0.01f;

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

    private void UpdateFlip(Vector2 velocity)
    {
        if (Mathf.Abs(velocity.x) > 0.01f)
        {
            spriteRenderer.flipX = velocity.x < 0f;
        }
    }
}