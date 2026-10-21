using SmbManager.Local;
namespace SmbManager.Local.Client;

public sealed partial class RealSmbEngine
{
    private HashSet<string> policyProfileIds = [];
    public int AppliedPolicyRevision { get { lock(policyGate) return state.Managed ? state.PolicyRevision : -1; } }
    public string ManagedSummary { get { lock(policyGate) return $"부서: {state.Department}\n배정 Drive 계정: {state.ExpectedAccount}\n정책: {(state.PolicyUntil > DateTimeOffset.UtcNow ? "유효" : "확인 필요 · 새 연결 금지")}"; } }
    public void EnableManagedMode() { lock(policyGate) { state.Managed=true;state.Epoch++;state.PolicyUntil=DateTimeOffset.MinValue;foreach(var a in state.Approvals.Values){a.Authorized=false;a.Enabled=false;}Save(); } }
    public void SetManagementPaused(bool value) { lock(policyGate){if(state.ManagementPaused!=value){state.ManagementPaused=value;state.Epoch++;Save();}} }
    private bool ManagedAllows(Approval a) => !state.Managed || (a.Authorized && !a.AccountMismatch && !state.ManagementPaused && state.PolicyUntil > DateTimeOffset.UtcNow);
    public async Task ApplyPolicy(DepartmentPolicy policy, CancellationToken ct)
    {
        var fingerprint=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new {policy.Department,policy.ExpectedAccount,policy.Profiles,policy.Reconnect,policy.ForceDisconnect,policy.Revision,policy.ResumeStopped,policy.Accounts,policy.AccessRole},Wire.Json)));
        bool revisionChanged;
        HashSet<string> enabledBefore;
        lock(policyGate) {
            enabledBefore=state.Approvals.Values.Where(a=>a.Enabled).Select(a=>a.Profile.Id).ToHashSet();
            if(!state.Managed || policy.Revision<state.PolicyRevision || policy.IssuedAt<state.PolicyIssuedAt) return;
            policyProfileIds=policy.Profiles.Select(p=>p.Id).ToHashSet();
            revisionChanged=policy.AssignmentRevision>state.AssignmentRevision;
            bool changed=state.PolicyFingerprint!=fingerprint || state.PolicyUntil<=DateTimeOffset.UtcNow || state.Approvals.Values.Any(a=>(a.Pending && operationGate.CurrentCount>0) || (!a.Authorized && a.Owned));
            state.PolicyUntil=policy.ExpiresAt;state.PolicyIssuedAt=policy.IssuedAt;
            if(!changed){Save();return;}
            state.Epoch++;
            foreach(var previous in state.Approvals.Values) {
                var next=policy.Profiles.FirstOrDefault(p=>p.Id==previous.Profile.Id) ?? policy.Profiles.FirstOrDefault(p=>string.Equals(WindowsCredential.Server(p.Unc),WindowsCredential.Server(previous.Profile.Unc),StringComparison.OrdinalIgnoreCase));
                var nextAccount=next is null?null:policy.Accounts?.FirstOrDefault(x=>x.ProfileId==next.Id)?.Account??policy.ExpectedAccount;
                if(nextAccount is not null && !string.Equals(previous.ExpectedAccount,nextAccount,StringComparison.OrdinalIgnoreCase)) {
                    var server=WindowsCredential.Server(previous.Profile.Unc);
                    if(server.Length>0)state.PreviousServerAccounts[server]=state.PreviousServerAccounts.GetValueOrDefault(server,[]).Append(previous.ExpectedAccount).Distinct(StringComparer.OrdinalIgnoreCase).TakeLast(5).ToArray();
                }
            }
            foreach(var a in state.Approvals.Values){a.Authorized=false;a.Enabled=false;a.ForceDisconnect=policy.ForceDisconnect;}
            state.AssignmentRevision=policy.AssignmentRevision;state.Department=policy.Department;state.ExpectedAccount=policy.ExpectedAccount;state.PolicyRevision=policy.Revision;state.PolicyFingerprint=fingerprint;
            if(revisionChanged && policy.ResumeStopped)state.ConnectionsStopped=false;
            Save();
        }
        await operationGate.WaitAsync(ct);
        try {
            Approval[] old;lock(policyGate)old=state.Approvals.Values.ToArray();
            foreach(var a in old) {
                // The operation gate and exclusive engine lock exclude an in-flight native call.
                // Only proven absence can resolve an interrupted attempt; never adopt a mapping.
                if(a.Pending && NativeMapping.Get(a.Profile.Drive).Code==2250 && !NativeMapping.DeviceAssigned(a.Profile.Drive)) {
                    lock(policyGate) {
                        a.Pending=false;a.Owned=false;a.Failures=0;a.NextAttempt=DateTimeOffset.UtcNow;
                        a.Status="중단된 연결 기록 복구 — 새 정책 확인 대기";
                    }
                }
                var target=policy.Profiles.FirstOrDefault(p=>p.Id==a.Profile.Id);
                if(target is not null && Fingerprint(target)==Fingerprint(a.Profile) && a.ExpectedAccount==(policy.Accounts?.FirstOrDefault(x=>x.ProfileId==target.Id)?.Account??policy.ExpectedAccount) && !a.Pending)continue;
                if(a.Owned)await Task.Run(()=>DisconnectOwned(a),CancellationToken.None);
                lock(policyGate){if(!a.Owned && !a.Pending)state.Approvals.Remove(a.Profile.Id);}
            }
            lock(policyGate) {
                if(state.PolicyRevision!=policy.Revision || state.PolicyUntil<=DateTimeOffset.UtcNow){Save();return;}
                foreach(var p in policy.Profiles) {
                    if(!ProfileRules.Available(p))continue;
                    if(state.Approvals.TryGetValue(p.Id,out var existing) && (existing.Pending || Fingerprint(existing.Profile)!=Fingerprint(p) || existing.ExpectedAccount!=(policy.Accounts?.FirstOrDefault(x=>x.ProfileId==p.Id)?.Account??policy.ExpectedAccount)))continue;
                    var a=existing ?? new Approval{Profile=p,NextAttempt=DateTimeOffset.UtcNow};
                    a.ExpectedAccount=policy.Accounts?.FirstOrDefault(x=>x.ProfileId==p.Id)?.Account??policy.ExpectedAccount;a.Authorized=true;a.Reconnect=policy.Reconnect;a.ForceDisconnect=policy.ForceDisconnect;
                    if(revisionChanged)a.AccountMismatch=false;
                    if(revisionChanged && policy.ResumeStopped){a.NeedsCommand=false;a.AuthRejected=false;}
                    a.Enabled=!state.Quarantined&&!state.ConnectionsStopped&&!a.AccountMismatch&&!a.NeedsCommand&&!a.AuthRejected
                        && (a.Reconnect || existing is null || revisionChanged || enabledBefore.Contains(p.Id));
                    if(existing is null)a.Status="관리자 배정됨 — 자동 연결 대기";
                    state.Approvals[p.Id]=a;
                }
                Save();
            }
        } finally{operationGate.Release();}
    }
    public async Task MaintainPolicy(CancellationToken ct)
    {
        bool cleanup;
        lock(policyGate){
            if(!state.Managed)return;
            if(state.PolicyUntil<=DateTimeOffset.UtcNow && state.Approvals.Values.Any(a=>a.Authorized)){
                state.Epoch++;foreach(var a in state.Approvals.Values){a.Authorized=false;a.Enabled=false;a.Status="관리 정책 만료 — 새 연결 중지 / 앱 소유 연결 회수";}Save();
            }
            cleanup=state.Approvals.Values.Any(a=>a.Owned&&!a.Authorized);
        }
        if(!cleanup)return;
        await operationGate.WaitAsync(ct);
        try {Approval[] revoked;lock(policyGate)revoked=state.Approvals.Values.Where(a=>a.Owned&&!a.Authorized).ToArray();foreach(var a in revoked)await Task.Run(()=>DisconnectOwned(a),CancellationToken.None);}
        finally{operationGate.Release();}
    }
    private ConnectionReport[] cachedConnections = [];
    public ConnectionReport[] Connections() => Volatile.Read(ref cachedConnections);
    public async Task RefreshConnections(CancellationToken ct)
    {
        if(!OperatingSystem.IsWindows())return;
        var reports=await Task.Run(()=>{
            Approval[] assigned; lock(policyGate)assigned=state.Approvals.Values.Where(a=>a.Authorized || (a.Pending && policyProfileIds.Contains(a.Profile.Id))).ToArray();
            var rows=new List<ConnectionReport>();var letters=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var a in assigned){
                letters.Add(a.Profile.Drive);var map=NativeMapping.Get(a.Profile.Drive);
                var same=NativeMapping.Same(map.Remote,a.Profile.Unc);
                var health=map.Code==2250?"Disconnected":!same?"Conflict":map.Code==0&&NativeMapping.Accessible(a.Profile.Drive)?"Connected":"Unavailable";
                var account=health=="Connected"?NativeMapping.Account(a.Profile.Drive):"";
                health=a.Pending?"ReconciliationRequired":a.AuthRejected?"AuthenticationRequired":ConnectionRules.AccountState(health,account,a.ExpectedAccount);
                var label=health switch{"AuthenticationRequired"=>"Drive 계정·암호 확인 필요 · 자동 인증 중지","ReconciliationRequired"=>"이전 연결 작업 확인 필요 · 재연결 보류","Disconnected"=>"연결 끊김 · 재연결 대기","Unavailable"=>"접근 불가 · 재연결 대기","Conflict"=>"다른 드라이브 사용 중 · 기존 연결 보존",_=>string.Equals(account,a.ExpectedAccount,StringComparison.OrdinalIgnoreCase)?"연결됨":"연결 계정 확인 필요"};
                rows.Add(new(a.Profile.Id,a.Profile.Drive,a.Profile.Unc,account,label,a.Owned,ConnectionState:health));
                lock(policyGate){if(health=="Disconnected"&&a.Reconnect&&ManagedAllows(a)&&!state.Quarantined&&!state.ConnectionsStopped&&!a.Pending&&!a.NeedsCommand&&!a.AuthRejected){a.Enabled=true;if(a.Failures==0)a.NextAttempt=DateTimeOffset.UtcNow;}}
            }
            foreach(var drive in DriveInfo.GetDrives().Select(d=>d.Name[..2]).Where(d=>!letters.Contains(d))){
                var map=NativeMapping.Get(drive);if(map.Code is not (0 or 1201)||string.IsNullOrEmpty(map.Remote))continue;
                rows.Add(new("",drive,map.Remote,NativeMapping.Account(drive),"기존 외부 연결",false,ConnectionState:map.Code==0?"Unknown":"Unavailable"));
            }
            return rows.Take(26).ToArray();
        },ct);
        Volatile.Write(ref cachedConnections,reports);
    }
    private readonly Dictionary<string,ShareCredential> sessionCredentials=[];
    public void SetCredentials(Dictionary<string,ShareCredential> values){lock(policyGate){
        var changed=values.Where(x=>!sessionCredentials.TryGetValue(x.Key,out var old)||old!=x.Value).Select(x=>x.Key).ToHashSet();
        sessionCredentials.Clear();foreach(var x in values)sessionCredentials[x.Key]=x.Value;
        bool saved=false;
        foreach(var a in state.Approvals.Values.Where(a=>a.AuthRejected&&changed.Contains(a.Profile.Id))){
            a.AuthRejected=false;a.Failures=0;a.NextAttempt=DateTimeOffset.UtcNow;
            a.Enabled=a.Reconnect&&ManagedAllows(a)&&!state.Quarantined&&!state.ConnectionsStopped&&!a.NeedsCommand;
            saved=true;
        }
        if(saved)Save();
    }}
    public async Task<Receipt[]> ReconnectNow(CancellationToken ct, bool replacePreviousMappings=false){
        var transitions=replacePreviousMappings?await SwitchPreviousMappings(ct):Array.Empty<Receipt>();
        Profile[] targets;lock(policyGate){
            if(state.Quarantined||state.ManagementPaused||state.PolicyUntil<=DateTimeOffset.UtcNow||state.ConnectionsStopped)throw new InvalidOperationException("연결 권한을 확인할 수 없습니다. 관리자에게 문의하세요.");
            var assigned=state.Approvals.Values.Where(a=>ManagedAllows(a)&&!a.NeedsCommand).ToArray();
            foreach(var a in assigned)a.AuthRejected=false;Save();
            targets=assigned.Select(a=>a.Profile).ToArray();
        }
        var results=new List<Receipt>(transitions);foreach(var p in targets)results.Add(await Connect("manual-"+Guid.NewGuid(),p,ct));
        await RefreshConnections(ct);return results.ToArray();
    }
}