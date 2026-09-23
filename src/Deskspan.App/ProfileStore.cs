using System.Text.Json;
using System.IO;
using Deskspan.Input;
using Deskspan.Net;

namespace Deskspan;

internal sealed class UserProfile
{
    public required DeviceIdentity Identity { get; init; }
    public Hotkey Hotkey { get; init; } = Hotkey.Default;
    public ControlShare ControlShare { get; init; } = ControlShare.Both;
    public StoredPeer? Peer { get; init; }
}

internal static class ProfileStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Deskspan");

    public static UserProfile Load()
    {
        try
        {
            var path = Path.Combine(Folder, "profile.json");
            if (File.Exists(path))
            {
                var dto = JsonSerializer.Deserialize<ProfileDto>(File.ReadAllText(path), Json);
                if (dto != null && dto.DeviceId != Guid.Empty && dto.PublicKey.Length > 0 && dto.PrivateKey.Length > 0)
                {
                    var identity = new DeviceIdentity(dto.DeviceId, dto.Name, Convert.FromBase64String(dto.PublicKey), Convert.FromBase64String(dto.PrivateKey));
                    var hotkey = new Hotkey((HotkeyModifiers)dto.HotkeyModifiers, (ushort)dto.HotkeyVirtualKey);
                    if (hotkey.Modifiers == HotkeyModifiers.None || hotkey.VirtualKey == 0 || hotkey == new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x4D))
                        hotkey = Hotkey.Default;
                    var share = dto.ControlShare switch
                    {
                        (int)ControlShare.Mouse => ControlShare.Mouse,
                        (int)ControlShare.Keyboard => ControlShare.Keyboard,
                        _ => ControlShare.Both
                    };
                    return new UserProfile
                    {
                        Identity = identity,
                        Hotkey = hotkey,
                        ControlShare = share,
                        Peer = ToPeer(dto.Peer)
                    };
                }
            }
        }
        catch (Exception)
        {
        }

        var created = DeviceIdentity.Create(Environment.MachineName);
        var initial = new UserProfile { Identity = created, Hotkey = Hotkey.Default };
        Save(initial);
        return initial;
    }

    public static void Save(UserProfile profile)
    {
        Directory.CreateDirectory(Folder);
        var identity = profile.Identity;
        var peer = profile.Peer;
        var dto = new ProfileDto
        {
            DeviceId = identity.Id,
            Name = identity.Name,
            PublicKey = Convert.ToBase64String(identity.PublicKey),
            PrivateKey = Convert.ToBase64String(identity.PrivateKey),
            HotkeyModifiers = (int)profile.Hotkey.Modifiers,
            HotkeyVirtualKey = profile.Hotkey.VirtualKey,
            ControlShare = (int)profile.ControlShare,
            Peer = peer == null ? null : new PeerDto
            {
                Id = peer.Id,
                Name = peer.Name,
                PublicKey = Convert.ToBase64String(peer.PublicKey),
                Address = peer.Address,
                Port = peer.Port,
                Dial = peer.Dial,
                Relay = peer.Relay,
                RelayCode = peer.RelayCode,
                RelayHost = peer.RelayHost,
                RelayPort = peer.RelayPort
            }
        };
        var path = Path.Combine(Folder, "profile.json");
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(dto, Json));
        File.Move(temp, path, overwrite: true);
    }

    private static StoredPeer? ToPeer(PeerDto? peer)
    {
        if (peer == null || peer.Id == Guid.Empty || string.IsNullOrWhiteSpace(peer.PublicKey))
            return null;
        return new StoredPeer
        {
            Id = peer.Id,
            Name = peer.Name,
            PublicKey = Convert.FromBase64String(peer.PublicKey),
            Address = peer.Address,
            Port = peer.Port > 0 ? peer.Port : Protocol.SessionPort,
            Dial = peer.Dial,
            Relay = peer.Relay,
            RelayCode = peer.RelayCode,
            RelayHost = peer.RelayHost,
            RelayPort = peer.RelayPort
        };
    }

    private sealed class ProfileDto
    {
        public Guid DeviceId { get; set; }
        public string Name { get; set; } = "";
        public string PublicKey { get; set; } = "";
        public string PrivateKey { get; set; } = "";
        public int HotkeyModifiers { get; set; }
        public int HotkeyVirtualKey { get; set; }
        public int ControlShare { get; set; }
        public PeerDto? Peer { get; set; }
    }

    private sealed class PeerDto
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public string PublicKey { get; set; } = "";
        public string? Address { get; set; }
        public int Port { get; set; }
        public bool? Dial { get; set; }
        public bool Relay { get; set; }
        public string? RelayCode { get; set; }
        public string? RelayHost { get; set; }
        public int RelayPort { get; set; }
    }
}

internal static class ErrorLog
{
    public static void Note(string text)
    {
        try
        {
            Directory.CreateDirectory(ProfileStore.Folder);
            File.AppendAllText(Path.Combine(ProfileStore.Folder, "network.log"), DateTimeOffset.Now.ToString("s") + " " + text + Environment.NewLine);
        }
        catch (Exception)
        {
        }
    }

    public static void Write(Exception exception)
    {
        try
        {
            Directory.CreateDirectory(ProfileStore.Folder);
            var line = DateTimeOffset.Now.ToString("s") + " " + exception.GetType().Name + ": " + exception.Message + Environment.NewLine;
            File.AppendAllText(Path.Combine(ProfileStore.Folder, "error.log"), line);
        }
        catch (Exception)
        {
        }
    }
}
