namespace Deskspan.Net;

public static class Sequence
{
    public static bool IsNewer(uint incoming, uint latest) => unchecked((int)(incoming - latest)) > 0;
}
