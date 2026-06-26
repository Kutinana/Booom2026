using UnityEngine;

public class CircuitSwitch : MonoBehaviour
{
    [Header("Wire States")]
    [SerializeField] private GameObject offWire;
    [SerializeField] private GameObject onWire;

    [Header("Default State")]
    [SerializeField] private bool startPowered = false;

    private bool isPowered;

    private void Awake()
    {
        SetPowered(startPowered);
    }

    public void SetPowered(bool powered)
    {
        isPowered = powered;

        if (offWire != null)
            offWire.SetActive(!powered);

        if (onWire != null)
            onWire.SetActive(powered);
    }

    public void Toggle()
    {
        SetPowered(!isPowered);
    }

    public bool IsPowered()
    {
        return isPowered;
    }
}