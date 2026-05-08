using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using QFramework;

public class GameManager : MonoSingleton<GameManager>
{
    private void Awake()
    {
        DontDestroyOnLoad(gameObject);
    }
}
