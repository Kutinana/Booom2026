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

    public BoxPushAttemptEvent TryPush(StandardBox box, BoxPushDirection direction, UnityEngine.GameObject pusher = null)
    {
        bool canPush = box != null && IsRegistered(box) && box.CanPushToward(direction);
        BoxPushAttemptEvent attempt = new BoxPushAttemptEvent(box, direction, pusher, canPush);
        SendEvent(attempt);
        return attempt;
    }

    private void OnPushRequested(BoxPushRequestEvent e)
    {
        TryPush(e.Box, e.Direction, e.Pusher);
    }
}
