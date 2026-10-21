using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using SmbManager.Local;
using SmbManager.Local.Client;

if(args.Contains("--review-regression")){await ReviewRegression.Run(args);return;}
if(args.Contains("--installer-reset-qa")){InstallerResetQA.Run(args);return;}
if(args.Contains("--account-update-qa")){await AccountUpdateQA.Run(args);return;}
if(args.Contains("--native-smb-qa")){await NativeWorkflowSmb.Run(args);return;}
var root = Path.GetFullPath(Wire.Arg(args, "--root"));
var run = Path.Combine(root, ".local", "qa", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
Directory.CreateDirectory(run);
var processes = new List<Process>();
var passed = new List<string>();
string? failure = null;
void Check(bool ok, string text) { if (!ok) throw new InvalidOperationException(text); passed.Add(text); Console.WriteLine("PASS " + text); }
Process Start(string exe, params string[] values)
{
    var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root };
    foreach (var value in values) info.ArgumentList.Add(value);
    var p = Process.Start(info) ?? throw new InvalidOperationException("프로세스 시작 실패"); processes.Add(p); return p;
}
string Exe(string project, bool windows = false) => Wire.Arg(args,"--payload-dir","") is { Length: > 0 } payload
    ? Path.Combine(payload,project,project+".exe")
    : Path.Combine(Wire.Arg(args, "--bin-dir", Path.Combine(root, ".local", "alpha3", "bin")), project, "Release", windows ? "net10.0-windows" : "net10.0", project + ".exe");
