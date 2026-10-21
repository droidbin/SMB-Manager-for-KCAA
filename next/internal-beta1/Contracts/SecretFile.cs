using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;

namespace SmbManager.Local;

// Windows DPAPI, current-user scope. No machine-wide key or fallback plaintext.
public static class SecretFile
{
    private static readonly byte[] Magic = "SMB-DPAPI-1\n"u8.ToArray();
    [StructLayout(LayoutKind.Sequential)]
    private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr pointer);

    public static void PrivateDirectory(string directory)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows DPAPI가 필요합니다.");
        var info = Directory.CreateDirectory(directory);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("비밀값 폴더는 링크일 수 없습니다.");
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var sid in new[] { WindowsIdentity.GetCurrent().User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        if (info.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access) != acl.GetSecurityDescriptorSddlForm(AccessControlSections.Access))
            info.SetAccessControl(acl);
    }
    private static byte[] Transform(byte[] bytes, bool protect)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var input = new Blob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        Blob output = default;
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            var ok = protect ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows 사용자 비밀값 보호/복원 실패. 기존 파일을 유지합니다.");
            var result = new byte[output.Length]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally
        {
            // Clear both native buffers before releasing them.
            for (var i = 0; i < input.Length; i++) Marshal.WriteByte(input.Data, i, 0);
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != IntPtr.Zero) { for (var i = 0; i < output.Length; i++) Marshal.WriteByte(output.Data, i, 0); LocalFree(output.Data); }
        }
    }
    public static void Save<T>(string file, T value)
    {
        PrivateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        var plain = JsonSerializer.SerializeToUtf8Bytes(value, Wire.Json);
        try { Wire.AtomicWrite(file, Magic.Concat(Transform(plain, true)).ToArray()); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public static T Load<T>(string file)
    {
        var bytes = File.ReadAllBytes(file);
        if (!bytes.AsSpan().StartsWith(Magic)) throw new InvalidDataException("보호된 비밀값 파일 형식이 아닙니다.");
        var plain = Transform(bytes[Magic.Length..], false);
        try { return JsonSerializer.Deserialize<T>(plain, Wire.Json) ?? throw new InvalidDataException("비밀값이 비어 있습니다."); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public static T LoadOrMigrate<T>(string legacyFile)
    {
        var protectedFile = legacyFile + ".protected";
        if (File.Exists(protectedFile)) {
            var saved = Load<T>(protectedFile); // A failed decrypt must never reset enrollment.
            if (File.Exists(legacyFile)) {
                var legacy = Wire.Load<T>(legacyFile);
                if (JsonSerializer.Serialize(saved, Wire.Json) != JsonSerializer.Serialize(legacy, Wire.Json))
                    throw new InvalidDataException("기존 장치 정보와 보호된 등록 정보가 다릅니다. 수동 복구가 필요합니다.");
                File.Delete(legacyFile); // Finish an interrupted, already verified migration.
            }
            return saved;
        }
        var value = Wire.Load<T>(legacyFile);
        Save(protectedFile, value);
        _ = Load<T>(protectedFile); // Verify before retiring the legacy plaintext.
        File.Delete(legacyFile);
        return value;
    }
}
