using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using SmbManager.Local;
namespace SmbManager.Local.Client;

public sealed record SmbResetResult(bool Completed,int Disconnected,string[] Remaining,string[] Problems);
public sealed record InstallCompletionResult(bool Completed,string Sid,LegacyMigrationResult? Legacy,SmbResetResult? Connections,string Message);
public static class InstallCompletion
{
    public static InstallCompletionResult Run(string expectedSid)
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException();
        var sid=WindowsIdentity.GetCurrent().User?.Value??"";
        if(sid is "" or "S-1-5-18" or "S-1-5-19" or "S-1-5-20" || !string.Equals(sid,expectedSid,StringComparison.Ordinal))
            return new(false,sid,null,null,"설치를 시작한 Windows 사용자와 실행 계정이 다릅니다. 직원의 Windows 계정으로 설치를 진행하세요. 다른 사용자의 연결은 변경하지 않았습니다.");
        var legacy=LegacyMigration.RunForCurrentUser(rescan:true);
        if(!legacy.Completed)return new(false,sid,legacy,null,legacy.Message);
        var connections=CurrentUserSmbReset.DisconnectAll();
        return new(connections.Completed,sid,legacy,connections,connections.Completed
            ?"설치 완료: 구버전을 정리하고 이 Windows 사용자 세션의 기존 SMB 연결과 저장 매핑을 해제했습니다. 설정과 저장 암호는 보존했습니다."
            :"프로그램 파일 설치는 완료됐지만 기존 연결을 모두 해제하지 못했습니다. 공유폴더의 작업 파일과 탐색기 창을 닫고 설치를 다시 실행하세요. 강제 해제하지 않았습니다.");
    }
}

