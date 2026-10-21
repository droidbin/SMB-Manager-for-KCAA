using System.Runtime.InteropServices;
using System.Text;
using SmbManager.Local;

namespace SmbManager.Local.Client;

// Update only an existing, exact host Windows credential belonging to the previous
// signed assignment. No enumeration/deletion, no new persistence, no secrets in state/logs.
internal static class WindowsCredential
{
    internal static string Server(string unc) => unc.StartsWith(@"\\",StringComparison.Ordinal)
        ? unc[2..].Split('\\',StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()??"" : "";

    internal static string UpdateExisting(string server, ShareCredential next, string[] previous)
    {
        if(!OperatingSystem.IsWindows() || server.Length==0 || previous.Length==0)return "";
        if(!CredRead(server,2,0,out var pointer))
            return Marshal.GetLastWin32Error()==1168?"":" · Windows 저장 계정 확인 실패 (연결은 정상)";
        IntPtr user=IntPtr.Zero,blob=IntPtr.Zero;byte[]? bytes=null;
        try
        {
            var value=Marshal.PtrToStructure<Credential>(pointer);
            var old=Marshal.PtrToStringUni(value.UserName)??"";
            if(string.Equals(old,next.Account,StringComparison.OrdinalIgnoreCase))return "";
            if(!previous.Contains(old,StringComparer.OrdinalIgnoreCase))
                return " · 다른 용도의 Windows 저장 계정은 보존했습니다.";
            bytes=Encoding.Unicode.GetBytes(next.Password);
            if(bytes.Length>2560)return " · Windows 저장 암호 길이 초과 (연결은 정상)";
            user=Marshal.StringToCoTaskMemUni(next.Account);
            blob=Marshal.AllocCoTaskMem(Math.Max(1,bytes.Length));
            Marshal.Copy(bytes,0,blob,bytes.Length);
            value.UserName=user;value.CredentialBlob=blob;value.CredentialBlobSize=(uint)bytes.Length;
            return CredWrite(ref value,0)?" · Windows 저장 계정 갱신 완료":" · Windows 저장 계정 갱신 실패 (연결은 정상)";
        }
        finally
        {
            if(bytes is not null){if(blob!=IntPtr.Zero)Marshal.Copy(new byte[bytes.Length],0,blob,bytes.Length);System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);}
            if(blob!=IntPtr.Zero)Marshal.FreeCoTaskMem(blob);
            if(user!=IntPtr.Zero)Marshal.FreeCoTaskMem(user);
            CredFree(pointer);
        }
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Credential
    {
        public uint Flags,Type;public IntPtr TargetName,Comment;public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;public IntPtr CredentialBlob;public uint Persist,AttributeCount;public IntPtr Attributes,TargetAlias,UserName;
    }
    [DllImport("advapi32.dll",EntryPoint="CredReadW",CharSet=CharSet.Unicode,SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]internal static extern bool CredRead(string target,uint type,uint flags,out IntPtr credential);
    [DllImport("advapi32.dll",EntryPoint="CredWriteW",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]internal static extern bool CredWrite(ref Credential credential,uint flags);
    [DllImport("advapi32.dll")]internal static extern void CredFree(IntPtr pointer);
}