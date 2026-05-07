using QFramework;

public class PushableBoxService : ServiceBase<StandardBox>
{
    private IUnRegister pushRequestUnRegister;

    protected override void Awake()
    {
        base.Awake();
        pushRequestUnRegister = RegisterEvent<BoxPushRequestEvent>(OnPushRequested);
    }

    protected override void OnDestroy()
    {
        pushRequestUnRegister?.UnRegister();
        pushRequestUnRegister = null;
        base.OnDestroy();
    }

    public BoxPushInitializeEvent InitializePush(StandardBox box, BoxPushDirection direction, UnityEngine.GameObject pusher = null)
    {
        bool canPush = CanPush(box, direction);
        BoxPushInitializeEvent initialize = new BoxPushInitializeEvent(box, direction, pusher, canPush);
        SendEvent(initialize);
        return initialize;
    }

    public BoxPushAttemptEvent TryPush(StandardBox box, BoxPushDirection direction, UnityEngine.GameObject pusher = null)
    {
        return TryPush(box, direction, pusher, CanPush(box, direction));
    }

    public BoxPushAttemptEvent TryPush(StandardBox box, BoxPushDirection direction, UnityEngine.GameObject pusher, bool canPush)
    {
        canPush = canPush && box != null && IsRegistered(box);
        BoxPushAttemptEvent attempt = new BoxPushAttemptEvent(box, direction, pusher, canPush);
        SendEvent(attempt);
        return attempt;
    }

    private void OnPushRequested(BoxPushRequestEvent e)
    {
        TryPush(e.Box, e.Direction, e.Pusher);
    }

    private bool CanPush(StandardBox box, BoxPushDirection direction)
    {
        return box != null && IsRegistered(box) && box.CanPushToward(direction);
    }
}
