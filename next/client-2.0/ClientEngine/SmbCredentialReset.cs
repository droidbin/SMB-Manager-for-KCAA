using System.Runtime.InteropServices;
using System.Text.Json;
using SmbManager.Local;
namespace SmbManager.Local.Client;

public sealed record CredentialResetResult(bool Completed,int Deleted,string[] Servers,string[] Problems);
// User-authorized Drive server domain-password entries only; no generic credential
// enumeration, password blob reads, browser credentials or remote desktop targets.
public static class SmbCredentialReset {
    internal static string NormalizeHost(string text) {
        var value=text.Trim().TrimEnd('.');
        if(value.StartsWith(@"\\"))value=WindowsCredential.Server(value);
        return value.Length is >0 and <=253 && value.All(c=>char.IsLetterOrDigit(c)||c is '.' or '-' or '_' or ':') ? value.ToLowerInvariant():"";
    }
    internal static string[] KnownServers(string directory) {
        var hosts=new HashSet<string>(StringComparer.OrdinalIgnoreCase){"192.168.1.65"};
        var folder=Path.Combine(directory,"real-smb");
        if(Directory.Exists(folder))foreach(var file in Directory.EnumerateFiles(folder,"smb-policy.json",SearchOption.AllDirectories).Take(200)) {
            using var data=JsonDocument.Parse(File.ReadAllBytes(file));
            if(data.RootElement.TryGetProperty("approvals",out var approvals))foreach(var item in approvals.EnumerateObject())
                if(item.Value.TryGetProperty("profile",out var profile)&&profile.TryGetProperty("unc",out var unc))hosts.Add(WindowsCredential.Server(unc.GetString()??""));
            if(data.RootElement.TryGetProperty("previousServerAccounts",out var previous))foreach(var item in previous.EnumerateObject())hosts.Add(item.Name);
        }
        return hosts.Select(NormalizeHost).Where(h=>h.Length>0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    public static CredentialResetResult DeleteForServers(IEnumerable<string> servers) {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException();
        var hosts=servers.Select(NormalizeHost).Where(h=>h.Length>0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var problems=new List<string>();var deleted=0;
        foreach(var host in hosts) {
            if(CredDelete(host,2,0))deleted++;
            else {var code=Marshal.GetLastWin32Error();if(code!=1168)problems.Add(host+": Windows "+code);}
            if(WindowsCredential.CredRead(host,2,0,out var pointer)){WindowsCredential.CredFree(pointer);problems.Add(host+": 저장 계정이 남아 있습니다.");}
            else {var code=Marshal.GetLastWin32Error();if(code!=1168)problems.Add(host+": 확인 실패 "+code);}
        }
        return new(problems.Count==0,deleted,hosts,problems.ToArray());
    }
    [DllImport("advapi32.dll",EntryPoint="CredDeleteW",CharSet=CharSet.Unicode,SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]private static extern bool CredDelete(string target,uint type,uint flags);
}