// Current logon session only. WNet/NetUse are used directly: no shells, firewall,
// credential deletion, other-user tokens or forced closure of open files.
public static class CurrentUserSmbReset
{
    private sealed record Link(string Local,string Remote,string Provider);
    public static SmbResetResult DisconnectAll()
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException();
        var problems=new List<string>();var disconnected=0;
        try
        {
            var provider=ProviderName();
            var links=Enumerate(1).Concat(Enumerate(3)).Where(p=>IsSmb(p,provider)).DistinctBy(p=>p.Local+"|"+p.Remote,StringComparer.OrdinalIgnoreCase).ToArray();
            foreach(var link in links.OrderBy(p=>p.Local.Length==0?1:0))
            {
                var target=link.Local.Length>0?link.Local:link.Remote;
                var code=WNetCancelConnection2(target,1,false);
                if(code==0)disconnected++;else if(code!=2250)problems.Add($"{target}: Windows {code} (열린 파일을 닫고 다시 시도하세요)");
            }
            // Include deviceless IPC$/UNC uses not exposed by the network provider enumeration.
            foreach(var remote in Uses())
            {
                var code=NetUseDel(null,remote,0);
                if(code==0)disconnected++;else if(code!=2250)problems.Add($"{remote}: Windows {code} (강제 해제하지 않음)");
            }
            var remaining=Enumerate(1).Concat(Enumerate(3)).Where(p=>IsSmb(p,provider)).Select(p=>p.Local.Length>0?p.Local+" "+p.Remote:p.Remote).Concat(Uses()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            // Earlier transient failures are recoverable if final enumeration proves all are gone.
            return new(remaining.Length==0,disconnected,remaining,remaining.Length==0?[]:problems.ToArray());
        }
        catch(Exception e)when(e is Win32Exception or IOException or InvalidOperationException)
        {problems.Add(e.Message);return new(false,disconnected,[],problems.ToArray());}
    }
    private static bool IsSmb(Link p,string provider)=>p.Remote.StartsWith(@"\\",StringComparison.Ordinal)&&string.Equals(p.Provider,provider,StringComparison.OrdinalIgnoreCase);
    private static string ProviderName(){var length=1024;var name=new StringBuilder(length);var code=WNetGetProviderName(0x00020000,name,ref length);if(code!=0)throw new Win32Exception(code,"Windows SMB provider unavailable");return name.ToString();}
    private static Link[] Enumerate(int scope)
    {
        var code=WNetOpenEnum(scope,0,0,IntPtr.Zero,out var handle);if(code!=0)throw new Win32Exception(code,"Windows SMB enumeration failed");
        var links=new List<Link>();IntPtr buffer=IntPtr.Zero;
        try
        {
            var capacity=65536;buffer=Marshal.AllocHGlobal(capacity);
            while(true)
            {
                var count=-1;var size=capacity;code=WNetEnumResource(handle,ref count,buffer,ref size);
                if(code==259)break;
                if(code==234){if(size>4*1024*1024)throw new IOException("연결 목록이 너무 커 자동 해제를 보류했습니다.");Marshal.FreeHGlobal(buffer);capacity=Math.Max(size,capacity*2);buffer=Marshal.AllocHGlobal(capacity);continue;}
                if(code!=0)throw new Win32Exception(code,"Windows SMB enumeration failed");
                if(count<=0)break;
                var elementSize=Marshal.SizeOf<Resource>();if((long)count*elementSize>capacity)throw new IOException("잘못된 연결 목록");
                for(var i=0;i<count;i++){var p=Marshal.PtrToStructure<Resource>(IntPtr.Add(buffer,i*elementSize));links.Add(new(p.Local??"",p.Remote??"",p.Provider??""));}
            }
        }finally{if(buffer!=IntPtr.Zero)Marshal.FreeHGlobal(buffer);WNetCloseEnum(handle);}
        return links.ToArray();
    }
    private static string[] Uses()
    {
        var result=new List<string>();uint resume=0;
        do
        {
            var code=NetUseEnum(null,1,out var buffer,-1,out var count,out _,ref resume);
            try
            {
                if(code is not (0 or 234))throw new Win32Exception(code,"Windows SMB use enumeration failed");
                var size=Marshal.SizeOf<Use>();for(var i=0;i<count;i++){var use=Marshal.PtrToStructure<Use>(IntPtr.Add(buffer,i*size));var remote=Marshal.PtrToStringUni(use.Remote)??"";if(remote.StartsWith(@"\\",StringComparison.Ordinal))result.Add(remote);}
            }finally{if(buffer!=IntPtr.Zero)NetApiBufferFree(buffer);}
            if(code==0)break;
        }while(true);
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct Resource{public int Scope,Type,DisplayType,Usage;public string? Local,Remote,Comment,Provider;}
    [StructLayout(LayoutKind.Sequential)]private struct Use{public IntPtr Local,Remote,Password;public uint Status,AsgType,RefCount,UseCount;}
    [DllImport("mpr.dll",CharSet=CharSet.Unicode)]private static extern int WNetGetProviderName(int type,StringBuilder name,ref int length);
    [DllImport("mpr.dll",CharSet=CharSet.Unicode)]private static extern int WNetOpenEnum(int scope,int type,int usage,IntPtr resource,out IntPtr handle);
    [DllImport("mpr.dll",CharSet=CharSet.Unicode)]private static extern int WNetEnumResource(IntPtr handle,ref int count,IntPtr buffer,ref int size);
    [DllImport("mpr.dll")]private static extern int WNetCloseEnum(IntPtr handle);
    [DllImport("mpr.dll",CharSet=CharSet.Unicode)]private static extern int WNetCancelConnection2(string name,int flags,[MarshalAs(UnmanagedType.Bool)]bool force);
    [DllImport("netapi32.dll",CharSet=CharSet.Unicode)]private static extern int NetUseEnum(string? server,int level,out IntPtr buffer,int length,out int count,out int total,ref uint resume);
    [DllImport("netapi32.dll",CharSet=CharSet.Unicode)]private static extern int NetUseDel(string? server,string name,int force);
    [DllImport("netapi32.dll")]private static extern int NetApiBufferFree(IntPtr buffer);
}