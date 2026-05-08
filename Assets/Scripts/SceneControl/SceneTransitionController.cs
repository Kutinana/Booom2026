using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Kuchinashi.SceneFlow;
using Kuchinashi.Utils.Progressable;

public class SceneTransitionController : SceneTransitionViewBehaviour
{
    [SerializeField] private CanvasGroupAlphaProgressable canvasGroup;

    public override IEnumerator EnterCover()
    {
        yield return canvasGroup.LinearTransition(0.5f, delay: 0f);
        yield return new WaitForEndOfFrame();
    }

    public override IEnumerator ExitCover()
    {
        yield return canvasGroup.InverseLinearTransition(0.5f, delay: 0f);
    }
}
