namespace Deskspan.Net;

public enum ShareMode
{
    Idle = 0,
    Controlling = 1,
    Controlled = 2
}

public static class ShareMachine
{
    public static ShareMode OnHotkey(ShareMode mode, bool linked) => mode switch
    {
        ShareMode.Controlling => ShareMode.Idle,
        ShareMode.Controlled => ShareMode.Idle,
        _ => linked ? ShareMode.Controlling : ShareMode.Idle
    };

    public static ShareMode OnPeerControl(ShareMode mode, bool active)
    {
        if (active)
            return ShareMode.Controlled;
        return mode == ShareMode.Controlled ? ShareMode.Idle : mode;
    }

    public static ShareMode OnReleaseRequest(ShareMode mode) =>
        mode == ShareMode.Controlling ? ShareMode.Idle : mode;

    public static ShareMode OnLinkLost(ShareMode mode) => ShareMode.Idle;
}
