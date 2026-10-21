using System.Net.Http.Json;
using SmbManager.Local;

namespace SmbManager.Local.Client;

public sealed class SimulationExecutor
{
    private readonly string journalFile;
    private readonly Journal journal;
    private readonly object gate = new();
    public SimulationExecutor(string stateDir)
    {
        journalFile = Path.Combine(stateDir, "journal.json");
        journal = File.Exists(journalFile) ? Wire.Load<Journal>(journalFile) : new();
    }
    public string[] Profiles { get { lock (gate) return journal.Profiles.Order().ToArray(); } }
    public int Completed { get { lock (gate) return journal.Receipts.Count; } }
    public bool Quarantined { get { lock (gate) return journal.Quarantined; } }
    public string IncidentId { get { lock (gate) return journal.IncidentId; } }
    public string[] Notifications { get { lock (gate) return journal.Notifications.ToArray(); } }
    public NotificationEntry[] NotificationEntries { get { lock(gate)return journal.NotificationEntries.ToArray(); } }
    public void Quarantine(string reason, string? incidentId = null)
    {
        lock (gate)
        {
            if (!journal.Quarantined) journal.IncidentId = Guid.NewGuid().ToString();
            if (incidentId is not null) journal.IncidentId = incidentId;
            journal.Quarantined = true; journal.Profiles.Clear(); journal.Reason = reason;
            Wire.Save(journalFile, journal);
        }
    }
    public Receipt? Previous(string id) { lock (gate) return journal.Receipts.GetValueOrDefault(id); }
    public void Record(Receipt receipt) { lock (gate) { journal.Receipts[receipt.Id] = receipt; Wire.Save(journalFile, journal); } }
    public static bool Valid(Identity identity, string sessionId, Envelope envelope)
    {
        var p = envelope.Payload;
        var snapshotValid = p.Mode == "SimulateConnect" && p.TargetProfile is { } profile && profile.Id == p.ProfileId
            && profile.Revision == p.ProfileRevision && profile.Mode == p.Mode && ProfileRules.Available(profile);
        var operationValid = p.Operation switch
        {
            "Notify" => p.Text is { Length: <= 1000 },
            "Quarantine" => true, "ReconnectAssigned" => true,
            "ReleaseQuarantine" => Guid.TryParse(p.Text, out _),
            "ConnectApprovedSmb" => snapshotValid,
            "ConnectProfile" => snapshotValid || (p.ProfileRevision == 1 &&
                ((p.ProfileId == "demo-share" && p.Mode == "SimulateConnect") || (p.ProfileId == "demo-auth-failure" && p.Mode == "SimulateAuthFailure"))),
            _ => false
        };
        return operationValid && Signature.Verify(identity.PublicKey, envelope) && p.DeviceId == identity.DeviceId && p.SessionId == sessionId
            && p.ExpiresAt > DateTimeOffset.UtcNow && p.IssuedAt <= DateTimeOffset.UtcNow.AddSeconds(10);
    }
    public Receipt Execute(Identity identity, string sessionId, Envelope envelope, bool sensorHealthy = true)
    {
        if (!Valid(identity, sessionId, envelope)) throw new InvalidOperationException("명령 서명·대상·세션·만료·정책 검증 실패");
        if (envelope.Payload.Operation == "ConnectApprovedSmb") throw new InvalidOperationException("실제 연결은 관리자 배정 엔진을 통해서만 실행합니다.");
        lock (gate)
        {
        var p = envelope.Payload;
        if (journal.Receipts.TryGetValue(p.Id, out var saved)) return saved;
        // Deliberately contains no WNet, net use, credential, registry or shell invocation.
        Receipt result;
        if (p.Operation == "Notify")
        {
            journal.NotificationEntries.Add(new(p.Id,p.Text,DateTimeOffset.UtcNow));if(journal.NotificationEntries.Count>50)journal.NotificationEntries.RemoveAt(0);
            journal.Notifications.Add(p.Text); if (journal.Notifications.Count > 50) journal.Notifications.RemoveAt(0);
            result = new(p.Id, "Succeeded", "Client 알림함에 수신됨 — 사용자 읽음은 확인하지 않음");
        }
        else if (p.Operation == "Quarantine")
        {
            Quarantine("관리자 격리 시연 요청");
            result = new(p.Id, "Succeeded", "모의 격리 잠금 적용 — 실제 네트워크 차단 미구현");
        }
        else if (p.Operation == "ReleaseQuarantine")
        {
            if (!sensorHealthy || !journal.Quarantined || p.Text != journal.IncidentId)
                result = new(p.Id, "Failed", "격리 ID 불일치 또는 탐지 파일 복구/감시 재시작 필요");
            else { journal.Quarantined = false; journal.Reason = ""; result = new(p.Id, "Succeeded", "모의 격리 해제 — 자동 연결 재개 안 함"); }
        }
        else if (journal.Quarantined) result = new(p.Id, "Failed", "QUARANTINED: 모의 격리 중 연결/재연결 금지");
        else result = p.Mode == "SimulateConnect"
            ? new Receipt(p.Id, "Succeeded", "모의 연결 완료 — 실제 공유폴더/드라이브 변경 없음")
            : new Receipt(p.Id, "Failed", "AUTH_FAILED 모의 시험 — 기존 모의 연결 상태 보존");
        if (result.State == "Succeeded" && p.Operation == "ConnectProfile") journal.Profiles.Add(p.ProfileId);
        journal.Receipts[p.Id] = result;
        Wire.Save(journalFile, journal); // Durable receipt is written before acknowledging the server.
        return result;
        }
    }
    public sealed class Journal
    {
        public Dictionary<string, Receipt> Receipts { get; set; } = [];
        public HashSet<string> Profiles { get; set; } = [];
        public bool Quarantined { get; set; }
        public string IncidentId { get; set; } = "";
        public string Reason { get; set; } = "";
        public List<string> Notifications { get; set; } = [];
        public List<NotificationEntry> NotificationEntries { get; set; } = [];
    }
}

