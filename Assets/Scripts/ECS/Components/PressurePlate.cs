using QFramework;
using UnityEngine;
using UnityEngine.Events;

[DisallowMultipleComponent]
public class PressurePlate : MonoBehaviour, IPlayerRelativePositionTarget
{
    public bool IsPressed { get; private set; }

    public UnityEvent OnPressed;
    public UnityEvent OnReleased;

    [Header("Animation")]
    public Animator Animator;

    [Header("Animator Parameters")]
    public string PressedParameter = "Pressed";

    private void Start()
    {
        Animator=GetComponent<Animator>();
    }
    public Bounds Bounds
    {
        get
        {
            if (_collider2D != null)
            {
                return _collider2D.bounds;
            }

            return _collider3D != null
                ? _collider3D.bounds
                : new Bounds(transform.position, Vector3.zero);
        }
    }

    private Collider _collider3D;
    private Collider2D _collider2D;

    private void Awake()
    {
        _collider3D = GetComponent<Collider>();
        _collider2D = GetComponent<Collider2D>();

        ServiceBase.Get<PressurePlateService>()?.Register(this);

        
        if (Animator != null)
        {
            Animator.SetBool(PressedParameter, IsPressed);
        }
    }

    private void OnDestroy()
    {
        if (ServiceBase.TryGet(out PressurePlateService pressurePlateService))
        {
            pressurePlateService.UnRegister(this);
        }
    }

    public void SetPressed(bool pressed)
    {
        if (IsPressed == pressed)
        {
            return;
        }

        bool wasPressed = IsPressed;
        IsPressed = pressed;

        
        if (Animator != null)
        {
            Animator.SetBool(PressedParameter, IsPressed);
        }

        TypeEventSystem.Global.Send(
            new PressurePlateStateEvent(this, IsPressed, wasPressed)
        );

        if (IsPressed)
        {
            if(!wasPressed) AudioMng.Instance.PlaySfx("Pressure", 0.2f);
            OnPressed?.Invoke();
        }
        else
        {
            OnReleased?.Invoke();
        }

        Debug.Log(
            $"Pressure Plate '{name}' is now {(IsPressed ? "Pressed" : "Released")}"
        );
    }
}