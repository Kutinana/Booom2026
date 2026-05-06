using QFramework;
using UnityEngine;
using UnityEngine.Events;

[DisallowMultipleComponent]
public class PressurePlate : MonoBehaviour, IPlayerRelativePositionTarget
{
    public bool IsPressed { get; private set; }
    public UnityEvent OnPressed;
    public UnityEvent OnReleased;

    public Bounds Bounds
    {
        get
        {
            if (_collider2D != null)
            {
                return _collider2D.bounds;
            }

            return _collider3D != null ? _collider3D.bounds : new Bounds(transform.position, Vector3.zero);
        }
    }

    private Collider _collider3D;
    private Collider2D _collider2D;
    private IUnRegister playerPositionUnRegister;

    private void Awake()
    {
        _collider3D = GetComponent<Collider>();
        _collider2D = GetComponent<Collider2D>();
    }

    private void OnEnable()
    {
        ServiceBase.Get<PlayerRelativePositionService>()?.Register(this);
        playerPositionUnRegister?.UnRegister();
        playerPositionUnRegister = TypeEventSystem.Global.Register<PlayerRelativePositionEvent>(OnPlayerPositionChanged);
    }

    private void OnDisable()
    {
        playerPositionUnRegister?.UnRegister();
        playerPositionUnRegister = null;
        ServiceBase.Get<PlayerRelativePositionService>()?.UnRegister(this);
    }

    private void Start()
    {
        RefreshPressedState();
    }

    private void OnPlayerPositionChanged(PlayerRelativePositionEvent e)
    {
        if (e.Target != this)
        {
            return;
        }

        SetPressed(e.PlayerAbove);
    }

    private void RefreshPressedState()
    {
        PlayerRelativePositionService service = ServiceBase.Get<PlayerRelativePositionService>();
        SetPressed(service != null && service.IsPlayerAbove(this));
    }

    private void SetPressed(bool pressed)
    {
        if (IsPressed == pressed)
        {
            return;
        }

        bool wasPressed = IsPressed;
        IsPressed = pressed;
        TypeEventSystem.Global.Send(new PressurePlateStateEvent(this, IsPressed, wasPressed));
        if (IsPressed)
        {
            OnPressed?.Invoke();
        }
        else
        {
            OnReleased?.Invoke();
        }
        Debug.Log($"Pressure Plate '{name}' is now {(IsPressed ? "Pressed" : "Released")}");
    }
}
