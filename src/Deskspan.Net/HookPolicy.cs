namespace Deskspan.Net;

public enum HookAction
{
    Pass = 0,
    Swallow = 1,
    SwallowAndToggle = 2
}

public static class HookPolicy
{
    public static HookAction Decide(bool capturing, bool injectedByUs, bool isHotkey)
    {
        if (injectedByUs)
            return HookAction.Pass;
        if (isHotkey)
            return HookAction.SwallowAndToggle;
        return capturing ? HookAction.Swallow : HookAction.Pass;
    }
}