async Task<T> Until<T>(Func<Task<T>> read, Func<T, bool> ready)
{
    var end = DateTime.UtcNow.AddSeconds(18);
    do { var value = await read(); if (ready(value)) return value; await Task.Delay(150); } while (DateTime.UtcNow < end);
    throw new TimeoutException("로컬 통합 시험 대기 시간 초과");
}
try
{
    var server = Start(Exe("ManagementServer"), "--run-dir", run, "--qa-diagnostics");
    var readyFile = Path.Combine(run, "ready.json");
    await Until(() => Task.FromResult(File.Exists(readyFile)), value => value || server.HasExited);
    Check(!server.HasExited, "관리 서버 프로세스 시작");
    var ready = Wire.Load<ServerReady>(readyFile);
    Check(Wire.LocalUrl(ready.Url).Host == "127.0.0.1", "서버가 loopback에만 바인딩됨");
    using var admin = Wire.Http(ready.Url, admin: ready.AdminKey);
    using var bad = Wire.Http(ready.Url, admin: "wrong");
    using (var denied = await bad.GetAsync("api/admin/devices")) Check(denied.StatusCode == HttpStatusCode.Unauthorized, "잘못된 관리자 인증 거절");
    var originalDrives = string.Join("|", DriveInfo.GetDrives().Select(d => d.Name).Order());
    async Task<EnrollmentCode> Code() => await Wire.Read<EnrollmentCode>(await admin.PostAsync("api/admin/enrollment-code", null));
    async Task<DeviceView[]> Devices() => await admin.GetFromJsonAsync<DeviceView[]>("api/admin/devices", Wire.Json) ?? [];
    async Task<CommandView[]> Commands() => await admin.GetFromJsonAsync<CommandView[]>("api/admin/commands", Wire.Json) ?? [];
    async Task<CommandView> Create(string id, string profile, int ttl = 60) => await Wire.Read<CommandView>(await admin.PostAsJsonAsync("api/admin/commands", new CreateCommand(id, profile, ttl), Wire.Json));
    var alphaCode = await Code(); var betaCode = await Code();
    var alphaBoot = Path.Combine(run, "alpha-bootstrap.json"); var betaBoot = Path.Combine(run, "beta-bootstrap.json");
    Wire.Save(alphaBoot, new ClientBootstrap(ready.Url, alphaCode.Code, "QA-PC-A"));
    Wire.Save(betaBoot, new ClientBootstrap(ready.Url, betaCode.Code, "QA-PC-B"));
    var alphaDir = Path.Combine(run, "alpha"); var betaDir = Path.Combine(run, "beta");
    Start(Exe("ClientDesktop", true), "--headless", "--bootstrap", alphaBoot, "--state-dir", alphaDir);
    Start(Exe("ClientDesktop", true), "--headless", "--bootstrap", betaBoot, "--state-dir", betaDir);
    var list = await Until(Devices, d => d.Length == 2);
    var alpha = list.Single(d => d.Name == "QA-PC-A"); var beta = list.Single(d => d.Name == "QA-PC-B");
    Check(!alpha.Approved && !beta.Approved, "별도 Client 프로세스 2대 등록 및 승인 대기");
    var duplicateClient = Start(Exe("ClientDesktop", true), "--headless", "--bootstrap", alphaBoot, "--state-dir", alphaDir);
    Check(duplicateClient.WaitForExit(5000) && duplicateClient.ExitCode != 0,"같은 장치 상태를 사용하는 Client 중복 실행 거절");
    using (var reuse = Wire.Http(ready.Url))
    using (var response = await reuse.PostAsJsonAsync("api/enroll", new EnrollRequest(alphaCode.Code, "reuse", "qa", Guid.NewGuid().ToString()), Wire.Json))
        Check(response.StatusCode == HttpStatusCode.BadRequest, "사용한 등록 코드 재사용 거절");
    using (var notReady = await admin.PostAsJsonAsync("api/admin/commands", new CreateCommand(alpha.Id, "demo-share"), Wire.Json))
        Check(notReady.StatusCode == HttpStatusCode.Conflict, "미승인 PC에 명령 발행 거절");
    using (var approval = await admin.PostAsync($"api/admin/devices/{alpha.Id}/approve", null)) approval.EnsureSuccessStatusCode();
    using (var approval = await admin.PostAsync($"api/admin/devices/{beta.Id}/approve", null)) approval.EnsureSuccessStatusCode();
    await Until(Devices, d => d.All(x => x.Approved));
    Check(true, "관리자 승인 후 Client 활성화");
    using (var unknown = await admin.PostAsJsonAsync("api/admin/commands", new CreateCommand(alpha.Id, "arbitrary-shell"), Wire.Json))
        Check(unknown.StatusCode == HttpStatusCode.BadRequest, "허용 목록 밖 작업 거절");
    var success = await Create(alpha.Id, "demo-share");
    var completed = await Until(Commands, c => c.Any(x => x.Id == success.Id && x.State == "Succeeded"));
    Check(completed.Single(c => c.Id == success.Id).Detail.Contains("모의"), "관리자 → 서버 → Client A → 모의 성공 결과 보고");
    Check((await Devices()).Single(d => d.Id == alpha.Id).SessionId == alpha.SessionId, "등록부터 첫 명령 완료까지 동일 세션 유지");
    var alphaState = await Until(() => Task.FromResult(Wire.Load<LocalState>(Path.Combine(alphaDir, "status.json"))), s => s.Completed == 1);
    var betaState = Wire.Load<LocalState>(Path.Combine(betaDir, "status.json"));
    Check(alphaState.SimulatedProfiles.Contains("demo-share") && betaState.Completed == 0, "대상 PC만 실행, 다른 PC 상태 보존");
    var failed = await Create(alpha.Id, "demo-auth-failure");
    await Until(Commands, c => c.Any(x => x.Id == failed.Id && x.State == "Failed"));
    await Until(() => Task.FromResult(Wire.Load<LocalState>(Path.Combine(alphaDir, "status.json"))), s => s.Completed == 2);
    Check(Wire.Load<LocalState>(Path.Combine(alphaDir, "status.json")).SimulatedProfiles.Contains("demo-share"), "모의 인증 실패 후 기존 모의 상태 보존");
    var betaIdentity = SecretFile.LoadOrMigrate<Identity>(Path.Combine(betaDir, "identity.json"));
    using (var cross = Wire.Http(ready.Url, device: betaIdentity.DeviceKey))
    using (var response = await cross.PostAsJsonAsync($"api/client/commands/{success.Id}/result", new ResultRequest("fake", "Succeeded", "fake"), Wire.Json))
        Check(response.StatusCode == HttpStatusCode.NotFound, "다른 PC의 작업 결과 변조 거절");

    // A registered test identity without a polling process gives deterministic queue/security tests.
    var manualCode = await Code(); var session = Guid.NewGuid().ToString();
    using var registration = Wire.Http(ready.Url);
    var manual = await Wire.Read<EnrollReply>(await registration.PostAsJsonAsync("api/enroll", new EnrollRequest(manualCode.Code, "QA-MANUAL", "qa", session, HardwareIdentity.Capture()), Wire.Json));
    using (var approval = await admin.PostAsync($"api/admin/devices/{manual.DeviceId}/approve", null)) approval.EnsureSuccessStatusCode();
    using var device = Wire.Http(ready.Url, device: manual.DeviceKey);
    async Task Beat(bool paused) { using var response = await device.PostAsJsonAsync("api/client/heartbeat", new Heartbeat(session, paused, []), Wire.Json); response.EnsureSuccessStatusCode(); }
    await Beat(true);
    using (var paused = await admin.PostAsJsonAsync("api/admin/commands", new CreateCommand(manual.DeviceId, "demo-share"), Wire.Json))
        Check(paused.StatusCode == HttpStatusCode.Conflict, "일시 중지 PC의 새 명령 거절");
    await Beat(false);
    var expired = await Create(manual.DeviceId, "demo-share", 1); await Task.Delay(1200);
    Check((await Commands()).Single(c => c.Id == expired.Id).State == "Expired", "유효 시간이 지난 미실행 작업 만료");
    var stale = await Create(manual.DeviceId, "demo-share"); session = Guid.NewGuid().ToString(); await Beat(false);
    Check((await Commands()).Single(c => c.Id == stale.Id).State == "Rejected", "세션 변경 시 이전 작업 거절");
    var signedJob = await Create(manual.DeviceId, "demo-share");
    var lease = (await Wire.Read<LeaseReply>(await device.PostAsync("api/client/lease", null))).Lease!;
    var identity = new Identity(ready.Url, manual.DeviceId, manual.DeviceKey, manual.PublicKey);
    Check(SimulationExecutor.Valid(identity, session, lease.Envelope), "정상 서버 서명 검증");
    Check(!SimulationExecutor.Valid(identity, session, lease.Envelope with { Payload = lease.Envelope.Payload with { Mode = "ExecuteShell" } }), "변조된 명령 서명 거절");
    Check(!SimulationExecutor.Valid(identity, Guid.NewGuid().ToString(), lease.Envelope), "다른 사용자 세션 명령 거절");
    var local = new SimulationExecutor(Path.Combine(run, "manual-journal"));
    var first = local.Execute(identity, session, lease.Envelope); var second = local.Execute(identity, session, lease.Envelope);
    Check(first == second && local.Completed == 1, "중복 명령의 실행 기록 재사용");
    Check(new SimulationExecutor(Path.Combine(run, "manual-journal")).Completed == 1, "Client 작업 기록 디스크 복원");
    using (var receipt = await device.PostAsJsonAsync($"api/client/commands/{signedJob.Id}/result", new ResultRequest(lease.LeaseId, first.State, first.Detail), Wire.Json)) receipt.EnsureSuccessStatusCode();
    Check(originalDrives == string.Join("|", DriveInfo.GetDrives().Select(d => d.Name).Order()), "시험 전후 실제 드라이브 문자 목록 변경 없음");
    var outsideRejected = false;
    try { Wire.LocalUrl("http://192.168.1.1:1234/"); }
    catch (InvalidOperationException) { outsideRejected = true; }
    Check(outsideRejected, "로컬 이외의 서버 주소 거절");

    var salesProfile=await Wire.Read<Profile>(await admin.PostAsJsonAsync("api/admin/profiles",new SaveProfile("사업부 시험",@"\\lab-nas\sales","R:",Department:"사업부"),Wire.Json));
    var eduProfile=await Wire.Read<Profile>(await admin.PostAsJsonAsync("api/admin/profiles",new SaveProfile("교육부 시험",@"\\lab-nas\education","S:",Department:"교육부"),Wire.Json));
    using(var unassigned=await admin.PostAsJsonAsync("api/admin/commands",new CreateCommand(manual.DeviceId,salesProfile.Id,Operation:"ConnectApprovedSmb"),Wire.Json))Check(!unassigned.IsSuccessStatusCode,"미배정 PC의 실제 연결 명령 거절");
    using(var crossDept=await admin.PostAsJsonAsync($"api/admin/devices/{manual.DeviceId}/assignment",new SaveAssignment("사업부",@"LABNAS\sales",[eduProfile.Id],AccessRole:"서버관리자"),Wire.Json))Check(crossDept.StatusCode==HttpStatusCode.BadRequest,"Drive 배정으로 서버관리자 권한 부여 거절");
    var assignment=await Wire.Read<DeviceAssignment>(await admin.PostAsJsonAsync($"api/admin/devices/{manual.DeviceId}/assignment",new SaveAssignment("사업부",@"LABNAS\sales",[salesProfile.Id]),Wire.Json));
    var policy=await device.GetFromJsonAsync<PolicyEnvelope>("api/client/policy",Wire.Json)??throw new Exception("정책 없음");
    Check(AssignmentRules.Verify(identity,session,policy),"관리자 배정 정책의 서명·PC·세션 검증");
    Check(policy.Payload.Profiles.Select(p=>p.Id).SequenceEqual(new[]{salesProfile.Id})&&policy.Payload.ExpectedAccount==@"LABNAS\sales","해당 부서 공유와 지정 Drive 계정만 정책에 포함");
    Check(!AssignmentRules.Verify(identity,session,policy with {Payload=policy.Payload with {ExpectedAccount=@"LABNAS\education"}}),"배정 계정 변조 거절");
    Check(!AssignmentRules.Verify(identity,Guid.NewGuid().ToString(),policy),"다른 세션의 배정 정책 거절");
    using(var crossCommand=await admin.PostAsJsonAsync("api/admin/commands",new CreateCommand(manual.DeviceId,eduProfile.Id,Operation:"ConnectApprovedSmb"),Wire.Json))Check(!crossCommand.IsSuccessStatusCode,"교육부 실제 연결 명령을 사업부 PC에 발행 불가");
    using(var wrongAccount=await admin.PostAsJsonAsync($"api/admin/devices/{manual.DeviceId}/assignment",new SaveAssignment("사업부",@"LABNAS\education",[salesProfile.Id],ExpectedRevision:assignment.Revision),Wire.Json))Check(wrongAccount.StatusCode==HttpStatusCode.Conflict,"같은 부서의 Drive 계정 임의 변경 거절");
    var queued=await Wire.Read<CommandView>(await admin.PostAsJsonAsync("api/admin/commands",new CreateCommand(manual.DeviceId,salesProfile.Id,Operation:"ConnectApprovedSmb"),Wire.Json));
    assignment=await Wire.Read<DeviceAssignment>(await admin.PostAsJsonAsync($"api/admin/devices/{manual.DeviceId}/assignment",new SaveAssignment("사업부",@"LABNAS\sales",[],ExpectedRevision:assignment.Revision),Wire.Json));
    await device.PostAsync("api/client/lease",null);
    Check((await Commands()).Single(c=>c.Id==queued.Id).State=="Rejected","배정 회수 후 대기 중인 실제 연결 명령 무효화");
    var revoked=await device.GetFromJsonAsync<PolicyEnvelope>("api/client/policy",Wire.Json)??throw new Exception("정책 없음");
    Check(AssignmentRules.Verify(identity,session,revoked)&&revoked.Payload.Profiles.Length==0,"회수된 정책은 허용 공유 없이 다시 서명됨");
    using(var reused=await admin.PostAsJsonAsync($"api/admin/devices/{manual.DeviceId}/assignment",new SaveAssignment("교육부",@"LABNAS\sales",[eduProfile.Id],ExpectedRevision:assignment.Revision),Wire.Json))Check(reused.StatusCode==HttpStatusCode.Conflict,"사업부 Drive 계정을 교육부 권한 배정에 재사용 불가");
    Check(originalDrives==string.Join("|",DriveInfo.GetDrives().Select(d=>d.Name).Order()),"권한 배정 QA가 호스트 실제 매핑을 만들거나 해제하지 않음");

    using(var whitespace=await admin.PostAsJsonAsync($"api/admin/devices/{manual.DeviceId}/assignment",new SaveAssignment(" 교육부 ",@" LABNAS\sales ",[],ExpectedRevision:assignment.Revision),Wire.Json))Check(whitespace.StatusCode==HttpStatusCode.Conflict,"공유가 없는 배정과 공백으로 부서 계정 검증 우회 불가");
    using(var empty=await admin.PostAsJsonAsync($"api/admin/devices/{manual.DeviceId}/assignment",new SaveAssignment("사업부","",[],ExpectedRevision:assignment.Revision),Wire.Json))Check(empty.StatusCode==HttpStatusCode.BadRequest,"공유 회수 시에도 부서 계정 바인딩 유지");
    using(var secondAssignment=await admin.PostAsJsonAsync($"api/admin/devices/{alpha.Id}/assignment",new SaveAssignment("사업부",@"LABNAS\sales",[salesProfile.Id]),Wire.Json))secondAssignment.EnsureSuccessStatusCode();
    using(var education=await admin.PostAsJsonAsync($"api/admin/devices/{beta.Id}/assignment",new SaveAssignment("교육부",@"LABNAS\education",[eduProfile.Id]),Wire.Json))education.EnsureSuccessStatusCode();
    using(var crossRotate=await admin.PostAsJsonAsync("api/admin/department-account",new DepartmentAccountChange("사업부",@"LABNAS\sales",@"LABNAS\education"),Wire.Json))Check(crossRotate.StatusCode==HttpStatusCode.Conflict,"다른 부서의 계정으로 일괄 변경 거절");
    using(var staleRotate=await admin.PostAsJsonAsync("api/admin/department-account",new DepartmentAccountChange("사업부",@"LABNAS\obsolete",@"LABNAS\sales2"),Wire.Json))Check(staleRotate.StatusCode==HttpStatusCode.Conflict,"오래된 부서 계정 변경 요청 거절");
    using(var rotation=await admin.PostAsJsonAsync("api/admin/department-account",new DepartmentAccountChange("사업부",@"LABNAS\sales",@"LABNAS\sales2"),Wire.Json))rotation.EnsureSuccessStatusCode();
    var rotated=(await Devices()).Where(d=>d.Assignment?.Department=="사업부").ToArray();
    Check(rotated.Length==2&&rotated.All(d=>d.Assignment!.ExpectedAccount==@"LABNAS\sales2"),"부서 계정 변경이 소속 PC 전체에 적용됨");
    var rotatedPolicy=await device.GetFromJsonAsync<PolicyEnvelope>("api/client/policy",Wire.Json)??throw new Exception("정책 없음");
    Check(AssignmentRules.Verify(identity,session,rotatedPolicy)&&rotatedPolicy.Payload.ExpectedAccount==@"LABNAS\sales2","변경된 부서 계정이 새 서명 정책에 반영됨");
    // Empty policies exercise local expiry fences without reaching any Windows network API.
    using(var managed=new RealSmbEngine(Path.Combine(run,"managed-empty"))){
        managed.EnableManagedMode();
        var now=DateTimeOffset.UtcNow;
        var blank=new DepartmentPolicy("qa","qa",1,"사업부",@"LABNAS\sales",[],true,false,false,now,now.AddMilliseconds(100),1);
        await managed.ApplyPolicy(blank,CancellationToken.None);
        Check(managed.AppliedPolicyRevision==1,"로컬 승인 없이 관리자 정책 수신");
        await Task.Delay(150);await managed.MaintainPolicy(CancellationToken.None);
        var denied=await managed.Connect("empty-policy-denied",salesProfile,CancellationToken.None);
        Check(denied.State=="Failed"&&denied.Detail.StartsWith("MANAGED_PERMISSION_DENIED"),"미배정 프로필은 네이티브 연결 전에 거절");
        await managed.ApplyPolicy(blank with {Revision=0,IssuedAt=now.AddSeconds(1),ExpiresAt=now.AddSeconds(20)},CancellationToken.None);
        Check(managed.AppliedPolicyRevision==1,"낮은 개정의 관리자 정책 재생 거절");
    }
    using(var managed=new RealSmbEngine(Path.Combine(run,"managed-release"))){
        managed.EnableManagedMode();
        var now=DateTimeOffset.UtcNow;
        var assignedPolicy=new DepartmentPolicy("qa","qa",1,"사업부",@"LABNAS\sales",[salesProfile],true,false,false,now,now.AddSeconds(30),1);
        await managed.ApplyPolicy(assignedPolicy,CancellationToken.None);
        var incident=managed.LatchQuarantine("regression fixture");
        managed.Release(incident);
        await managed.ApplyPolicy(assignedPolicy with {Revision=2,IssuedAt=DateTimeOffset.UtcNow,ExpiresAt=DateTimeOffset.UtcNow.AddSeconds(30)},CancellationToken.None);
        var deferred=await managed.Connect("automatic-after-release",salesProfile,CancellationToken.None,automatic:true);
        Check(deferred.State=="Failed"&&deferred.Detail.StartsWith("RESUME_REQUIRED"),"격리 해제 뒤 다른 PC의 정책 변경으로 자동 연결 재개 불가");
        var saved=Wire.Load<RealSmbEngine.State>(Path.Combine(run,"managed-release","smb-policy.json"));
        Check(saved.Approvals[salesProfile.Id].NeedsCommand&&!saved.Approvals[salesProfile.Id].Enabled,"관리자 재개 대기 상태를 디스크에 유지");
    }
    var secretPath=Path.Combine(run,"secret-check","identity.json.protected");
    SecretFile.Save(secretPath,identity);
    Check(!System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(secretPath)).Contains(identity.DeviceKey),"장치 키가 평문으로 저장되지 않음");
    Check(SecretFile.Load<Identity>(secretPath)==identity,"현재 Windows 사용자 DPAPI 복원");
    var corrupt=File.ReadAllBytes(secretPath);corrupt[^1]^=1;File.WriteAllBytes(secretPath,corrupt);
    var corruptDenied=false;try{SecretFile.Load<Identity>(secretPath);}catch(System.ComponentModel.Win32Exception){corruptDenied=true;}
    Check(corruptDenied,"변조된 비밀값 복원 거절 및 자동 재등록 없음");
    var legacy=Path.Combine(run,"legacy-check","identity.json");Wire.Save(legacy,identity);
    Check(SecretFile.LoadOrMigrate<Identity>(legacy)==identity&&!File.Exists(legacy)&&File.Exists(legacy+".protected"),"평문 장치 등록을 검증 후 DPAPI로 이관");
    Wire.Save(legacy,identity);
    Check(SecretFile.LoadOrMigrate<Identity>(legacy)==identity&&!File.Exists(legacy),"중단된 평문 이관은 보호본 검증 후 정리");
    Check(Wire.LocalUrl("https://nas.example.test:48653/").Scheme=="https","원격 HTTPS 서버 주소 허용");
    var remoteAdminDenied=false;try{using var invalidAdmin=Wire.Http("https://nas.example.test/",admin:"test");}catch(InvalidOperationException){remoteAdminDenied=true;}
    Check(remoteAdminDenied,"원격 관리 키 전송 거절");
    var atomicPath=Path.Combine(run,"atomic-replace.json");Wire.Save(atomicPath,new{value="original"});
    var held=new FileStream(atomicPath,FileMode.Open,FileAccess.Read,FileShare.Read);
    var retryWrite=Task.Run(()=>Wire.Save(atomicPath,new{value="replaced"}));
    await Task.Delay(180);held.Dispose();await retryWrite;
    Check(Wire.Load<System.Text.Json.Nodes.JsonObject>(atomicPath)["value"]!.GetValue<string>()=="replaced","일시적인 Windows 파일 교체 잠금 해제 후 제한된 재시도로 저장 성공");
    var permanentDenied=false;
    using(var locked=new FileStream(atomicPath,FileMode.Open,FileAccess.Read,FileShare.Read)){
        try{await Task.Run(()=>Wire.Save(atomicPath,new{value="must-not-overwrite"}));}catch(Exception e)when(e is IOException or UnauthorizedAccessException){permanentDenied=true;}
    }
    Check(permanentDenied&&Wire.Load<System.Text.Json.Nodes.JsonObject>(atomicPath)["value"]!.GetValue<string>()=="replaced"&&Directory.GetFiles(run,"atomic-replace.json.*.tmp").Length==0,"영구 파일 잠금은 실패로 보고하고 기존 값·임시 파일 정리 보장");
    var channelIdentity = await BetaChannelTests.Run(admin, ready.Url, run, Check);
    await LegacyMigrationTests.Run(run,Check); await HardwareTests.Run(admin, ready.Url, run, Check); await WorkflowTests.Run(admin,ready.Url,run,Check);
    var interrupted=await Create(manual.DeviceId,"demo-share",600);
    var interruptedLease=(await Wire.Read<LeaseReply>(await device.PostAsync("api/client/lease",null))).Lease;
    Check(interruptedLease?.Envelope.Payload.Id==interrupted.Id,"재시작 시험용 실행 임대 생성");
    var restartQueued=await Create(manual.DeviceId,"demo-share",600);
    server.Kill();server.WaitForExit(5000);File.Delete(readyFile);
    // Simulate one legacy device record lacking the new optional channel fields.
    var persistedPath=Path.Combine(run,".server-secrets","profiles.json.protected");
    var legacyState=SecretFile.Load<System.Text.Json.Nodes.JsonObject>(persistedPath);
    var legacyDevice=legacyState["devices"]!.AsArray().Single(d=>d!["id"]!.GetValue<string>()==alpha.Id)!.AsObject();
    legacyDevice.Remove("betaAdministrator");legacyDevice.Remove("betaChannel");
    SecretFile.Save(persistedPath,legacyState);
    var restarted=Start(Exe("ManagementServer"),"--run-dir",run);
    await Until(()=>Task.FromResult(File.Exists(readyFile)),v=>v||restarted.HasExited);
    Check(!restarted.HasExited,"기존 상태에서 관리 서버 재시작");
    var restoredReady=Wire.Load<ServerReady>(readyFile);
    Check(restoredReady.AdminKey==ready.AdminKey,"재시작 후 관리자 키 유지");
    using var restoredAdmin=Wire.Http(restoredReady.Url,admin:restoredReady.AdminKey);
    var migratedAccess=await restoredAdmin.GetFromJsonAsync<BetaAccess>($"api/admin/devices/{alpha.Id}/beta",Wire.Json);
    Check(migratedAccess is {Administrator:false,Selected:BetaChannels.Open}&&!migratedAccess.Allowed.Contains(BetaChannels.Closed),"구 장치 상태 이관 시 관리자 권한 자동 부여 없이 오픈베타 기본값 적용");
    using (var restoredChannel = Wire.Http(restoredReady.Url, device: channelIdentity.DeviceKey))
    {
        var access = await restoredChannel.GetFromJsonAsync<BetaAccess>("api/client/beta/channels", Wire.Json);
        Check(access is { Administrator: true, Selected: BetaChannels.Closed } && access.Allowed.Contains(BetaChannels.Closed), "서버 재시작 후 관리자 자격·선택 채널 복원");
    }
    var restoredDevices=(await restoredAdmin.GetFromJsonAsync<DeviceView[]>("api/admin/devices",Wire.Json))!;
    Check(restoredDevices.Any(d=>d.Id==manual.DeviceId&&d.Approved)&&restoredDevices.All(d=>!d.Online),"장치 승인 복원 및 재접속 전 오프라인 표시");
    using var restoredDevice=Wire.Http(restoredReady.Url,device:manual.DeviceKey);
    using(var beat=await restoredDevice.PostAsJsonAsync("api/client/heartbeat",new Heartbeat(session,false,[]),Wire.Json))Check(beat.IsSuccessStatusCode,"서버 재시작 후 기존 장치 키 인증");
    var restoredPolicy=await restoredDevice.GetFromJsonAsync<PolicyEnvelope>("api/client/policy",Wire.Json);
    Check(restoredPolicy is not null&&AssignmentRules.Verify(identity,session,restoredPolicy),"서버 재시작 후 기존 공개키로 정책 서명 검증");
    var restoredCommands=(await restoredAdmin.GetFromJsonAsync<CommandView[]>("api/admin/commands",Wire.Json))!;
    Check(restoredCommands.Single(c=>c.Id==interrupted.Id).State=="Unknown","중단된 임대는 결과 미확인으로 복원해 중복 실행 금지");
    Check(restoredCommands.Single(c=>c.Id==restartQueued.Id).State=="Queued","미실행 명령은 재시작 후 대기 상태 유지");
    Check(restoredCommands.Single(c=>c.Id==success.Id).State=="Succeeded","완료한 명령의 결과 기록 복원");
    Console.WriteLine($"TOTAL PASS {passed.Count}");
}
catch (Exception e) { failure = e.ToString(); Console.Error.WriteLine(failure); Environment.ExitCode = 1; }
finally
{
    foreach (var p in processes.AsEnumerable().Reverse())
    {
        try { if (!p.HasExited) { p.Kill(true); p.WaitForExit(5000); } } catch (InvalidOperationException) { }
        p.Dispose();
    }
    Wire.Save(Path.Combine(run, "report.json"), new { mode = "SimulationOnly", hardware = HardwareIdentity.Capture(), passed, failure, finishedAt = DateTimeOffset.UtcNow });
    File.WriteAllText(Path.Combine(root, ".local", "last-qa.txt"), run);
    Console.WriteLine("REPORT " + Path.Combine(run, "report.json"));
}
