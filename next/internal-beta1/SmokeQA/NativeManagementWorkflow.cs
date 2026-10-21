using System.Diagnostics;
using System.Net.Http.Json;
using SmbManager.Local;
using SmbManager.Local.Client;
internal static class NativeManagementWorkflow
{
    public static async Task Run(string root,string unc,string drive,Action<bool,string> check,Func<string,int> disconnect){
        var serverExe=Environment.GetEnvironmentVariable("SMB_QA_SERVER_EXE")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"SMB Manager Internal Beta 1","ManagementServer","ManagementServer.exe");
        if(FileVersionInfo.GetVersionInfo(serverExe).FileVersion!=FileVersionInfo.GetVersionInfo(typeof(ClientAgent).Assembly.Location).FileVersion)throw new Exception("Matching QA server and engine versions required.");
        var work=Path.Combine(root,"management-workflow");Directory.CreateDirectory(work);
        var start=new ProcessStartInfo(serverExe){UseShellExecute=false,CreateNoWindow=true};start.ArgumentList.Add("--run-dir");start.ArgumentList.Add(work);
        using var server=Process.Start(start)??throw new Exception("QA server start failed");ClientAgent? agent=null;Task? worker=null;using var stop=new CancellationTokenSource();
        try{
            var readyFile=Path.Combine(work,"ready.json");for(var i=0;i<100&&!File.Exists(readyFile);i++){if(server.HasExited)throw new Exception("QA server exited");await Task.Delay(100);}
            var ready=Wire.Load<ServerReady>(readyFile);using var admin=Wire.Http(ready.Url,admin:ready.AdminKey);
            var profile=await Wire.Read<Profile>(await admin.PostAsJsonAsync("api/admin/profiles",new SaveProfile("네이티브 관리 시험",unc,drive,Department:"QA"),Wire.Json));
            await Wire.Read<DepartmentTemplate>(await admin.PostAsJsonAsync("api/admin/department-templates",new DepartmentTemplate("QA",Environment.UserDomainName+@"\"+Environment.UserName,[profile.Id]),Wire.Json));
            agent=await ClientAgent.Create(new(ready.Url,"","실제 등록 시험","QA"),Path.Combine(work,"client"));worker=agent.Run(stop.Token);
            var id=agent.Identity.DeviceId;
            using(var approved=await admin.PostAsync($"api/admin/devices/{id}/approve",null))approved.EnsureSuccessStatusCode();
            async Task<DeviceView> Wait(Func<DeviceView,bool> predicate){var deadline=DateTime.UtcNow.AddSeconds(50);do{var list=await admin.GetFromJsonAsync<DeviceView[]>("api/admin/devices",Wire.Json)??[];var d=list.Single(x=>x.Id==id);if(predicate(d))return d;await Task.Delay(250);}while(DateTime.UtcNow<deadline);throw new Exception("Native management workflow timeout");}
            var connected=await Wait(d=>d.Connections?.Any(c=>c.ProfileId==profile.Id&&c.ConnectionState=="Connected"&&c.AppOwned)==true);
            check(connected.Approved,"자동 등록 → 서버 승인 → 실제 클라이언트 엔진 연결 → 관리자 정상 보고");
            var current=connected.Assignment!;
            await Wire.Read<DeviceAssignment>(await admin.PostAsJsonAsync($"api/admin/devices/{id}/assignment",new SaveAssignment("QA",current.ExpectedAccount,[profile.Id],Reconnect:false,ExpectedRevision:current.Revision),Wire.Json));
            await Wait(d=>d.AppliedPolicyRevision>connected.AppliedPolicyRevision);
            // Permit the automatic loop to observe Reconnect=false before removing only the QA mapping.
            await Task.Delay(5500);
            check(disconnect(drive)==0,"원격 재연결 시험용 수동 매핑 해제");
            var missing=await Wait(d=>d.Connections?.Any(c=>c.ProfileId==profile.Id&&c.ConnectionState=="Disconnected")==true);
            check(missing.Online,"관리 서버에서 실제 수동 해제 상태 감지");
            var command=await Wire.Read<CommandView>(await admin.PostAsync($"api/admin/devices/{id}/reconnect",null));
            var deadline=DateTime.UtcNow.AddSeconds(50);string outcome="";
            do{var commands=await admin.GetFromJsonAsync<CommandView[]>("api/admin/commands",Wire.Json)??[];outcome=commands.Single(c=>c.Id==command.Id).State;if(outcome is "Succeeded" or "Failed")break;await Task.Delay(250);}while(DateTime.UtcNow<deadline);
            check(outcome=="Succeeded","관리자 재연결 요청 → 서명 명령 → 실제 Windows SMB 복구 완료 보고");
            await Wait(d=>d.Connections?.Any(c=>c.ProfileId==profile.Id&&c.ConnectionState=="Connected")==true);
            check(File.ReadAllText(drive+@"\qa-marker.txt")=="KCAA WORKFLOW TEST","관리자 요청 복구 후 시험 공유 파일 접근 정상");
        }finally{
            stop.Cancel();if(worker is not null)try{await worker;}catch(OperationCanceledException){}
            if(agent is not null){try{await agent.StopRealConnections(CancellationToken.None);}finally{agent.Dispose();}}
            if(!server.HasExited){server.Kill(true);server.WaitForExit(5000);}
        }
    }
}