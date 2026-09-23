namespace Deskspan.Net;

public static class Protocol
{
    public const byte Version = 1;
    public static ReadOnlySpan<byte> DiscoveryMagic => "DSD1"u8;
    public static ReadOnlySpan<byte> PairMagic => "DSP2"u8;
    public static ReadOnlySpan<byte> QuickMagic => "DSQ2"u8;
    public static ReadOnlySpan<byte> SessionMagic => "DSSE"u8;
    public const int DiscoveryPort = 47841;
    public const int SessionPort = 47842;
    public const int MousePort = 47843;
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(5);
    public const int MaxNameBytes = 64;
    public const int MaxPairingFrame = 4096;
    public const int MaxFrame = 65535;
    public const int MaxClipboardBytes = 60000;
}
