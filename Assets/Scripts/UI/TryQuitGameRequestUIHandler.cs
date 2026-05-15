using System.Collections;
using Kuchinashi.Utils.Progressable;
using QFramework;
using UnityEngine;
using UnityEngine.UI;

public readonly struct TryQuitGameRequestedEvent { }

/// <summary>
/// 首次 <see cref="TryQuitGameRequestedEvent"/>：展开面板并禁用玩家移动/跳跃等输入；
/// 再次收到该事件（同场景下再按 ESC）：收起面板并恢复输入。<see cref="m_CancelButton"/> 与第二次 ESC 行为一致。
/// </summary>
public sealed class TryQuitGameRequestUIHandler : MonoBehaviour
{
    [SerializeField] private CanvasGroupAlphaProgressable m_CanvasGroupAlphaProgressable;
    [SerializeField] private Button m_QuitButton;
    [SerializeField] private Button m_CancelButton;
    [SerializeField] private PlayerController m_Player;
    [SerializeField] private float m_TransitionSeconds = 0.2f;

    bool m_PanelBlockingInput;
    bool m_Closing;
    Coroutine m_CloseRoutine;

    void OnEnable()
    {
        TypeEventSystem.Global.Register<TryQuitGameRequestedEvent>(OnTryQuitGameRequested)
            .UnRegisterWhenGameObjectDestroyed(gameObject);

        if (m_QuitButton != null)
        {
            m_QuitButton.onClick.AddListener(OnQuitClicked);
        }

        if (m_CancelButton != null)
        {
            m_CancelButton.onClick.AddListener(OnCancelClicked);
        }
    }

    void OnDisable()
    {
        if (m_QuitButton != null)
        {
            m_QuitButton.onClick.RemoveListener(OnQuitClicked);
        }

        if (m_CancelButton != null)
        {
            m_CancelButton.onClick.RemoveListener(OnCancelClicked);
        }

        StopCloseRoutine();
        if (m_PanelBlockingInput || m_Closing)
        {
            SetPlayerMovementEnabled(true);
            m_PanelBlockingInput = false;
            m_Closing = false;
        }
    }

    void OnTryQuitGameRequested(TryQuitGameRequestedEvent e)
    {
        if (m_CanvasGroupAlphaProgressable == null)
        {
            return;
        }

        if (m_Closing)
        {
            return;
        }

        if (m_PanelBlockingInput)
        {
            StartClosePanel();
            return;
        }

        OpenPanel();
    }

    void OpenPanel()
    {
        StopCloseRoutine();
        m_PanelBlockingInput = true;
        SetPlayerMovementEnabled(false);
        m_CanvasGroupAlphaProgressable.LinearTransition(m_TransitionSeconds);
    }

    void OnCancelClicked()
    {
        if (!m_PanelBlockingInput || m_Closing || m_CanvasGroupAlphaProgressable == null)
        {
            return;
        }

        StartClosePanel();
    }

    void StartClosePanel()
    {
        m_Closing = true;
        m_CanvasGroupAlphaProgressable.InverseLinearTransition(m_TransitionSeconds);
        StopCloseRoutine();
        m_CloseRoutine = StartCoroutine(CloseAfterTransitionRoutine());
    }

    IEnumerator CloseAfterTransitionRoutine()
    {
        yield return new WaitForSeconds(m_TransitionSeconds);
        m_Closing = false;
        m_PanelBlockingInput = false;
        SetPlayerMovementEnabled(true);
        m_CloseRoutine = null;
    }

    void StopCloseRoutine()
    {
        if (m_CloseRoutine != null)
        {
            StopCoroutine(m_CloseRoutine);
            m_CloseRoutine = null;
        }
    }

    void OnQuitClicked()
    {
        Application.Quit();
    }

    void SetPlayerMovementEnabled(bool enabled)
    {
        var player = m_Player != null ? m_Player : FindFirstObjectByType<PlayerController>();
        if (player != null)
        {
            player.MovementInputDisabled = !enabled;
        }
    }
}
