namespace Deskspan.Net;

public sealed record Edition(string Name, bool CustomShortcut, int MaxComputers)
{
    public static Edition Free { get; } = new("Deskspan", false, 2);

    public static Edition Pro { get; } = new("Deskspan Pro", true, 2);
}
