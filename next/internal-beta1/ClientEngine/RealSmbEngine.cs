using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using SmbManager.Local;

namespace SmbManager.Local.Client;

// Native calls run off the UI thread. Only authorized profiles can reach WNet.
// This owns drive mappings, not the Windows network stack; containment is always PartialBlock.
public sealed partial class RealSmbEngine : IDisposable
{
    private readonly FileStream instanceLock;
    private readonly string file;
    private readonly object policyGate = new();
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly State state;
    public RealSmbEngine(string directory)
    {
        Directory.CreateDirectory(directory);
        instanceLock = new FileStream(Path.Combine(directory, "client.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        file = Path.Combine(directory, "smb-policy.json");
        state = File.Exists(file) ? Wire.Load<State>(file) : new();
    }
    public bool Quarantined { get { lock (policyGate) return state.Quarantined; } }
    public string IncidentId { get { lock (policyGate) return state.IncidentId; } }
    public string Summary
    {
        get { lock (policyGate) return string.Join("\n", state.Approvals.Values.Select(a => $"{a.Profile.Drive} {a.Profile.Unc} · {a.Status} · 재시도 {(a.Enabled ? "ON" : "OFF")}")); }
    }
    public string LatchQuarantine(string reason, string? incidentId = null)
    {
        lock (policyGate)
        {
            state.Epoch++;
            if (!state.Quarantined) state.IncidentId = incidentId ?? Guid.NewGuid().ToString();
            state.Quarantined = true; state.Reason = reason;
            foreach (var a in state.Approvals.Values) a.Enabled = false;
            Save(); return state.IncidentId;
        }
    }
    public void Release(string incidentId)
    {
        lock (policyGate)
        {
            if (!state.Quarantined || state.IncidentId != incidentId) throw new InvalidOperationException("현재 격리 사고 ID와 일치하지 않습니다.");
            state.Epoch++; state.Quarantined = false; state.Reason = "";
            // Resume requires a fresh explicit command or an explicit per-device resume policy.
            foreach (var a in state.Approvals.Values) { a.Enabled = false; a.NeedsCommand = true; }
            Save();
        }
    }
    public async Task<Receipt> Connect(string commandId, Profile profile, CancellationToken ct, bool automatic = false)
    {
        await operationGate.WaitAsync(ct);
        try { return await Task.Run(() => ConnectCore(commandId, profile, automatic), CancellationToken.None); }
        finally { operationGate.Release(); }
    }
    private Receipt ConnectCore(string id, Profile profile, bool automatic)
    {
        Approval a; long epoch;
        lock (policyGate)
        {
            if (state.Quarantined) return new(id, "Failed", "QUARANTINED: 실제 연결/재연결 금지");
            if (state.ConnectionsStopped) return new(id, "Failed", "LOCAL_STOPPED: 관리자가 해당 PC의 연결 재개를 배정해야 합니다.");
            if (!state.Approvals.TryGetValue(profile.Id, out a!) || Fingerprint(a.Profile) != Fingerprint(profile))
                return new(id, "Failed", "MANAGED_PERMISSION_DENIED: 관리자에게 해당 부서/공유 배정을 요청하세요.");
            if(!ManagedAllows(a))return new(id,"Failed","MANAGED_PERMISSION_DENIED: 유효한 부서/계정 배정 필요");
            if (a.Pending) return new(id, "Failed", "UNKNOWN: 이전 연결 작업의 소유권 확인 필요, 자동 변경하지 않음");
            if(a.AuthRejected)return new(id,"Failed","AUTHENTICATION_REQUIRED: Drive 계정·암호를 확인한 뒤 다시 연결하세요. 같은 서버의 자동 인증은 중지했습니다.");
            if(automatic && a.NeedsCommand)return new(id,"Failed","RESUME_REQUIRED: 관리자 명시적 연결 재개 필요");
            if(!automatic){a.NeedsCommand=false;Save();}
            epoch = state.Epoch;
        }
        var mapping = NativeMapping.Get(profile.Drive);
        var same = NativeMapping.Same(mapping.Remote, profile.Unc) && mapping.Code is 0 or 1201;
        if (!same && (mapping.Code != 2250 || NativeMapping.DeviceAssigned(profile.Drive)))
            return Finish(a, new(id, "Failed", "DRIVE_CONFLICT: 다른 매핑/로컬 장치 보존"));
        if(same && state.Managed && !string.Equals(NativeMapping.Account(profile.Drive),a.ExpectedAccount,StringComparison.OrdinalIgnoreCase))
        {
            ShareCredential? replacement; lock(policyGate)sessionCredentials.TryGetValue(profile.Id,out replacement);
            if(!a.Owned || replacement is null || !string.Equals(replacement.Account,a.ExpectedAccount,StringComparison.OrdinalIgnoreCase))
            { lock(policyGate){a.AccountMismatch=true;a.Enabled=false;Save();} return Finish(a,new(id,"Failed","ACCOUNT_MISMATCH: 기존 연결 계정이 배정과 다릅니다. 외부 연결은 보존합니다. 관리자에게 새 계정·암호 저장과 기존 연결 정리를 요청하세요."),false); }
            // Only an app-owned mapping may be replaced; never cancel a whole server session.
            DisconnectOwned(a);
            mapping=NativeMapping.Get(profile.Drive);
            if(mapping.Code!=2250 || NativeMapping.DeviceAssigned(profile.Drive))
                return Finish(a,new(id,"Failed","ACCOUNT_SWITCH_PENDING: 사용 중인 파일을 닫고 다시 연결하세요. 기존 연결은 강제로 끊지 않습니다."));
            same=false;
        }
        if (same && !a.Owned)
        {
            if (mapping.Code == 0 && NativeMapping.Accessible(profile.Drive))
                return Finish(a, new(id, "Succeeded", "ALREADY_CONNECTED: 기존 연결 사용, 앱 소유권을 가져오지 않음"), false);
            return Finish(a, new(id, "Failed", "EXTERNAL_REMEMBERED_MAPPING: 외부 저장 매핑은 변경하지 않음"), false);
        }
        if (same && NativeMapping.Accessible(profile.Drive))
            return Finish(a, new(id, "Succeeded", "실제 SMB 연결 정상"));
        ShareCredential? credential;
        lock (policyGate)
        {
            if (state.Quarantined || state.Epoch != epoch || !ManagedAllows(a)) return new(id, "Failed", "격리/정책 변경으로 연결 중지");
            sessionCredentials.TryGetValue(profile.Id,out credential);
            // Pending describes an ambiguous native attempt, never a preflight rejection.
            if(credential is not null&&!string.Equals(credential.Account,a.ExpectedAccount,StringComparison.OrdinalIgnoreCase))
                return Finish(a,new(id,"Failed","CREDENTIAL_POLICY_CHANGED: 새 관리 정책 대기"));
            a.Pending = true; Save();
        }
        var code = NativeMapping.Connect(profile.Drive, profile.Unc, credential);
        if (same && code is 85 or 1202) code = NativeMapping.Connect(null, profile.Unc, credential);
        var after = NativeMapping.Get(profile.Drive);
        var connected = code == 0 && after.Code == 0 && NativeMapping.Same(after.Remote, profile.Unc);
        bool stale;
        lock (policyGate)
        {
            stale = state.Quarantined || state.Epoch != epoch || !ManagedAllows(a);
            a.Pending = false;
            if (connected && !same) a.Owned = true;
            Save();
        }
        if(connected && state.Managed && !string.Equals(NativeMapping.Account(profile.Drive),a.ExpectedAccount,StringComparison.OrdinalIgnoreCase)){lock(policyGate){a.AccountMismatch=true;a.Enabled=false;Save();} if(a.Owned)DisconnectOwned(a);return Finish(a,new(id,"Failed","ACCOUNT_MISMATCH: 새 앱 매핑 해제 시도 · Drive 자격 증명 확인 필요"),false);}
        if (stale)
        {
            if (connected && a.Owned) DisconnectOwned(a);
            return Finish(a, new(id, "Failed", "격리/정책 변경 중 발생한 연결 처리: 재연결 중지. 네트워크 전체 차단 미구현"), false);
        }
        RejectAuthenticationForServer(a,code,credential);
        if (connected && NativeMapping.Accessible(profile.Drive)) return Finish(a, new(id, "Succeeded", "실제 SMB 매핑 및 루트 접근 확인 완료" + SyncWindowsCredential(a)));
        return Finish(a, new(id, "Failed", code == 1219 ? "Windows SMB 오류 1219: 이 서버에 다른 계정의 연결이 남아 있습니다. 열린 파일을 닫고 관리자에게 기존 연결 정리를 요청하세요. 다른 연결·저장 암호는 보존했습니다." : code == 0 ? "매핑/접근 확인 실패 — 기존 연결 보존" : $"Windows SMB 오류 {code} — 기존 연결·자격 증명 보존"));
    }
    private string SyncWindowsCredential(Approval a)
    {
        lock(policyGate) {
            if(!state.Managed || !a.Owned || !ManagedAllows(a) || state.Quarantined || state.ConnectionsStopped || !sessionCredentials.TryGetValue(a.Profile.Id,out var credential) || !string.Equals(credential.Account,a.ExpectedAccount,StringComparison.OrdinalIgnoreCase))return "";
            var server=WindowsCredential.Server(a.Profile.Unc);
            var previous=state.PreviousServerAccounts.GetValueOrDefault(server,[]);
            return WindowsCredential.UpdateExisting(server,credential,previous);
        }
    }
    private void RejectAuthenticationForServer(Approval failed,int code,ShareCredential? attempted)
    {
        if(code is not (86 or 1326 or 1909))return;
        lock(policyGate){
            sessionCredentials.TryGetValue(failed.Profile.Id,out var current);
            if(current!=attempted)return; // A new credential arrived while the old native call was running.
            var host=WindowsCredential.Server(failed.Profile.Unc);
            foreach(var a in state.Approvals.Values.Where(a=>string.Equals(WindowsCredential.Server(a.Profile.Unc),host,StringComparison.OrdinalIgnoreCase))){
                a.AuthRejected=true;a.Enabled=false;a.Status=$"Windows SMB 오류 {code}: 저장된 Drive 계정·암호 확인 필요 · 해당 서버 자동 인증 중지";
            }
            Save();
        }
    }
    private Receipt Finish(Approval a, Receipt result, bool allowRetry = true)
    {
        lock (policyGate)
        {
            if(result.State=="Succeeded" && (state.Quarantined || state.ConnectionsStopped || !ManagedAllows(a)))
                result=new(result.Id,"Failed",state.Quarantined?"QUARANTINED: 연결 검사 중 격리됨":"POLICY_CHANGED: 연결 검사 중 권한·연결 상태가 변경됨");
            if (result.Detail.StartsWith("DRIVE_CONFLICT")) a.Owned = false;
            a.Status = result.Detail;
            a.Enabled = a.Owned && a.Reconnect && !state.Quarantined;
            // Missing mappings should still be retried after a real failed connect attempt.
            if (!a.Owned && !result.Detail.StartsWith("DRIVE_CONFLICT") && !result.Detail.StartsWith("ALREADY_CONNECTED")) a.Enabled = a.Reconnect && !state.Quarantined;
            a.Enabled &= allowRetry && !a.AuthRejected && !state.ConnectionsStopped && !a.NeedsCommand && ManagedAllows(a);
            a.Failures = result.State == "Succeeded" ? 0 : Math.Min(a.Failures + 1, 5);
            a.NextAttempt = DateTimeOffset.UtcNow.AddSeconds(result.State == "Succeeded" ? 30 : Math.Min(300, 15 * Math.Pow(2, a.Failures - 1)));
            Save(); return result;
        }
    }
    public async Task ReconnectDue(CancellationToken ct)
    {
        Profile[] due;
        lock (policyGate) due = state.Quarantined || state.ConnectionsStopped || state.ManagementPaused ? [] : state.Approvals.Values.Where(a => a.Enabled && ManagedAllows(a) && !a.Pending && a.NextAttempt <= DateTimeOffset.UtcNow).Select(a => a.Profile).ToArray();
        foreach (var p in due) { ct.ThrowIfCancellationRequested(); await Connect("reconnect-" + Guid.NewGuid(), p, ct, automatic:true); }
    }
    public async Task<string> Contain(CancellationToken ct)
    {
        await operationGate.WaitAsync(ct);
        try
        {
            Approval[] owned; lock (policyGate) owned = state.Approvals.Values.Where(a => a.Owned).ToArray();
            foreach (var a in owned) await Task.Run(() => DisconnectOwned(a), CancellationToken.None);
            return "PARTIAL_BLOCK: 앱 소유 드라이브 해제 시도 완료. 직접 UNC/열린 핸들/다른 앱 차단은 보장하지 않음 (Guard 미구현)";
        }
        finally { operationGate.Release(); }
    }
    private void DisconnectOwned(Approval a)
    {
        var current = NativeMapping.Get(a.Profile.Drive);
        if (!NativeMapping.Same(current.Remote, a.Profile.Unc) || current.Code is not (0 or 1201))
        {
            lock (policyGate) { a.Owned = false; a.Enabled = false; a.Status = "매핑 변경 감지 — 외부 연결은 해제하지 않음"; Save(); } return;
        }
        var code = NativeMapping.Disconnect(a.Profile.Drive, a.ForceDisconnect);
        lock (policyGate)
        {
            if (code is 0 or 2250) a.Owned = false;
            a.Enabled = false; a.Status = code == 0 ? "앱 소유 드라이브 해제됨 (전체 네트워크 격리 아님)" : $"드라이브 해제 미완료: {code}"; Save();
        }
    }
    public async Task DisconnectAllOwned(CancellationToken ct)
    {
        lock (policyGate) { state.Epoch++; state.ConnectionsStopped = true; foreach (var a in state.Approvals.Values) a.Enabled = false; Save(); }
        await Contain(ct);
    }
    private void Save() => Wire.Save(file, state);
    public void Dispose() { instanceLock.Dispose(); operationGate.Dispose(); }
    private static string Fingerprint(Profile p) => Convert.ToHexString(SHA256.HashData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(p, Wire.Json)));
    public sealed class State
    {
        public Dictionary<string, Approval> Approvals { get; set; } = [];
        public Dictionary<string, string[]> PreviousServerAccounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public bool Quarantined { get; set; }
        public string IncidentId { get; set; } = "";
        public string Reason { get; set; } = "";
        public long Epoch { get; set; }
        public bool ConnectionsStopped { get; set; }
        public int AssignmentRevision {get;set;}=-1;public bool Managed {get;set;} public bool ManagementPaused {get;set;} public int PolicyRevision {get;set;}=-1; public string PolicyFingerprint {get;set;}="";public string Department {get;set;}="";public string ExpectedAccount {get;set;}="";public DateTimeOffset PolicyUntil {get;set;} public DateTimeOffset PolicyIssuedAt {get;set;}
    }
    public sealed class Approval
    {
        public Profile Profile { get; set; } = new("", "", "");
        public bool Authorized {get;set;} public bool AccountMismatch {get;set;} public string ExpectedAccount {get;set;}="";
        public bool Reconnect { get; set; }
        public bool ForceDisconnect { get; set; }
        public bool Owned { get; set; }
        public bool Pending { get; set; }
        public bool Enabled { get; set; }
        public int Failures { get; set; }
        public DateTimeOffset NextAttempt { get; set; }
        public bool NeedsCommand {get;set;}
        public bool AuthRejected {get;set;}
        public string Status { get; set; } = "";
    }
}

internal static class NativeMapping
{
    public static (int Code, string Remote) Get(string drive)
    {
        var size = 32768; var text = new StringBuilder(size);
        var code = WNetGetConnection(drive, text, ref size); return (code, text.ToString());
    }
    public static bool Same(string left, string right) => string.Equals(left.TrimEnd('\\'), right.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    public static bool DeviceAssigned(string drive)
    {
        var buffer = new StringBuilder(32768);
        return QueryDosDevice(drive, buffer, buffer.Capacity) != 0 || Marshal.GetLastWin32Error() != 2;
    }
    public static bool Accessible(string drive) { var attributes = GetFileAttributes(drive + "\\"); return attributes != uint.MaxValue && (attributes & 0x10) != 0; }
    public static string Account(string drive){var length=512;var value=new StringBuilder(length);var code=WNetGetUser(drive,value,ref length);return code==0?value.ToString():"확인 불가 ("+code+")";}
    [DllImport("mpr.dll",CharSet=CharSet.Unicode)] private static extern int WNetGetUser(string name,StringBuilder user,ref int length);
    public static int Connect(string? drive, string unc, ShareCredential? credential = null, bool remember = false)
    {
        var resource = new Resource { Type = 1, LocalName = drive, RemoteName = unc };
        // Current user's Windows/SMB credentials. No passwords in args, logs, or management APIs.
        return WNetAddConnection2(ref resource, credential?.Password, credential?.Account, remember ? 1 : 0);
    }
    public static int Disconnect(string drive, bool force, bool forgetRemembered = false) => WNetCancelConnection2(drive, forgetRemembered ? 1 : 0, force);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Resource { public int Scope, Type, DisplayType, Usage; public string? LocalName, RemoteName, Comment, Provider; }
    [DllImport("mpr.dll", CharSet = CharSet.Unicode)] private static extern int WNetGetConnection(string name, StringBuilder remote, ref int length);
    [DllImport("mpr.dll", CharSet = CharSet.Unicode)] private static extern int WNetAddConnection2(ref Resource resource, string? password, string? user, int flags);
    [DllImport("mpr.dll", CharSet = CharSet.Unicode)] private static extern int WNetCancelConnection2(string name, int flags, [MarshalAs(UnmanagedType.Bool)] bool force);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint QueryDosDevice(string name, StringBuilder path, int length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFileAttributes(string path);
}
