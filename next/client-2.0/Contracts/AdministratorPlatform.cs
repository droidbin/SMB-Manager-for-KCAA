namespace SmbManager.Local;

public static class AdministratorPlatform
{
    public const string Requirement = "관리자 앱은 Windows 11 64비트에서만 사용할 수 있습니다.";

    public static bool IsSupported(Version version, bool is64Bit, bool isWorkstation) =>
        is64Bit && isWorkstation && version.Major == 10 && version.Minor == 0 && version.Build >= 22000;

    public static bool IsCurrentSupported()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\ProductOptions");
        return IsSupported(Environment.OSVersion.Version, Environment.Is64BitOperatingSystem,
            string.Equals(key?.GetValue("ProductType") as string, "WinNT", StringComparison.OrdinalIgnoreCase));
    }
}
