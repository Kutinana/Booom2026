using System;
using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// 点击角色：若同物体上 <see cref="PlayerPortalScreenTap"/> 已配置则由其统一处理（内外层），
/// 否则依赖 EventSystem + 相机上的 Physics2DRaycaster。
/// </summary>
public partial class PlayerController
{
    [Header("Animation")]
    [SerializeField] private Animator animator;

    /// <summary>任意路径触发点按动画时都会调用（含 Portal 映射点击）。</summary>
    public event Action OnTapped;

    public void TriggerTapAnimation()
    {
        if (animator != null)
            animator.SetTrigger("Tapped");

        OnTapped?.Invoke();
    }

    void IPointerClickHandler.OnPointerClick(PointerEventData eventData)
    {
        if (GetComponent<PlayerPortalScreenTap>() is { isActiveAndEnabled: true } tap &&
            tap.HandlesScreenTapForConfiguredPortal)
        {
            return;
        }

        TriggerTapAnimation();
    }

    public void FakeJump()
    {
        jumpQueued = true;
    }

    public void SwitchAnimationTo(string animationName)
    {
        animator.Play(animationName);
    }

    public void PlaySound(string soundName)
    {
        AudioMng.Instance.PlaySfx(soundName, 1f);
    }
    public void PlaySoundL(string soundName)
    {
        AudioMng.Instance.PlaySfx(soundName, 0.5f);
    }

    public void PlayFootstepSound()
    {
        AudioMng.Instance.PlayFootstep();
    }
}
