using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace SmbManager.Local;

// These are client-reported inventory signals, not proof of physical possession.
public sealed record HardwareIdentity(string SystemUuid, string[] MacAddresses, string[] IpAddresses, string MacSource = "ObservedFallback")
{
    public static HardwareIdentity Capture()
    {
        var adapters = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)
            .OrderBy(n => n.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? 0 : 1).ThenBy(n => n.Id).ToArray();
        var permanent = adapters.Select(ReadPermanentMac).OfType<string>().Distinct().ToArray();
        var macs = (permanent.Length > 0 ? permanent : adapters.Select(n => n.GetPhysicalAddress().ToString().ToUpperInvariant()))
            .Where(m => m.Length == 12 && m != "000000000000" && m != "FFFFFFFFFFFF").Distinct().Take(16).ToArray();
        var ips = new List<string>();
        foreach (var n in adapters) try { ips.AddRange(n.GetIPProperties().UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork).Select(a => a.Address.ToString())); } catch (NetworkInformationException) { }
        return new(ReadUuid(), macs, ips.Distinct().Take(16).ToArray(), permanent.Length > 0 ? "Permanent" : "ObservedFallback");
    }
    public static bool Valid(HardwareIdentity? h) => h is not null && h.SystemUuid is not null &&
        (h.SystemUuid.Length == 0 || Guid.TryParse(h.SystemUuid, out var g) && g != Guid.Empty && h.SystemUuid != "ffffffff-ffff-ffff-ffff-ffffffffffff") &&
        h.MacAddresses is { Length: > 0 and <= 16 } && h.MacAddresses.All(m => m is { Length: 12 } && m.All(Uri.IsHexDigit) && m != "000000000000" && !m.Equals("FFFFFFFFFFFF", StringComparison.OrdinalIgnoreCase)) &&
        h.IpAddresses is { Length: <= 16 } && h.IpAddresses.All(ip => System.Net.IPAddress.TryParse(ip, out _));
    public static bool Same(HardwareIdentity? a, HardwareIdentity? b) => Valid(a) && Valid(b) &&
        string.Equals(a!.SystemUuid, b!.SystemUuid, StringComparison.OrdinalIgnoreCase) &&
        a.MacAddresses.Intersect(b.MacAddresses, StringComparer.OrdinalIgnoreCase).Any();
    public string ShortId => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(SystemUuid + "|" + string.Join("|", MacAddresses.Order(StringComparer.OrdinalIgnoreCase)))))[..12];
    private static string? ReadPermanentMac(NetworkInterface adapter)
    {
        if (!OperatingSystem.IsWindows()) return null;
        IntPtr buffer = IntPtr.Zero;
        try {
            var index = adapter.GetIPProperties().GetIPv4Properties()?.Index;
            if (index is null) return null;
            // Allocate room for the complete native MIB_IF_ROW2, not just the prefix read below.
            buffer = Marshal.AllocHGlobal(4096);
            Marshal.Copy(new byte[4096], 0, buffer, 4096);
            Marshal.WriteInt32(buffer, Marshal.OffsetOf<InterfacePrefix>(nameof(InterfacePrefix.Index)).ToInt32(), index.Value);
            if (GetIfEntry2(buffer) != 0) return null;
            if ((Marshal.ReadByte(buffer, Marshal.OffsetOf<InterfacePrefix>(nameof(InterfacePrefix.Flags)).ToInt32()) & 1) == 0) return null;
            if (Marshal.ReadInt32(buffer, Marshal.OffsetOf<InterfacePrefix>(nameof(InterfacePrefix.Length)).ToInt32()) != 6) return null;
            var bytes = new byte[6];
            Marshal.Copy(IntPtr.Add(buffer, Marshal.OffsetOf<InterfacePrefix>(nameof(InterfacePrefix.Permanent)).ToInt32()), bytes, 0, bytes.Length);
            if (bytes.All(v => v == 0) || bytes.All(v => v == 255)) return null;
            return Convert.ToHexString(bytes);
        } catch (Exception e) when (e is NetworkInformationException or DllNotFoundException or EntryPointNotFoundException) { return null; }
        finally { if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer); }
    }
#pragma warning disable CS0649 // Native layout only, fields are written by Windows.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct InterfacePrefix
    {
        public ulong Luid;
        public uint Index;
        public Guid Guid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)] public string Alias;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)] public string Description;
        public uint Length;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Physical;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Permanent;
        public uint Mtu, Type, TunnelType, MediaType, PhysicalMediumType, AccessType, DirectionType;
        public byte Flags;
    }
#pragma warning restore CS0649
    [DllImport("iphlpapi.dll")] private static extern uint GetIfEntry2(IntPtr row);
    private static string ReadUuid()
    {
        if (!OperatingSystem.IsWindows()) return "";
        try {
            const uint provider = 0x52534D42; // RSMB
            var size = GetSystemFirmwareTable(provider, 0, null, 0);
            if (size < 8 || size > 1024 * 1024) return "";
            var bytes = new byte[size];
            if (GetSystemFirmwareTable(provider, 0, bytes, size) != size) return "";
            var end = Math.Min(bytes.Length, 8L + BitConverter.ToUInt32(bytes, 4));
            for (var offset = 8; offset + 4 <= end;) {
                int length = bytes[offset + 1];
                if (length < 4 || offset + length > end) break;
                if (bytes[offset] == 1 && length >= 24) {
                    var uuid = bytes.AsSpan(offset + 8, 16);
                    if (uuid.ToArray().All(v => v == 0) || uuid.ToArray().All(v => v == 255)) return "";
                    return new Guid(uuid).ToString();
                }
                offset += length;
                while (offset + 1 < end && (bytes[offset] != 0 || bytes[offset + 1] != 0)) offset++;
                offset += 2;
            }
        } catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or ArgumentException) { }
        return "";
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetSystemFirmwareTable(uint provider, uint id, [Out] byte[]? buffer, uint size);
}
