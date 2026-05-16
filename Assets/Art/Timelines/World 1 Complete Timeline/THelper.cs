using System.Collections;
using System.Collections.Generic;
using Kuchinashi.SceneFlow;
using UnityEngine;

public class THelper : MonoBehaviour
{
    public void PlayTransitionSfx()
    {
        AudioMng.Instance.FadeOutBGM(1f);
        AudioMng.Instance.PlaySfx("transition", 1f);
    }
    public void RecoverGame()
    {
        AudioMng.Instance.FadeInBGM(1f);
        SceneFlowController.Instance.RequestSwitchContent("World 2");
    }
}
