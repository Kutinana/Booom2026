using UnityEngine;

public partial class PlayerController
{
    [Header("Animation")]
    [SerializeField] private Animator animator;

    public void FakeJump()
    {
        jumpQueued = true;
    }

    public void SwitchAnimationTo(string animationName)
    {
        animator.Play(animationName);
    }
}
