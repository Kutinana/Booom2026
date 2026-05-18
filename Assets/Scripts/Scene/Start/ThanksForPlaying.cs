using System.Collections;
using System.Collections.Generic;
using Kuchinashi.Utils.Progressable;
using UnityEngine;
using UnityEngine.UI;

public class ThanksForPlaying : MonoBehaviour
{
    public Progressable progressable;
    public Button button;

    void Awake()
    {
        Save save = new Save().DeSerialize<Save>();
        if (save.HasThanksPlayed)
        {
            gameObject.SetActive(false);
            return;
        }

        // 第一次显示，标记为已播放
        save.HasThanksPlayed = true;
        save.Serialize();
    }

    // Start is called before the first frame update
    void Start()
    {
        if (button != null)
        {
            button.onClick.AddListener(() =>
            {
                progressable.InverseLinearTransition(0.2f);
            });
        }
    }

    // Update is called once per frame
    void Update()
    {
        
    }
}
