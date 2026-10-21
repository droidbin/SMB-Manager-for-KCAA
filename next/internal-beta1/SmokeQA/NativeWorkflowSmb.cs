using System.Runtime.InteropServices;
using System.Text;
using SmbManager.Local;
using SmbManager.Local.Client;
internal static class NativeWorkflowSmb
{
    [DllImport("mpr.dll",CharSet=CharSet.Unicode)]private static extern int WNetGetConnection(string name,StringBuilder remote,ref int length);
    [DllImport("mpr.dll",CharSet=CharSet.Unicode)]private static extern int WNetCancelConnection2(string name,int flags,[MarshalAs(UnmanagedType.Bool)]bool force);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern uint QueryDosDevice(string name,StringBuilder target,int length);
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct Resource{public int Scope,Type,DisplayType,Usage;public string? LocalName,RemoteName,Comment,Provider;}
    [DllImport("mpr.dll",CharSet=CharSet.Unicode)]private static extern int WNetAddConnection2(ref Resource resource,string? password,string? user,int flags);
    private static int Mapping(string drive){var text=new StringBuilder(32768);var size=text.Capacity;return WNetGetConnection(drive,text,ref size);}
    public static async Task Run(string[] args){
        var root=Path.GetFullPath(Wire.Arg(args,"--root"));var unc=Wire.Arg(args,"--test-share");var prefix=@"\\"+Environment.MachineName+@"\KCAAWorkflow";
        if(!unc.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)||Environment.MachineName!="DESKTOP-GMEG262")throw new InvalidOperationException("Dedicated native test share required.");
        var before=DriveInfo.GetDrives().Select(d=>d.Name).Order().ToArray();var passed=new List<string>();string? failure=null;string drive="";
        using var engine=new RealSmbEngine(Path.Combine(root,"engine"));
        void Check(bool ok,string text){if(!ok)throw new Exception(text);passed.Add(text);}
        try{
            drive=new[]{"P:","Q:","R:","S:","T:"}.First(d=>Mapping(d)==2250&&QueryDosDevice(d,new StringBuilder(32768),32768)==0&&Marshal.GetLastWin32Error()==2);
            var p=new Profile("native-test","격리된 시험 공유","SimulateConnect",1,unc,drive,"QA");
            var policy=new DepartmentPolicy("qa","qa",1,"QA",Environment.UserDomainName+@"\"+Environment.UserName,[p],true,false,false,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow.AddSeconds(30),1);
            engine.EnableManagedMode();await engine.ApplyPolicy(policy,CancellationToken.None);
            var first=await engine.Connect("first",p,CancellationToken.None);Check(first.State=="Succeeded","실제 Windows SMB 연결 성공: "+first.Detail);
            await engine.RefreshConnections(CancellationToken.None);Check(engine.Connections().Single(c=>c.ProfileId==p.Id).ConnectionState=="Connected","실제 연결 상태 Connected 감지");
            Check(File.ReadAllText(drive+@"\qa-marker.txt")=="KCAA WORKFLOW TEST","별도 시험 공유의 파일 읽기 성공");
            Check(WNetCancelConnection2(drive,0,false)==0,"탐색기 수동 해제와 같은 Windows 매핑 해제 성공");
            await engine.RefreshConnections(CancellationToken.None);Check(engine.Connections().Single(c=>c.ProfileId==p.Id).ConnectionState=="Disconnected","수동 해제 후 끊김 감지");
            await engine.ReconnectDue(CancellationToken.None);await engine.RefreshConnections(CancellationToken.None);Check(engine.Connections().Single(c=>c.ProfileId==p.Id).ConnectionState=="Connected","자동 재연결 엔진으로 실제 복구");
            Check(WNetCancelConnection2(drive,0,false)==0,"수동 다시 연결 시험용 해제 성공");
            var retry=await engine.ReconnectNow(CancellationToken.None);Check(retry.Length==1&&retry[0].State=="Succeeded"&&engine.Connections().Single(c=>c.ProfileId==p.Id).ConnectionState=="Connected","사용자 다시 연결 기능으로 실제 복구");
            engine.LatchQuarantine("native QA");await engine.Contain(CancellationToken.None);Check(Mapping(drive)==2250,"격리 시 시험 앱 소유 매핑 회수");
            var denied=false;try{await engine.ReconnectNow(CancellationToken.None);}catch(InvalidOperationException){denied=true;}Check(denied,"격리 중 다시 연결 금지");
            using var externalEngine=new RealSmbEngine(Path.Combine(root,"external-engine"));externalEngine.EnableManagedMode();await externalEngine.ApplyPolicy(policy with{IssuedAt=DateTimeOffset.UtcNow,ExpiresAt=DateTimeOffset.UtcNow.AddSeconds(30)},CancellationToken.None);
            var resource=new Resource{Type=1,LocalName=drive,RemoteName=unc};Check(WNetAddConnection2(ref resource,null,null,0)==0,"시험용 외부 매핑 생성");
            var preserved=await externalEngine.Connect("external",p,CancellationToken.None);Check(preserved.State=="Succeeded"&&preserved.Detail.StartsWith("ALREADY_CONNECTED"),"기존 동일 계정 연결은 앱 소유권 없이 보존");
            Check(WNetCancelConnection2(drive,0,false)==0,"기존 외부 연결의 수동 해제 시험");await externalEngine.RefreshConnections(CancellationToken.None);await externalEngine.ReconnectDue(CancellationToken.None);await externalEngine.RefreshConnections(CancellationToken.None);
            Check(externalEngine.Connections().Single(c=>c.ProfileId==p.Id) is {ConnectionState:"Connected",AppOwned:true},"외부 연결을 사용하던 PC도 수동 해제 후 배정 연결 자동 복구");await externalEngine.DisconnectAllOwned(CancellationToken.None);await NativeManagementWorkflow.Run(root,unc,drive,Check,d=>WNetCancelConnection2(d,0,false));
            var startupDirectory=Path.Combine(root,"startup-restore");var startupPolicy=policy with{Reconnect=false,IssuedAt=DateTimeOffset.UtcNow,ExpiresAt=DateTimeOffset.UtcNow.AddMinutes(2)};
            using(var initial=new RealSmbEngine(startupDirectory)){initial.EnableManagedMode();await initial.ApplyPolicy(startupPolicy,default);await initial.ReconnectDue(default);Check(Mapping(drive)==0,"Startup performs actual connection with continuous reconnect disabled");await initial.DisconnectAllOwned(default);}
            using(var restarted=new RealSmbEngine(startupDirectory)){restarted.EnableManagedMode();await restarted.ApplyPolicy(startupPolicy with{IssuedAt=DateTimeOffset.UtcNow},default);await restarted.ReconnectDue(default);await restarted.RefreshConnections(default);Check(Mapping(drive)==0&&restarted.Connections().Single(c=>c.ProfileId==p.Id).ConnectionState=="Connected","Fresh app session restores actual mapping without manual action");await restarted.DisconnectAllOwned(default);}
            using(var held=new RealSmbEngine(Path.Combine(root,"startup-held"))){held.EnableManagedMode();await held.ApplyPolicy(startupPolicy with{ConnectionsHeld=true,IssuedAt=DateTimeOffset.UtcNow},default);await held.ReconnectDue(default);Check(Mapping(drive)==2250,"Administrator hold prevents startup from recreating actual mapping");}
            using var unavailable=new RealSmbEngine(Path.Combine(root,"unavailable-engine"));unavailable.EnableManagedMode();
            var missing=p with{Id="missing-test",Unc=unc+"-missing"};var missingPolicy=policy with{Profiles=[missing],IssuedAt=DateTimeOffset.UtcNow,ExpiresAt=DateTimeOffset.UtcNow.AddMinutes(2)};
            await unavailable.ApplyPolicy(missingPolicy,CancellationToken.None);
            var failureResult=await unavailable.Connect("missing",missing,CancellationToken.None);
            Check(failureResult.State=="Failed"&&Mapping(drive)==2250,"존재하지 않는 시험 공유 연결 실패 및 매핑 미생성");
            var pendingState=(RealSmbEngine.State)typeof(RealSmbEngine).GetField("state",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.GetValue(unavailable)!;var pending=pendingState.Approvals[missing.Id];
            Check(!pending.Pending&&pending.Enabled&&pending.Failures==1,"공유 접근 실패 뒤 미완료 고착 없이 재시도 예약");
            Check(pending.NextAttempt>DateTimeOffset.UtcNow.AddSeconds(8),"공유 실패 직후 재연결 폭주 방지 대기");
            await unavailable.ReconnectDue(CancellationToken.None);Check(pending.Failures==1,"대기 시간이 지나기 전 자동 재시도하지 않음");
            pending.NextAttempt=DateTimeOffset.UtcNow.AddSeconds(-1);await unavailable.ReconnectDue(CancellationToken.None);Check(pending.Failures==2&&!pending.Pending,"대기 만료 뒤 실제 Windows 재시도 및 실패 상태 갱신");
            using var conflict=new RealSmbEngine(Path.Combine(root,"conflict-engine"));conflict.EnableManagedMode();var reserved=p with{Id="local-conflict",Drive=Path.GetPathRoot(Environment.SystemDirectory)![..2]};
            await conflict.ApplyPolicy(policy with{Profiles=[reserved],IssuedAt=DateTimeOffset.UtcNow,ExpiresAt=DateTimeOffset.UtcNow.AddMinutes(2)},CancellationToken.None);
            var conflictResult=await conflict.Connect("conflict",reserved,CancellationToken.None);Check(conflictResult.Detail.StartsWith("DRIVE_CONFLICT"),"로컬 시스템 드라이브 문자 충돌은 Windows 연결 전에 거절");
            Check(File.Exists(Path.Combine(Environment.SystemDirectory,"kernel32.dll")),"드라이브 문자 충돌 후 로컬 시스템 파일 보존");
        }catch(Exception e){failure=e.ToString();Environment.ExitCode=1;}
        finally{
            try{await engine.DisconnectAllOwned(CancellationToken.None);}catch(Exception e){failure??=e.ToString();Environment.ExitCode=1;}
            var after=DriveInfo.GetDrives().Select(d=>d.Name).Order().ToArray();if(!before.SequenceEqual(after)){failure??="Drive letters changed after cleanup";Environment.ExitCode=1;}
            Wire.Save(Path.Combine(root,"native-smb-report.json"),new{passed,failure,drive,before,after,finishedAt=DateTimeOffset.UtcNow});
        }
    }
}