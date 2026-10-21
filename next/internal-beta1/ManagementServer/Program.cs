using System.Net;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using SmbManager.Local;

var runDir = Path.GetFullPath(Wire.Arg(args, "--run-dir"));
Directory.CreateDirectory(runDir);
var isolatedLab = args.Contains("--isolated-vm-lab");
var tlsFile = Wire.Arg(args, "--https-config", "");
var tls = tlsFile.Length == 0 ? null : TlsSettings.Load(tlsFile);
var localPort = int.Parse(Wire.Arg(args, "--port", "0"));
if (localPort != 0 && (localPort < 1024 || localPort > 65535)) throw new ArgumentOutOfRangeException("--port");
if (isolatedLab && tls is not null) throw new ArgumentException("VM 개발 HTTP와 운영 HTTPS 설정은 함께 사용할 수 없습니다.");
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(o => { o.Listen(IPAddress.Loopback, localPort); if (isolatedLab) o.Listen(IPAddress.Parse("10.77.30.1"), 48652); if(tls is not null) o.Listen(IPAddress.Parse(tls.BindAddress),tls.Port, endpoint=>endpoint.UseHttps(tls.Certificate)); o.Limits.MaxRequestBodySize = 32 * 1024; });
builder.Logging.ClearProviders();
var app = builder.Build();
var profilesPath = Path.GetFullPath(Wire.Arg(args, "--profiles-file", Path.Combine(runDir, "profiles.json")));
Directory.CreateDirectory(Path.GetDirectoryName(profilesPath)!);
using var instanceLock = new FileStream(profilesPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
var store = new Store(profilesPath);
app.Use(async (ctx, next) =>
{
    if (!store.StorageHealthy) { ctx.Response.StatusCode = 503; return; }
    if (ctx.Connection.RemoteIpAddress is not { } ip || !(IPAddress.IsLoopback(ip) || ctx.Request.IsHttps || (isolatedLab && ip.Equals(IPAddress.Parse("10.77.30.2"))))) { ctx.Response.StatusCode = 403; return; }
    try { await next(); }
    catch (Exception error) when (args.Contains("--qa-diagnostics") && error is not OperationCanceledException)
    {
        // Test-only diagnostics deliberately exclude request headers, bodies and exception messages.
        Wire.Save(Path.Combine(runDir,"qa-error-"+Guid.NewGuid().ToString("N")+".json"),new {type=error.GetType().FullName,error.HResult,error.StackTrace});
        throw;
    }
});
app.MapGet("/health", () => new { mode = "InternalBeta", version = "2.0.0-beta.1", buildRevision = 9, isolatedVmLab = isolatedLab, networkGuard = false });
app.MapPost("/api/enroll", (EnrollRequest request) => store.Enroll(request));
app.MapGet("/api/registration/departments", () => store.RegistrationDepartments());
app.MapPost("/api/registration/request", (HttpContext ctx, RegistrationRequest request) => store.RequestRegistration(ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown", request));
var admin = app.MapGroup("/api/admin").AddEndpointFilter(async (context, next) =>
    context.HttpContext.Connection.RemoteIpAddress is { } adminIp && IPAddress.IsLoopback(adminIp) && Wire.EqualSecret(context.HttpContext.Request.Headers["X-Admin-Key"].FirstOrDefault(), store.AdminKey)
        ? await next(context) : Results.Unauthorized());
admin.MapPost("/enrollment-code", () => store.Code());
admin.MapGet("/devices", () => store.Devices());
admin.MapGet("/devices/{id}/beta", (string id) => store.AdminBetaAccess(id));
admin.MapPost("/devices/{id}/beta-administrator", (string id, BetaAdministrator request) => store.GrantBetaAdministrator(id, request));
admin.MapPost("/devices/{id}/approve", (string id) => store.Approve(id));
admin.MapPost("/devices/{id}/assignment", (string id, SaveAssignment request) => store.SaveAssignment(id, request));
admin.MapGet("/departments", () => store.Departments());
admin.MapGet("/department-templates", () => store.DepartmentTemplates());
admin.MapPost("/department-templates", (SaveDepartmentTemplate request) => store.SaveDepartmentTemplate(request));
admin.MapPost("/department-template-preview", (SaveDepartmentTemplate request) => store.TemplatePreview(request));
admin.MapPost("/share-credential-preview", (SaveShareCredential request) => store.CredentialPreview(request));
admin.MapGet("/share-credentials", () => store.CredentialSummaries());
admin.MapPost("/devices/{id}/correction", (string id, ReviewCorrection request) => store.ReviewRegistration(id, request));
admin.MapPost("/devices/{id}/archive", (string id) => store.ArchiveDevice(id));
admin.MapPost("/share-credential", (SaveShareCredential request) => store.SaveCredential(request));
admin.MapPost("/devices/{id}/reconnect", (string id) => store.RequestReconnect(id));
admin.MapPost("/devices/connections", (BulkConnectionRequest request) => store.BulkConnections(request));
admin.MapPost("/department-account", (DepartmentAccountChange request) => store.ChangeDepartmentAccount(request));
admin.MapGet("/profiles", () => store.GetProfiles());
admin.MapPost("/profiles", (SaveProfile r) => store.SaveProfile(null, r));
admin.MapGet("/audit", () => store.Audit());
admin.MapGet("/profiles/{id}/impact", (string id) => store.ProfileImpact(id));
admin.MapPost("/profiles/{id}/status", (string id, ProfileStatusChange request) => store.ProfileStatus(id,request));
admin.MapPut("/profiles/{id}", (string id, SaveProfile r) => store.SaveProfile(id, r));
admin.MapPost("/commands", (CreateCommand request) => store.Create(request));
admin.MapGet("/commands", () => store.Commands());
var client = app.MapGroup("/api/client").AddEndpointFilter(async (context, next) =>
{
    var header = context.HttpContext.Request.Headers.Authorization.ToString();
    var id = header.StartsWith("Bearer ", StringComparison.Ordinal) ? store.Auth(header[7..], context.HttpContext.Request.Headers["X-Device-Hardware"].FirstOrDefault()) : null;
    if (id is null) return Results.Unauthorized();
    context.HttpContext.Items["device"] = id;
    return await next(context);
});
client.MapGet("/registration", (HttpContext ctx) => store.RegistrationView((string)ctx.Items["device"]!));
client.MapPost("/registration-correction", (HttpContext ctx, CorrectionRequest request) => store.RequestCorrection((string)ctx.Items["device"]!, request));
client.MapPost("/heartbeat", (HttpContext ctx, Heartbeat request) => store.Heartbeat((string)ctx.Items["device"]!, request));
client.MapPost("/lease", (HttpContext ctx) => store.Lease((string)ctx.Items["device"]!));
client.MapPost("/urgent-lease", (HttpContext ctx) => store.Lease((string)ctx.Items["device"]!, true));
client.MapGet("/policy", (HttpContext ctx) => store.Policy((string)ctx.Items["device"]!));
client.MapGet("/credentials/{profileId}", (HttpContext ctx, string profileId) => { ctx.Response.Headers.CacheControl="no-store"; return store.Credential((string)ctx.Items["device"]!, profileId); });
client.MapGet("/profiles", (HttpContext ctx) => store.ClientProfiles((string)ctx.Items["device"]!));
client.MapGet("/beta/channels", (HttpContext ctx) => store.BetaChannelsFor((string)ctx.Items["device"]!));
client.MapPost("/beta/selection", (HttpContext ctx, BetaSelection request) => store.SelectBeta((string)ctx.Items["device"]!, request));
client.MapGet("/beta/releases/{channel}", (HttpContext ctx, string channel) => store.BetaArtifact((string)ctx.Items["device"]!, channel, false));
client.MapGet("/beta/download/{channel}", (HttpContext ctx, string channel) => store.BetaArtifact((string)ctx.Items["device"]!, channel, true));
client.MapGet("/events", (HttpContext ctx) => TypedResults.ServerSentEvents(store.Events((string)ctx.Items["device"]!, ctx.RequestAborted)));
client.MapPost("/commands/{id}/result", (HttpContext ctx, string id, ResultRequest result) =>
    store.Result((string)ctx.Items["device"]!, id, result));
SecretFile.PrivateDirectory(runDir);
await app.StartAsync();
var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single(address => new Uri(address).Host == "127.0.0.1").TrimEnd('/') + "/";
Wire.Save(Path.Combine(runDir, "ready.json"), new ServerReady(url, store.AdminKey, Environment.ProcessId, isolatedLab ? "IsolatedVmDevelopmentLab" : "LoopbackSimulationOnly"));
Console.WriteLine("SMB Manager development server ready; administrator API is loopback only.");
await app.WaitForShutdownAsync();

sealed partial class Store
{

    private readonly Dictionary<string, DeviceAssignment> assignments = [];
    private int policyRevision;
    private string assignmentFile = "";
    private void LoadAssignments(string file) {
        assignmentFile = file + ".assignments.json";
        if(File.Exists(assignmentFile)) { var saved = Wire.Load<AssignmentStore>(assignmentFile); policyRevision = saved.Revision; if(saved.Profiles is not null)profiles=saved.Profiles; foreach(var entry in saved.Entries) assignments[entry.Key] = entry.Value; }
    }
    private void CommitPolicy(Profile[] nextProfiles, Dictionary<string,DeviceAssignment> nextAssignments) {
        var revision=checked(policyRevision+1);
        try{Wire.Save(assignmentFile,new AssignmentStore {Revision=revision,Profiles=nextProfiles,Entries=nextAssignments});}catch{StorageHealthy=false;throw;}
        profiles=nextProfiles;assignments.Clear();foreach(var entry in nextAssignments)assignments[entry.Key]=entry.Value;
        policyRevision=revision;
        foreach(var d in devices.Values)d.Signal.Writer.TryWrite(1);
    }

    public Dictionary<string,string> Departments(){lock(gate)return assignments.Values.GroupBy(a=>a.Department).ToDictionary(g=>g.Key,g=>g.First().ExpectedAccount);}
    public IResult ChangeDepartmentAccount(DepartmentAccountChange request){
        lock(gate){
            var department=(request.Department??"").Trim();var expected=(request.ExpectedAccount??"").Trim();var account=(request.NewAccount??"").Trim();
            if(!AssignmentRules.ValidDepartment(department)||!AssignmentRules.ValidAccount(account))return Results.BadRequest();
            var affected=assignments.Where(x=>x.Value.Department==department).ToArray();
            if(affected.Length==0)return Results.NotFound();
            if(affected.Any(x=>!string.Equals(x.Value.ExpectedAccount,expected,StringComparison.OrdinalIgnoreCase)))return Results.Conflict(new{error="DEPARTMENT_ACCOUNT_CHANGED"});
            if(assignments.Values.Any(a=>a.Department!=department&&string.Equals(a.ExpectedAccount,account,StringComparison.OrdinalIgnoreCase)))return Results.Conflict(new{error="ACCOUNT_BELONGS_TO_OTHER_DEPARTMENT"});
            if(affected.Any(entry=>ConflictingServerAccounts(department,account,profiles.Where(p=>ProfileRules.Available(p)&&(entry.Value.AccessRole=="임원"||entry.Value.ProfileIds.Contains(p.Id))))))return Results.Conflict(new{error="SERVER_ACCOUNT_BELONGS_TO_OTHER_DEPARTMENT"});
            var candidate=new Dictionary<string,DeviceAssignment>(assignments);
            foreach(var entry in affected)candidate[entry.Key]=entry.Value with {ExpectedAccount=account,Revision=policyRevision+1,ResumeStopped=false};
            CommitPolicy(profiles,candidate);
            return Results.Ok(new{department,account,affectedDevices=affected.Length});
        }
    }

    private bool Allows(string deviceId, Profile profile) => !devices[deviceId].ConnectionsHeld && assignments.TryGetValue(deviceId, out var a) && (a.AccessRole=="임원" || a.ProfileIds.Contains(profile.Id))
        && AssignmentRules.ValidAccount(a.ExpectedAccount) && ProfileRules.Available(profile);
    public IResult SaveAssignment(string id, SaveAssignment request) {
        lock(gate) {
            request=request with {Department=(request.Department??"").Trim(),ExpectedAccount=(request.ExpectedAccount??"").Trim()};
            if(!devices.TryGetValue(id,out var d)) return Results.NotFound();
            if(!d.Approved) return Results.Conflict(new { error = "DEVICE_NOT_APPROVED" });
            if(!AssignmentRules.ValidDepartment(request.Department) || request.Department == "공용" || request.ProfileIds is null || request.ProfileIds.Length > 26
                || request.AccessRole is not ("직원" or "임원") || request.ProfileIds.Distinct().Count() != request.ProfileIds.Length || !AssignmentRules.ValidAccount(request.ExpectedAccount))
                return Results.BadRequest(new { error = "INVALID_ASSIGNMENT" });
            if(request.FollowDepartment){
                if(!templates.TryGetValue(request.Department,out var template))return Results.Conflict(new{error="TEMPLATE_REQUIRED"});
                request=request with{ExpectedAccount=template.ExpectedAccount,ProfileIds=template.ProfileIds,AccessRole=template.AccessRole};
            }
            if(request.AccessRole=="임원")request=request with{ProfileIds=profiles.Where(p=>ProfileRules.Available(p)).Select(p=>p.Id).ToArray()}; var selected = request.ProfileIds.Select(key=>profiles.FirstOrDefault(p=>p.Id==key)).ToArray();
            if(selected.Any(p=>p is null || !ProfileRules.Available(p))
                || selected.Select(p=>p!.Drive).Distinct().Count()!=selected.Length) return Results.BadRequest(new { error = "DEPARTMENT_OR_DRIVE_MISMATCH" });
            if(assignments.Values.Any(a=>(a.Department==request.Department && !string.Equals(a.ExpectedAccount,request.ExpectedAccount,StringComparison.OrdinalIgnoreCase)) || (a.Department!=request.Department && string.Equals(a.ExpectedAccount,request.ExpectedAccount,StringComparison.OrdinalIgnoreCase)))) return Results.Conflict(new {error="DEPARTMENT_ACCOUNT_BINDING_MISMATCH"});
            if(ConflictingServerAccounts(request.Department,request.ExpectedAccount,selected.Cast<Profile>()))return Results.Conflict(new{error="SERVER_ACCOUNT_BELONGS_TO_OTHER_DEPARTMENT"});
            var old = assignments.GetValueOrDefault(id);
            if((old?.Revision ?? 0) != request.ExpectedRevision) return Results.Conflict(new { error = "ASSIGNMENT_CHANGED" });
            var next = new DeviceAssignment(request.Department.Trim(), (request.ExpectedAccount??"").Trim(), request.ProfileIds, request.Reconnect, request.ForceDisconnect, policyRevision+1, request.ResumeStopped, request.AccessRole, request.FollowDepartment);
            var candidate=new Dictionary<string,DeviceAssignment>(assignments){[id]=next}; CommitPolicy(profiles,candidate);RecordAudit("PC 배정 변경",id,request.Department+" / "+string.Join(" / ",request.ProfileIds));Persist();
            return Results.Ok(next);
        }
    }
    public IResult Policy(string id) {
        lock(gate) {
            var d = devices[id]; if(!d.Approved) return Results.StatusCode(403);
            var a = assignments.GetValueOrDefault(id);
            var allowed = profiles.Where(p=>Allows(id,p)).ToArray(); if(allowed.Select(p=>p.Drive).Distinct().Count()!=allowed.Length)return Results.Conflict(new{error="DRIVE_CONFLICT"});
            var policy = new DepartmentPolicy(id,d.SessionId,policyRevision,a?.Department ?? "",a?.ExpectedAccount ?? "",allowed,a?.Reconnect ?? false,!d.ConnectionsHeld && (a?.ForceDisconnect ?? false),!d.ConnectionsHeld && (a?.ResumeStopped ?? false),Now,Now.AddSeconds(30),a?.Revision ?? 0, allowed.Select(p=>new ProfileAccount(p.Id,GetAccount(a!,p.Id))).ToArray(), a?.AccessRole ?? "직원",d.ConnectionsHeld);
            return Results.Ok(new PolicyEnvelope(policy,AssignmentRules.Sign(signing,policy)));
        }
    }
    private sealed class AssignmentStore { public int Revision {get;set;} public Profile[]? Profiles {get;set;} public Dictionary<string,DeviceAssignment> Entries {get;set;} = []; }

    private readonly string profilesFile;
    private readonly object gate = new();
    private readonly RSA signing = RSA.Create(2048);
    private readonly Dictionary<string, EnrollmentCode> codes = [];
    private readonly Dictionary<string, Device> devices = [];
    private readonly Dictionary<string, Work> commands = [];
    public string AdminKey { get; private set; } = Wire.Secret();
    private Profile[] profiles = [new("demo-share", "공유폴더 연결 성공 모의 시험", "SimulateConnect"),
        new("demo-auth-failure", "인증 실패·상태 보존 모의 시험", "SimulateAuthFailure")];
    public Store(string file) { profilesFile = file; if (File.Exists(file)) profiles = Wire.Load<Profile[]>(file); LoadAssignments(file); LoadPersistentState(); LoadRegistrationSettings(); EnsureLegacyCatalog(); }
    public Profile[] GetProfiles() { lock (gate) return profiles.ToArray(); }
    public IResult ClientProfiles(string id) { lock (gate) return devices[id].Approved ? Results.Ok(profiles.Where(p => Allows(id,p)).ToArray()) : Results.StatusCode(403); }
    public IResult SaveProfile(string? id, SaveProfile r)
    {
        lock (gate)
        {
            var drive = (r.Drive ?? "").Trim().ToUpperInvariant(); var unc = (r.Unc ?? "").Trim();
            if (!ProfileRules.Valid(r.Name, unc, drive) || (!string.IsNullOrEmpty(r.Department) && !AssignmentRules.ValidDepartment(r.Department))) return Results.BadRequest(new { error = "INVALID_PROFILE" });
            var old = profiles.FirstOrDefault(p => p.Id == id);
            if (id is not null && old is null) return Results.NotFound();
            if (old is not null && (old.Id.StartsWith("demo-") || old.Revision != r.ExpectedRevision)) return Results.Conflict();
            if (profiles.Any(p => p.Id != id && string.Equals(p.Unc, unc, StringComparison.OrdinalIgnoreCase))) return Results.Conflict(new { error = "DUPLICATE_UNC" });
            var profile = new Profile(id ?? Guid.NewGuid().ToString(), r.Name.Trim(), old?.Mode??"SimulateConnect", (old?.Revision ?? 0) + 1, unc, drive, (r.Department??"").Trim());
            if(assignments.Values.Any(a=>(a.AccessRole=="임원" || a.ProfileIds.Contains(profile.Id)) && profiles.Any(p=>p.Id!=profile.Id && a.ProfileIds.Contains(p.Id) && p.Drive==profile.Drive))) return Results.Conflict(new {error="ASSIGNED_DRIVE_CONFLICT"});
            var next = profiles.Where(p => p.Id != id).Append(profile).ToArray();
            if(assignments.Values.Any(a=>a.AccessRole=="임원") && next.Where(p=>ProfileRules.Available(p)).GroupBy(p=>p.Drive).Any(g=>g.Count()>1))return Results.Conflict(new{error="EXECUTIVE_DRIVE_CONFLICT"});
            if(old is not null&&ProfileRules.Valid(old.Name,old.Unc,old.Drive)&&!string.Equals(old.Unc.Split('\\')[2],unc.Split('\\')[2],StringComparison.OrdinalIgnoreCase)){
                foreach(var key in credentials.Keys.Where(k=>k.EndsWith("\n"+old.Id,StringComparison.Ordinal)).ToArray())credentials.Remove(key);
                SaveSettings();
            }
            CommitPolicy(next,new Dictionary<string,DeviceAssignment>(assignments));RecordAudit(old is null?"공유 등록":"공유 변경",profile.Id,profile.Name);Persist();
            try{Wire.Save(profilesFile,next);}catch(IOException){}catch(UnauthorizedAccessException){}
            return Results.Ok(profile);
        }
    }
    public async IAsyncEnumerable<string> Events(string id, [EnumeratorCancellation] CancellationToken ct)
    {
        Channel<int> signal;
        lock (gate) signal = devices[id].Signal;
        yield return "connected";
        while (!ct.IsCancellationRequested)
        {
            var message = "keepalive";
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            try { await signal.Reader.ReadAsync(timeout.Token); message = "commands-available"; }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            if (ct.IsCancellationRequested) yield break;
            yield return message;
        }
    }
    private static DateTimeOffset Now => DateTimeOffset.UtcNow;
    public IResult Code()
    {
        lock (gate) { foreach(var old in codes.Where(x=>x.Value.ExpiresAt<=Now).Select(x=>x.Key).ToArray())codes.Remove(old); if(codes.Count>=1000)return Results.StatusCode(429); var value = new EnrollmentCode(Wire.Secret(), Now.AddMinutes(5)); codes[value.Code] = value; return Results.Ok(value); }
    }
    public IResult Enroll(EnrollRequest r)
    {
        lock (gate)
        {
            if (!codes.TryGetValue(r.Code, out var code) || code.ExpiresAt <= Now) return Results.BadRequest(new { error = "INVALID_ENROLLMENT_CODE" });
            if (devices.Count >= 5000) return Results.StatusCode(429);
            if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Length > 80 || string.IsNullOrWhiteSpace(r.User) || r.User.Length > 160
                || !Guid.TryParse(r.SessionId, out _) || !HardwareIdentity.Valid(r.Hardware)) return Results.BadRequest();
            codes.Remove(r.Code);
            var id = Guid.NewGuid().ToString(); var key = Wire.Secret();
            devices[id] = new Device(id, r.Name, r.User, key, r.SessionId) { Hardware = r.Hardware, ComputerName = ValidComputerName(r.ComputerName) ? r.ComputerName : "" };
            Persist();
            return Results.Ok(new EnrollReply(id, key, signing.ExportSubjectPublicKeyInfoPem()));
        }
    }
    public string? Auth(string key, string? hardwareHeader)
    {
        if (hardwareHeader is null || hardwareHeader.Length > 4096) return null;
        HardwareIdentity? hardware;
        try { hardware = System.Text.Json.JsonSerializer.Deserialize<HardwareIdentity>(Convert.FromBase64String(hardwareHeader), Wire.Json); }
        catch (Exception e) when (e is FormatException or System.Text.Json.JsonException) { return null; }
        lock (gate) {
            var device = devices.Values.FirstOrDefault(d => Wire.EqualSecret(key, d.Key) && HardwareIdentity.Same(d.Hardware, hardware));
            if (device is not null) device.Hardware = hardware;
            return device?.Id;
        }
    }
    public DeviceView[] Devices()
    {
        lock (gate) return devices.Values.Select(d => new DeviceView(d.Id, d.Name, d.User, d.SessionId, d.Approved, d.Paused,
            d.LastSeen, !d.Archived && Now - d.LastSeen < TimeSpan.FromSeconds(30), d.Drives, d.PushConnected, d.Quarantined, d.IncidentId, d.Connections, d.AppliedPolicyRevision, assignments.GetValueOrDefault(d.Id), d.Hardware, d.RequestedDepartment, d.ComputerName, d.RequestedAt, d.Correction, d.Archived, d.ConnectionsHeld)).ToArray();
    }
    public IResult Approve(string id)
    {
        lock (gate) { if (!devices.TryGetValue(id, out var d)) return Results.NotFound(); if(!HardwareIdentity.Valid(d.Hardware))return Results.Conflict(new {error="HARDWARE_REENROLL_REQUIRED"}); if(!assignments.ContainsKey(id)&&templates.TryGetValue(d.RequestedDepartment,out var t)){
                var ids=t.AccessRole=="임원"?profiles.Where(p=>ProfileRules.Available(p)).Select(p=>p.Id).ToArray():t.ProfileIds;
                var selected=profiles.Where(p=>ids.Contains(p.Id)).ToArray();
                if(selected.Length!=ids.Length||selected.Select(p=>p.Drive).Distinct().Count()!=selected.Length)return Results.Conflict(new{error="DEPARTMENT_TEMPLATE_NEEDS_REVIEW"});
                var a=new DeviceAssignment(t.Department,t.ExpectedAccount,ids,true,false,policyRevision+1,false,t.AccessRole,true);
                CommitPolicy(profiles,new(assignments){[id]=a});
            }
            d.Archived = false; d.Approved = true; RecordAudit("등록 승인",d.Id,d.Name); Persist(); d.Signal.Writer.TryWrite(1); return Results.Ok(new { approved = true }); }
    }
    private string ReportAssignment(string id,ConnectionReport connection){
        var a=assignments.GetValueOrDefault(id);
        var p=profiles.FirstOrDefault(p=>p.Drive==connection.Drive&&string.Equals(p.Unc.TrimEnd('\\'),connection.Unc.TrimEnd('\\'),StringComparison.OrdinalIgnoreCase));
        if(a is null||p is null||!Allows(id,p))return "미배정/부서 불일치 · 외부 접근 회수 미확인";
        if(connection.ConnectionState=="AuthenticationRequired")return "Drive 계정·암호 확인 필요 · 자동 인증 중지";
        if(connection.ConnectionState=="ReconciliationRequired")return "이전 연결 작업 확인 필요 · 재연결 보류";
        if(connection.ConnectionState is "Disconnected" or "Unavailable" or "Conflict") return "연결 끊김/접근 불가 (클라이언트 보고)"; return string.Equals(GetAccount(a,p.Id),connection.Account,StringComparison.OrdinalIgnoreCase)?"배정 일치 (클라이언트 보고) · 공유폴더 ACL 미검증":"계정 불일치/확인 불가";
    }
    public IResult Heartbeat(string id, Heartbeat r)
    {
        lock (gate)
        {
            if (!ValidComputerName(r.ComputerName) || !Guid.TryParse(r.SessionId, out _) || r.VisibleDriveLetters is null || r.VisibleDriveLetters.Length > 26 || r.Connections is {Length: > 26} || (r.Connections?.Any(c=>c is null || c.Unc is null || c.Unc.Length > 512 || c.Account is null || c.Account.Length > 160 || c.Drive is null || c.Drive.Length > 3) ?? false)) return Results.BadRequest();
            var d = devices[id];
            if (d.SessionId != r.SessionId)
                foreach (var w in commands.Values.Where(w => w.Payload.DeviceId == id && w.State is "Queued" or "Leased" or "Running"))
                { w.State = "Rejected"; w.Detail = "SESSION_CHANGED"; }
            var sessionChanged = d.SessionId != r.SessionId;var computerChanged=r.ComputerName.Length>0&&d.ComputerName!=r.ComputerName;if(computerChanged)d.ComputerName=r.ComputerName;
            d.SessionId = r.SessionId; d.Paused = r.Paused; d.Drives = r.VisibleDriveLetters; d.LastSeen = Now;
            d.Connections = (r.Connections ?? []).Select(c=>c with {EffectiveAccess="미검증: 공유폴더 권한 확인 필요",AssignmentState=ReportAssignment(id,c)}).ToArray(); d.AppliedPolicyRevision = r.AppliedPolicyRevision;
            d.PushConnected = r.PushConnected; d.Quarantined = r.Quarantined; d.IncidentId = r.IncidentId;
            if (sessionChanged||computerChanged) Persist();
            return Results.Ok(new { approved = d.Approved });
        }
    }
    public IResult Create(CreateCommand r)
    {
        lock (gate)
        {
            if (!devices.TryGetValue(r.DeviceId, out var d)) return Results.NotFound();
            if (commands.Count >= 10000) return Results.StatusCode(429);
            if (!d.Approved || (d.Paused && r.Operation is "ConnectProfile" or "ConnectApprovedSmb")) return Results.Conflict(new { error = "DEVICE_NOT_READY" });
            if (r.Operation is not ("ConnectProfile" or "ConnectApprovedSmb" or "Notify" or "Quarantine" or "ReleaseQuarantine" or "ReconnectAssigned") || r.Text is null || r.Text.Length > 1000 || r.TtlSeconds < 1 || r.TtlSeconds > 600) return Results.BadRequest();
            var profile = profiles.FirstOrDefault(p => p.Id == r.ProfileId);
            if (r.Operation is "ConnectProfile" or "ConnectApprovedSmb" && profile is null) return Results.BadRequest();
            if (r.Operation == "ConnectApprovedSmb" && (profile is null || !Allows(d.Id,profile))) return Results.BadRequest();
            var p = new CommandPayload(Guid.NewGuid().ToString(), d.Id, d.SessionId, profile?.Id ?? "", profile?.Revision ?? 0,
                profile?.Mode ?? "AlphaControl", Now, Now.AddSeconds(r.TtlSeconds), r.Operation, r.Text, r.Operation is "ConnectProfile" or "ConnectApprovedSmb" ? profile : null);
            var w = new Work(p, Signature.Sign(signing, p), d.Name); commands[p.Id] = w;
            Persist();
            d.Signal.Writer.TryWrite(1);
            return Results.Ok(View(w));
        }
    }
    private void Expire()
    {
        foreach (var w in commands.Values.Where(w => w.State is "Queued" or "Leased" or "Running"))
        {
            if (w.State == "Running")
            {
                if (w.LeaseExpires <= Now) { w.State = "Unknown"; w.Detail = "실행 중 응답 중단 — 실제 결과 확인 필요, 자동 재실행 안 함"; }
            }
            else if (w.Payload.ExpiresAt <= Now) { w.State = "Expired"; w.Detail = "실행 시작 전 만료됨"; }
        }
    }
    public CommandView[] Commands()
    {
        lock (gate) { Expire(); return commands.Values.OrderByDescending(w => w.Payload.IssuedAt).Select(View).ToArray(); }
    }
    public IResult Lease(string id, bool urgentOnly = false)
    {
        lock (gate)
        {
            Expire(); var d = devices[id];
            foreach(var pending in commands.Values.Where(w=>w.Payload.DeviceId==id && w.Payload.Operation=="ConnectApprovedSmb" && w.State is "Queued" or "Leased")){var live=profiles.FirstOrDefault(p=>p.Id==pending.Payload.ProfileId); if(live is null || live.Revision!=pending.Payload.ProfileRevision || !Allows(id,live)){pending.State="Rejected";pending.Detail="ASSIGNMENT_REVOKED_OR_CHANGED";}}
            if (!d.Approved) return Results.StatusCode(403);
            // A device runs only one command until its lease expires or it reports completion.
            if (!urgentOnly && commands.Values.Any(w => w.Payload.DeviceId == id && w.Payload.Operation != "Quarantine" && w.State is "Leased" or "Running" && w.LeaseExpires > Now))
                return Results.Ok(new LeaseReply(null));
            var work = commands.Values.FirstOrDefault(w => w.Payload.DeviceId == id && w.Payload.SessionId == d.SessionId
                && (urgentOnly ? w.Payload.Operation == "Quarantine" : w.Payload.Operation != "Quarantine")
                && (!d.Paused || w.Payload.Operation is not ("ConnectProfile" or "ConnectApprovedSmb"))
                && (w.State == "Queued" || (w.State is "Leased" or "Running" && w.LeaseExpires <= Now)));
            if (work is null) return Results.Ok(new LeaseReply(null));
            work.State = "Leased"; work.LeaseId = Guid.NewGuid().ToString(); work.LeaseExpires = Now.AddSeconds(60);
            Persist();
            return Results.Ok(new LeaseReply(new Lease(new Envelope(work.Payload, work.Signature), work.LeaseId)));
        }
    }
    public IResult Result(string deviceId, string id, ResultRequest r)
    {
        lock (gate)
        {
            Expire();
            if (!commands.TryGetValue(id, out var w) || w.Payload.DeviceId != deviceId) return Results.NotFound();
            if (w.Payload.SessionId != devices[deviceId].SessionId || w.State is not ("Leased" or "Running")
                || w.LeaseId != r.LeaseId || w.LeaseExpires <= Now) return Results.Conflict();
            if (r.Mode != (w.Payload.Operation is "ConnectApprovedSmb" or "ReconnectAssigned" ? "RealSmb" : "Simulation") || r.State is not ("Running" or "Succeeded" or "Failed") || r.Detail.Length > 1000) return Results.BadRequest();
            if (r.State == "Running") w.LeaseExpires = Now.AddSeconds(60);
            devices[deviceId].LastSeen = Now;
            w.State = r.State; w.Detail = r.Detail; Persist(); return Results.Ok(new { accepted = true });
        }
    }
    private static CommandView View(Work w) => new(w.Payload.Id, w.Payload.DeviceId, w.DeviceName, w.Payload.Operation == "ConnectProfile" ? w.Payload.ProfileId : w.Payload.Operation,
        w.State, w.Detail, w.Payload.IssuedAt, w.Payload.ExpiresAt);
    private sealed class Device(string id, string name, string user, string key, string sessionId)
    {
        public string Id = id, Name = name, User = user, Key = key, SessionId = sessionId;
        public string RequestedDepartment = "", ComputerName = ""; public bool Approved, Paused, Archived, ConnectionsHeld;
        public DateTimeOffset RequestedAt = Now; public RegistrationCorrection? Correction;
        public HardwareIdentity? Hardware;
        public bool BetaAdministrator;
        public string BetaChannel = BetaChannels.Open;
        public bool PushConnected, Quarantined;
        public string IncidentId = "";
        public Channel<int> Signal = Channel.CreateBounded<int>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
        public DateTimeOffset LastSeen = Now;
        public string[] Drives = []; public ConnectionReport[] Connections = []; public int AppliedPolicyRevision = -1;
    }
    private sealed class Work(CommandPayload payload, string signature, string name)
    {
        public CommandPayload Payload = payload;
        public string Signature = signature, DeviceName = name, State = "Queued", Detail = "대기 중", LeaseId = "";
        public DateTimeOffset LeaseExpires;
    }
}
