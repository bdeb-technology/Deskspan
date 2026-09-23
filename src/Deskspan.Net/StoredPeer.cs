namespace Deskspan.Net;

public sealed class StoredPeer
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public byte[] PublicKey { get; set; } = [];
    public string? Address { get; set; }
    public int Port { get; set; } = Protocol.SessionPort;
    public bool? Dial { get; set; }
    public bool Relay { get; set; }
    public string? RelayCode { get; set; }
    public string? RelayHost { get; set; }
    public int RelayPort { get; set; }
}
