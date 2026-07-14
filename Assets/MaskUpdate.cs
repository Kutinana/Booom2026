using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class MaskUpdate : MonoBehaviour
{
    [SerializeField]SpriteMask mask;
    [SerializeField]SpriteRenderer spriteRenderer;
    private void Start()
    {
        mask=GetComponent<SpriteMask>();
        spriteRenderer=GetComponent<SpriteRenderer>();

    }
    private void Update()
    {
        mask.sprite=spriteRenderer.sprite;
    }
}