public sealed partial class ClientAgent : IDisposable
{
    private readonly HttpClient http;
    private readonly string stateDir;
    private readonly SimulationExecutor executor;
    private readonly RealSmbEngine real;
    private LocalState current;
    private volatile bool paused;
    private volatile bool pushConnected;
    private volatile bool managementApproved;
    private readonly SemaphoreSlim wake = new(0, 1);
    private readonly object stateGate = new();
    private CanarySensor? sensor;
    public bool PushConnected => pushConnected;
    public bool Quarantined => executor.Quarantined;
    public string RealStatus => real.ManagedSummary + "\n" + real.Summary;
    public ConnectionReport[] ConnectionReports => real.Connections();


    public Task StopRealConnections(CancellationToken ct) => real.DisconnectAllOwned(ct);
    public async Task<Receipt[]> ReconnectNow(CancellationToken ct, bool replacePreviousMappings=false){if(!managementApproved||paused||Quarantined)throw new InvalidOperationException("승인 또는 연결 권한을 확인하세요.");var results=await real.ReconnectNow(ct,replacePreviousMappings);Signal();return results;}
    public string SensorStatus => sensor?.Status ?? "미시작 (실제 네트워크 보호 없음)";
    public void StartSensor()
    {
        sensor?.Dispose();
        sensor = new CanarySensor(Path.Combine(stateDir, "alpha-sensor"), reason =>
        {
            var incident = real.LatchQuarantine(reason);
            executor.Quarantine(reason, incident); Update("Quarantined", reason + " / 실제 연결 잠금. 네트워크 전체 차단은 미구현"); Signal();
            _ = ContainLocally();
        });
        Signal();
    }
    private async Task ContainLocally()
    {
        try { var detail = await real.Contain(CancellationToken.None); Update("Quarantined", detail); }
        catch (Exception e) { Update("Quarantined", "PARTIAL_BLOCK: 드라이브 해제 실패 " + e.Message); }
    }
    public void DemonstrateDetection() => (sensor ?? throw new InvalidOperationException("탐지 파일 감시를 먼저 시작하세요.")).Demonstrate();
    private void Signal() { try { wake.Release(); } catch (SemaphoreFullException) { } }
    public Identity Identity { get; private set; }
    private ClientRegistration? registrationView;
    private DepartmentPolicy? policyView;
    public ClientRegistration? Registration => Volatile.Read(ref registrationView);
    public DepartmentPolicy? Policy => Volatile.Read(ref policyView);
    public NotificationEntry[] NotificationEntries => executor.NotificationEntries;
    public async Task RefreshRegistration(CancellationToken ct){
        var info=await http.GetFromJsonAsync<ClientRegistration>("api/client/registration",Wire.Json,ct)??throw new InvalidOperationException("등록 정보를 확인하지 못했습니다.");
        Volatile.Write(ref registrationView,info);
        var next=Identity with{Name=info.Name,Department=info.RequestedDepartment,ComputerName=info.ComputerName};
        if(next!=Identity){SecretFile.Save(Path.Combine(stateDir,"identity.json.protected"),next);Identity=next;}
    }
    public async Task RequestCorrection(CorrectionRequest request,CancellationToken ct){await Wire.Read<System.Text.Json.JsonElement>(await http.PostAsJsonAsync("api/client/registration-correction",request,Wire.Json,ct),ct);await RefreshRegistration(ct);}
    public string SessionId { get; }
    public LocalState Current => Volatile.Read(ref current);
    public bool Paused { get => paused; set { paused = value; real.SetManagementPaused(value); Signal(); } }
    private ClientAgent(Identity identity, string name, string directory, string sessionId, string securityStateDir)
    {
        Identity = identity; stateDir = directory; SessionId = sessionId;
        http = Wire.Http(identity.Url, device: identity.DeviceKey);
        executor = new(directory);
        real = new(securityStateDir); real.EnableManagedMode();real.SetManagementPaused(false);
        if (executor.Quarantined && !real.Quarantined) real.LatchQuarantine("이전 격리 기록 복원", executor.IncidentId);
        if (real.Quarantined) executor.Quarantine("영속 격리 잠금 복원", real.IncidentId);
        current = new(identity.DeviceId, name, "Starting", "등록 및 승인 상태 확인 중", executor.Profiles, executor.Completed, DateTimeOffset.UtcNow);
        Update("Starting", "로컬 시제품: 모의 작업만 실행합니다.");
    }
    public static async Task<ClientAgent> Create(ClientBootstrap bootstrap, string stateDir, CancellationToken ct = default, string? securityStateDir = null)
    {
        Wire.LocalUrl(bootstrap.Url);
        Directory.CreateDirectory(stateDir);
        var identityFile = Path.Combine(stateDir, "identity.json");
        Identity identity;
        var hardware = HardwareIdentity.Capture();
        if (!HardwareIdentity.Valid(hardware)) throw new InvalidOperationException("장치 MAC 정보를 확인할 수 없습니다. 네트워크 어댑터를 확인하세요.");
        var sessionId = Guid.NewGuid().ToString();
        if (File.Exists(identityFile) || File.Exists(identityFile + ".protected"))
        {
            identity = SecretFile.LoadOrMigrate<Identity>(identityFile);
            if (identity.Url != bootstrap.Url) throw new InvalidOperationException("다른 로컬 서버의 장치 정보입니다. 새 테스트 폴더를 사용하세요.");
        }
        else identity = null!;
        if (identity is null || !HardwareIdentity.Same(identity.Hardware, hardware))
        {
            using var registration = Wire.Http(bootstrap.Url);
            EnrollReply reply;
            try { reply = bootstrap.Code.Length==0 ? await Wire.Read<EnrollReply>(await registration.PostAsJsonAsync("api/registration/request",new RegistrationRequest(bootstrap.Name,bootstrap.Department,Environment.UserName,sessionId,hardware,Environment.MachineName),Wire.Json,ct),ct) : await Wire.Read<EnrollReply>(await registration.PostAsJsonAsync("api/enroll",
                new EnrollRequest(bootstrap.Code, bootstrap.Name, Environment.UserName, sessionId, hardware, Environment.MachineName), Wire.Json, ct), ct); }
            catch (HttpRequestException e) when (e.StatusCode == System.Net.HttpStatusCode.BadRequest) {
                throw new InvalidOperationException("새 PC 또는 하드웨어 변경/구버전 등록 정보입니다. 관리 서버와 부서를 선택하고 성명을 입력해 등록 요청 후 승인을 받아주세요.", e);
            }
            identity = new(bootstrap.Url, reply.DeviceId, reply.DeviceKey, reply.PublicKey, hardware,Environment.MachineName,bootstrap.Name,bootstrap.Department);
            SecretFile.Save(identityFile + ".protected", identity);
        }
        return new ClientAgent(identity, identity.Name.Length>0?identity.Name:bootstrap.Name, stateDir, sessionId, Path.Combine(securityStateDir ?? Path.Combine(stateDir, "real-smb"), identity.DeviceId));
    }
    public async Task Run(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var push = ReadPush(linked.Token);
        var urgent = UrgentLoop(linked.Token);
        var reconnect = ReconnectLoop(linked.Token);
        var policy = PolicyLoop(linked.Token);
        try { await RunCore(linked.Token); }
        finally { linked.Cancel(); try { await Task.WhenAll(push, urgent, reconnect, policy); } catch (OperationCanceledException) { } }
    }
    private readonly SemaphoreSlim policyFetchGate=new(1,1);
    private async Task RefreshPolicy(CancellationToken ct){await policyFetchGate.WaitAsync(ct);try{
                    var envelope=await http.GetFromJsonAsync<PolicyEnvelope>("api/client/policy",Wire.Json,ct) ?? throw new InvalidOperationException("관리 정책이 비어 있습니다.");
                    if(!AssignmentRules.Verify(Identity,SessionId,envelope))throw new InvalidOperationException("관리 정책 서명/대상/부서/만료 검증 실패");
                    var credentials=new Dictionary<string,ShareCredential>();
                    foreach(var p in envelope.Payload.Profiles){
                        using var response=await http.GetAsync("api/client/credentials/"+Uri.EscapeDataString(p.Id),ct);
                        if(response.StatusCode==System.Net.HttpStatusCode.NoContent)continue;
                        var value=await Wire.Read<ShareCredential>(response,ct);
                        if(value.Account!=(envelope.Payload.Accounts?.FirstOrDefault(x=>x.ProfileId==p.Id)?.Account??envelope.Payload.ExpectedAccount))throw new InvalidOperationException("배정 계정이 변경되었습니다.");
                        credentials[p.Id]=value;
                    }
                    real.SetCredentials(credentials);
                    await real.ApplyPolicy(envelope.Payload,ct);
                    Volatile.Write(ref policyView,envelope.Payload);
    }finally{policyFetchGate.Release();}}
    private async Task PolicyLoop(CancellationToken ct)
    {
        while(!ct.IsCancellationRequested){
            try{
                if(managementApproved){
                    await RefreshPolicy(ct);
                }

            } catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
            catch(Exception e){Update(Current.Phase,"관리 배정 확인 필요: "+e.Message);}
            try{await real.MaintainPolicy(ct);}catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}catch(Exception e){Update(Current.Phase,"정책 회수 확인 필요: "+e.Message);}
            try{await Task.Delay(2000,ct);}catch(OperationCanceledException){break;}
        }
    }
    private async Task ReconnectLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await real.RefreshConnections(ct); if (real.Quarantined) await real.Contain(ct);
                else if (!paused && managementApproved) await real.ReconnectDue(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e) { Update(Current.Phase, "실제 연결 엔진 확인 필요: " + e.Message); }
            try { await Task.Delay(5000, ct); } catch (OperationCanceledException) { break; }
        }
    }
    private async Task UrgentLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var lease = (await Wire.Read<LeaseReply>(await http.PostAsync("api/client/urgent-lease", null, ct), ct)).Lease;
                if (lease is not null) await HandleLease(lease, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception) { /* Main heartbeat reports server/auth errors. */ }
            try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { break; }
        }
    }
    private async Task HandleLease(Lease lease, CancellationToken ct)
    {
        var payload = lease.Envelope.Payload;
        if (!SimulationExecutor.Valid(Identity, SessionId, lease.Envelope)) throw new InvalidOperationException("변조/세션/만료 검증 실패");
        var mode = payload.Operation is "ConnectApprovedSmb" or "ReconnectAssigned" ? "RealSmb" : "Simulation";
        async Task Send(string state, string detail, CancellationToken token)
        {
            using var response = await http.PostAsJsonAsync($"api/client/commands/{payload.Id}/result", new ResultRequest(lease.LeaseId, state, detail, mode), Wire.Json, token);
            response.EnsureSuccessStatusCode();
        }
        await Send("Running", "Client가 요청을 처리 중입니다.", ct);
        using var renewalStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var renewal = Renew();
        async Task Renew()
        {
            try
            {
                while (true) { await Task.Delay(10000, renewalStop.Token); await Send("Running", "실행 중 — 임대 갱신", renewalStop.Token); }
            }
            catch (OperationCanceledException) when (renewalStop.IsCancellationRequested) { }
            catch (Exception) { /* Completion reporting may fail; durable local receipt remains available. */ }
        }
        try
        {
            Receipt receipt;
            if (paused && payload.Operation is "ConnectProfile" or "ConnectApprovedSmb") receipt = new(payload.Id, "Failed", "관리 일시 중지 중 연결 작업 거절");
            else if (executor.Previous(payload.Id) is { } previous) receipt = previous;
            else if(payload.Operation=="ReconnectAssigned"){
                await RefreshPolicy(ct);var results=await ReconnectNow(ct);receipt=new(payload.Id,results.Length>0&&results.All(r=>r.State=="Succeeded")?"Succeeded":"Failed",string.Join(" / ",results.Select(r=>r.Detail)) is var detail?detail[..Math.Min(900,detail.Length)]:"");executor.Record(receipt);
            }
            else if (payload.Operation == "ConnectApprovedSmb")
            {
                receipt = executor.Quarantined ? new(payload.Id, "Failed", "QUARANTINED") : await real.Connect(payload.Id, payload.TargetProfile!, ct);
                executor.Record(receipt);
            }
            else if (payload.Operation == "Quarantine")
            {
                var incident = real.LatchQuarantine("관리자 격리 요청");
                executor.Quarantine("관리자 격리 요청", incident); Update("Quarantined", "격리 잠금 적용, 드라이브 해제 진행 중"); Signal();
                var detail = await real.Contain(ct);
                receipt = new(payload.Id, "Failed", detail); executor.Record(receipt);
            }
            else
            {
                receipt = executor.Execute(Identity, SessionId, lease.Envelope, sensor?.Healthy ?? true);
                if (payload.Operation == "ReleaseQuarantine" && receipt.State == "Succeeded")
                {
                    try { real.Release(payload.Text); }
                    catch (Exception e) { executor.Quarantine("격리 해제 미완료", real.IncidentId); receipt = new(payload.Id, "Failed", e.Message); executor.Record(receipt); }
                }
            }
            renewalStop.Cancel(); await renewal;
            await Send(receipt.State, receipt.Detail, ct); Update(receipt.State, receipt.Detail); Signal();
        }
        finally { renewalStop.Cancel(); await renewal; }
    }
    private async Task ReadPush(CancellationToken ct)
    {
        using var streamHttp = Wire.Http(Identity.Url, device: Identity.DeviceKey);
        streamHttp.Timeout = Timeout.InfiniteTimeSpan;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "api/client/events");
                using var response = await streamHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
                pushConnected = true; Signal();
                while (await reader.ReadLineAsync(ct) is { } line)
                    if (line.StartsWith("data:") && !line.Contains("keepalive")) Signal();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception) { /* Authenticated lease/heartbeat reports the actionable error. */ }
            finally { pushConnected = false; }
            try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { break; }
        }
    }
    private async Task RunCore(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Enumerate drive letters only; never read remote directories or change a mapping.
                var drives = DriveInfo.GetDrives().Select(d => d.Name).ToArray();
                using var heartbeat = await http.PostAsJsonAsync("api/client/heartbeat", new Heartbeat(SessionId, paused, drives, pushConnected, executor.Quarantined, executor.IncidentId, real.Connections(), real.AppliedPolicyRevision,Environment.MachineName), Wire.Json, ct);
                heartbeat.EnsureSuccessStatusCode();
                managementApproved = (await heartbeat.Content.ReadFromJsonAsync<ApprovalState>(Wire.Json, ct))?.Approved == true;
                try{await RefreshRegistration(ct);}catch(HttpRequestException e)when(e.StatusCode==System.Net.HttpStatusCode.NotFound){}
                {
                    var response = await http.PostAsync("api/client/lease", null, ct);
                    if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                    {
                        response.Dispose(); Update("WaitingApproval", "관리 프로그램에서 이 PC의 등록을 승인하세요.");
                    }
                    else
                    {
                        var lease = (await Wire.Read<LeaseReply>(response, ct)).Lease;
                        if (lease is null)
                        {
                            if (Current.Phase is "Starting" or "WaitingApproval" or "Offline" or "Paused")
                                Update("Ready", "명령 대기 중 — 실제 SMB 연결은 변경하지 않습니다.");
                        }
                        else
                        {
                            await HandleLease(lease, ct);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e) { if (e is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden }) managementApproved = false; Update("Offline", "서버 통신/명령 확인 실패: " + e.Message); }
            if (Current.Phase != "Offline") Update(executor.Quarantined ? "Quarantined" : paused ? "Paused" : Current.Phase, Current.Message);
            try { await wake.WaitAsync(TimeSpan.FromSeconds(10), ct); } catch (OperationCanceledException) { break; }
        }
    }
    private void Update(string phase, string message)
    {
        lock (stateGate)
        {
        var state = new LocalState(Identity.DeviceId, current.Name, phase, message, executor.Profiles, executor.Completed, DateTimeOffset.UtcNow, pushConnected, executor.Quarantined, executor.Notifications);
        Volatile.Write(ref current, state);
        Wire.Save(Path.Combine(stateDir, "status.json"), state);
        }
    }
    public void Dispose() { sensor?.Dispose(); http.Dispose(); wake.Dispose(); real.Dispose(); }
    private sealed record ApprovalState(bool Approved);
}
