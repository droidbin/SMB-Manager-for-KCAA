using System.Net;
using System.Net.Http.Json;
using System.Text;
using SmbManager.Local;
using SmbManager.Local.Client;
internal static class WorkflowTests
{
    public static async Task Run(HttpClient admin,string url,string run,Action<bool,string> check){
        using var guest=Wire.Http(url);
        var departments=(await guest.GetFromJsonAsync<string[]>("api/registration/departments",Wire.Json))!;
        check(departments.Contains("운영부"),"등록 화면에 사용 가능한 부서 목록 제공");
        var hardware=HardwareIdentity.Capture();var session=Guid.NewGuid().ToString();
        await Wire.Read<DepartmentTemplate>(await admin.PostAsJsonAsync("api/admin/department-templates",new DepartmentTemplate("QA사용성",@"QA\ops",[]),Wire.Json));
        var request=new RegistrationRequest("등록 시험 직원","QA사용성","qa",session,hardware);
        var enrolled=await Wire.Read<EnrollReply>(await guest.PostAsJsonAsync("api/registration/request",request,Wire.Json));
        var devices=(await admin.GetFromJsonAsync<DeviceView[]>("api/admin/devices",Wire.Json))!;
        check(devices.Single(d=>d.Id==enrolled.DeviceId) is {Approved:false,RequestedDepartment:"QA사용성",Assignment:null},"코드 입력 없는 요청은 승인·권한 없이 요청 부서만 보관");
        using var device=Wire.Http(url,device:enrolled.DeviceKey);
        using(var response=await device.GetAsync("api/admin/profiles"))check(response.StatusCode==HttpStatusCode.Unauthorized,"등록된 사용자는 서버관리 API 접근 불가");
        using(var response=await device.GetAsync("api/client/credentials/legacy-Y"))check(response.StatusCode==HttpStatusCode.Forbidden,"미승인 장치에 Drive 암호 전달 금지");
        using(var response=await guest.PostAsJsonAsync("api/registration/request",request,Wire.Json))check(response.StatusCode==HttpStatusCode.TooManyRequests,"자동 등록 연속 요청 제한");
        var a=await Wire.Read<Profile>(await admin.PostAsJsonAsync("api/admin/profiles",new SaveProfile("신규 서버 A",@"\\workflow-a\staff","P:",Department:"사업부"),Wire.Json));
        var b=await Wire.Read<Profile>(await admin.PostAsJsonAsync("api/admin/profiles",new SaveProfile("신규 서버 B",@"\\workflow-b\docs","Q:",Department:"교육부"),Wire.Json));
        var template=new DepartmentTemplate("QA사용성",@"QA\ops",[a.Id,b.Id]);
        await Wire.Read<DepartmentTemplate>(await admin.PostAsJsonAsync("api/admin/department-templates",template,Wire.Json));
        using(var approval=await admin.PostAsync($"api/admin/devices/{enrolled.DeviceId}/approve",null))approval.EnsureSuccessStatusCode();
        var policy=(await device.GetFromJsonAsync<PolicyEnvelope>("api/client/policy",Wire.Json))!;
        check(policy.Payload.Profiles.Select(p=>p.Id).Order().SequenceEqual(new[]{a.Id,b.Id}.Order()),"서버관리자 승인 후 부서에 선택된 복수 서버 자동 배정");
        var password="QA-only-"+Guid.NewGuid();
        using(var saved=await admin.PostAsJsonAsync("api/admin/share-credential",new SaveShareCredential("QA사용성",a.Id,@"QA\ops",password),Wire.Json))saved.EnsureSuccessStatusCode();
        var received=await device.GetFromJsonAsync<ShareCredential>($"api/client/credentials/{a.Id}",Wire.Json);
        check(received is not null&&received.Password==password,"승인된 장치에 배정된 폴더 계정만 전달");
        var protectedFiles=Directory.GetFiles(Path.Combine(run,".server-secrets"),"*.registration");
        check(protectedFiles.Length==1&&!Encoding.UTF8.GetString(File.ReadAllBytes(protectedFiles[0])).Contains(password),"관리 PC의 Drive 암호 DPAPI 암호화 보관");
        using(var denied=await device.GetAsync("api/client/credentials/legacy-Y"))check(denied.StatusCode==HttpStatusCode.Forbidden,"부서에 배정되지 않은 폴더 암호 요청 거절");
        using(var beat=await device.PostAsJsonAsync("api/client/heartbeat",new Heartbeat(session,false,[],Connections:[new(a.Id,a.Drive,a.Unc,"","임의 정상 주장",true,ConnectionState:"Disconnected")]),Wire.Json))beat.EnsureSuccessStatusCode();
        var view=(await admin.GetFromJsonAsync<DeviceView[]>("api/admin/devices",Wire.Json))!.Single(d=>d.Id==enrolled.DeviceId);
        check(view.Connections!.Single().ConnectionState=="Disconnected"&&view.Connections!.Single().AssignmentState.Contains("끊김"),"수동 연결 해제 보고를 관리자 화면에서 끊김으로 유지");
        var command=await Wire.Read<CommandView>(await admin.PostAsync($"api/admin/devices/{enrolled.DeviceId}/reconnect",null));
        var lease=(await Wire.Read<LeaseReply>(await device.PostAsync("api/client/lease",null))).Lease!;
        check(lease.Envelope.Payload.Operation=="ReconnectAssigned"&&SimulationExecutor.Valid(new(url,enrolled.DeviceId,enrolled.DeviceKey,enrolled.PublicKey),session,lease.Envelope),"관리자 재연결 요청의 대상·세션·서명 검증");
        using(var result=await device.PostAsJsonAsync($"api/client/commands/{command.Id}/result",new ResultRequest(lease.LeaseId,"Failed","QA does not connect real shares","RealSmb"),Wire.Json))check(result.IsSuccessStatusCode,"실제 재연결 결과를 모의 결과와 구분해 수집");
        using(var denied=await device.PostAsJsonAsync("api/admin/devices/connections",new BulkConnectionRequest("Disconnect",[enrolled.DeviceId]),Wire.Json))check(denied.StatusCode==HttpStatusCode.Unauthorized,"직원은 관리자 일괄 연결 제어 불가");
        using(var duplicate=await admin.PostAsJsonAsync("api/admin/devices/connections",new BulkConnectionRequest("Disconnect",[enrolled.DeviceId,enrolled.DeviceId]),Wire.Json))check(duplicate.StatusCode==HttpStatusCode.BadRequest,"중복 대상 일괄 요청 거절");
        var bulk=await Wire.Read<BulkConnectionResult[]>(await admin.PostAsJsonAsync("api/admin/devices/connections",new BulkConnectionRequest("Disconnect",[enrolled.DeviceId,"missing-device"]),Wire.Json));
        check(bulk.Single(r=>r.DeviceId==enrolled.DeviceId).Accepted&&!bulk.Single(r=>r.DeviceId=="missing-device").Accepted,"일괄 연결 해제 대상별 접수·제외 구분");
        var held=(await admin.GetFromJsonAsync<DeviceView[]>("api/admin/devices",Wire.Json))!.Single(d=>d.Id==enrolled.DeviceId);
        var heldPolicy=(await device.GetFromJsonAsync<PolicyEnvelope>("api/client/policy",Wire.Json))!.Payload;
        check(held.ConnectionsHeld&&held.Assignment!.ProfileIds.Contains(a.Id)&&heldPolicy.Profiles.Length==0&&!heldPolicy.ResumeStopped&&!heldPolicy.ForceDisconnect,"관리자 연결 보류는 배정 보존·연결 권한 회수·강제 해제 금지");
        using(var denied=await device.GetAsync($"api/client/credentials/{a.Id}"))check(denied.StatusCode==HttpStatusCode.Forbidden,"관리자 연결 보류 중 암호 재요청 거절");
        var resumed=await Wire.Read<BulkConnectionResult[]>(await admin.PostAsJsonAsync("api/admin/devices/connections",new BulkConnectionRequest("Reconnect",[enrolled.DeviceId]),Wire.Json));
        var resumedView=(await admin.GetFromJsonAsync<DeviceView[]>("api/admin/devices",Wire.Json))!.Single(d=>d.Id==enrolled.DeviceId);
        var resumedPolicy=(await device.GetFromJsonAsync<PolicyEnvelope>("api/client/policy",Wire.Json))!.Payload;
        check(resumed.Single().Accepted&&!resumedView.ConnectionsHeld&&resumedPolicy.Profiles.Any(p=>p.Id==a.Id)&&resumedPolicy.ResumeStopped,"일괄 재연결은 관리자 연결 보류를 해제하고 배정 복구");
        var resumedLease=(await Wire.Read<LeaseReply>(await device.PostAsync("api/client/lease",null))).Lease!;
        using(var completed=await device.PostAsJsonAsync($"api/client/commands/{resumedLease.Envelope.Payload.Id}/result",new ResultRequest(resumedLease.LeaseId,"Failed","QA does not connect real shares","RealSmb"),Wire.Json))completed.EnsureSuccessStatusCode();
        var assignment=view.Assignment!;
        await Wire.Read<DeviceAssignment>(await admin.PostAsJsonAsync($"api/admin/devices/{enrolled.DeviceId}/assignment",new SaveAssignment("QA사용성",@"QA\ops",[],ExpectedRevision:(await device.GetFromJsonAsync<PolicyEnvelope>("api/client/policy",Wire.Json))!.Payload.AssignmentRevision,AccessRole:"임원"),Wire.Json));
        var executive=(await device.GetFromJsonAsync<PolicyEnvelope>("api/client/policy",Wire.Json))!;
        var all=(await admin.GetFromJsonAsync<Profile[]>("api/admin/profiles",Wire.Json))!.Where(p=>ProfileRules.Available(p)).ToArray();
        check(executive.Payload.AccessRole=="임원"&&executive.Payload.Profiles.Length==all.Length,"임원은 전체 등록 서버·폴더 이용 가능");
        using(var denied=await device.GetAsync("api/admin/devices"))check(denied.StatusCode==HttpStatusCode.Unauthorized,"임원에게 서버관리 권한은 부여되지 않음");
        using(var engine=new RealSmbEngine(Path.Combine(run,"workflow-quarantine"))){engine.EnableManagedMode();engine.LatchQuarantine("QA");var denied=false;try{await engine.ReconnectNow(CancellationToken.None);}catch(InvalidOperationException){denied=true;}check(denied,"수동 다시 연결로 보안 격리 해제 불가");}
        using(var revoked=await admin.PostAsJsonAsync($"api/admin/devices/{enrolled.DeviceId}/assignment",new SaveAssignment("QA사용성",@"QA\ops",[],ExpectedRevision:executive.Payload.AssignmentRevision),Wire.Json))revoked.EnsureSuccessStatusCode();
        using(var denied=await device.GetAsync($"api/client/credentials/{a.Id}"))check(denied.StatusCode==HttpStatusCode.Forbidden,"부서 배정 회수 즉시 기존 암호 조회 거절");
    }
}