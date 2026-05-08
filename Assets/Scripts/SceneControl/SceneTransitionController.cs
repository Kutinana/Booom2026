using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Kuchinashi.SceneFlow;
using Kuchinashi.Utils.Progressable;

public class SceneTransitionController : SceneTransitionViewBehaviour
{
    [SerializeField] private Progressable progressable;

    public override IEnumerator EnterCover()
    {
        yield return progressable.LinearTransition(0.5f, delay: 0f);
        yield return new WaitForEndOfFrame();
    }

    public override IEnumerator ExitCover()
    {
        yield return progressable.InverseLinearTransition(0.5f, delay: 0f);
    }
}
