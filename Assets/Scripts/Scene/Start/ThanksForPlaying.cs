using System.Collections;
using System.Collections.Generic;
using Kuchinashi.Utils.Progressable;
using UnityEngine;
using UnityEngine.UI;

public class ThanksForPlaying : MonoBehaviour
{
    public Progressable progressable;
    public Button button;

    // Start is called before the first frame update
    void Start()
    {
        button.onClick.AddListener(() =>
        {
            progressable.InverseLinearTransition(0.2f);
        });
    }

    // Update is called once per frame
    void Update()
    {
        
    }
}
