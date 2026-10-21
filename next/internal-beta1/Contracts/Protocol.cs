using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SmbManager.Local;

public static class Wire
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static string Secret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public static bool EqualSecret(string? a, string b) => a is not null && CryptographicOperations.FixedTimeEquals(
        SHA256.HashData(Encoding.UTF8.GetBytes(a)), SHA256.HashData(Encoding.UTF8.GetBytes(b)));
    public static Uri LocalUrl(string text)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || !(uri.Scheme == "https" || (uri.Scheme == "http" && (uri.Host == "127.0.0.1" || (uri.Host == "10.77.30.1" && uri.Port == 48652 && Environment.GetEnvironmentVariable("SMB_ALPHA_VM_LAB") == "1" && Environment.MachineName == "SMBALPHA-CLIENT" && File.Exists(@"C:\Lab\single-vm.json")))))
            || uri.AbsolutePath != "/" || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidOperationException("서버는 HTTPS 루트 주소를 사용하세요. HTTP는 로컬 시험에만 허용됩니다.");
        return uri;
    }
    public static HttpClient Http(string url, string? admin = null, string? device = null)
    {
        var address = LocalUrl(url);
        if (admin is not null && !address.IsLoopback) throw new InvalidOperationException("관리 API는 서버 PC에서만 사용할 수 있습니다.");
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = address, Timeout = TimeSpan.FromSeconds(5) };
        if (admin is not null) client.DefaultRequestHeaders.Add("X-Admin-Key", admin);
        if (device is not null) {
            client.DefaultRequestHeaders.Authorization = new("Bearer", device);
            client.DefaultRequestHeaders.Add("X-Device-Hardware", Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(HardwareIdentity.Capture(), Json)));
        }
        return client;
    }
    public static async Task<T> Read<T>(HttpResponseMessage response, CancellationToken ct = default)
    {
        using (response)
        {
            if (!response.IsSuccessStatusCode) {
                string code="";int retry=60;
                try{var error=await response.Content.ReadFromJsonAsync<JsonElement>(Json,ct);if(error.TryGetProperty("error",out var value))code=value.GetString()??"";if(error.TryGetProperty("retryAfterSeconds",out var seconds))retry=seconds.GetInt32();}catch(JsonException){}catch(InvalidOperationException){}
                var message=code switch {
                    "SETTINGS_CHANGED" or "ASSIGNMENT_CHANGED"=>"다른 변경이 먼저 저장됐습니다. 화면을 다시 열어 최신 설정을 확인하세요.",
                    "TEMPLATE_REQUIRED"=>"먼저 해당 부서의 기본 서버·폴더 설정을 저장하세요.",
                    "INDIVIDUAL_ACCOUNT_REVIEW_REQUIRED"=>"개별 설정 PC의 기본 계정과 다릅니다. 영향 목록을 확인하고 개별 설정 포함 여부를 선택하세요.",
                    "SERVER_ACCOUNT_BELONGS_TO_OTHER_DEPARTMENT" or "ACCOUNT_BELONGS_TO_OTHER_DEPARTMENT" or "DEPARTMENT_ACCOUNT_BINDING_MISMATCH"=>"다른 부서의 지정 계정과 충돌합니다. 부서별 계정을 확인하세요.",
                    "DRIVE_CONFLICT" or "DRIVE_CONFLICT_OR_INVALID_TEMPLATE" or "DEPARTMENT_OR_DRIVE_MISMATCH" or "ASSIGNED_DRIVE_CONFLICT" or "EXECUTIVE_DRIVE_CONFLICT"=>"선택한 공유폴더의 드라이브 문자가 겹치거나 입력 형식이 올바르지 않습니다. 폴더별 드라이브와 계정을 확인하세요.",
                    "DUPLICATE_UNC"=>"같은 공유폴더가 이미 등록되어 있습니다. 기존 항목을 선택하세요.",
                    "REGISTRATION_RETRY"=>$"요청이 연속으로 접수됐습니다. 승인 대기 여부를 확인하고 약 {retry}초 뒤 다시 시도하세요.",
                    _=>response.StatusCode switch { HttpStatusCode.TooManyRequests=>"요청이 많습니다. 잠시 기다린 뒤 다시 시도하세요.",HttpStatusCode.Conflict=>"현재 상태에서는 처리할 수 없습니다. 최신 등록·배정 상태를 확인하세요.",HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden=>"승인 또는 연결 권한을 확인할 수 없습니다. 서버관리자에게 문의하세요.",HttpStatusCode.BadRequest=>"입력 내용을 확인하세요. 부서·계정·폴더 경로를 다시 확인해 주세요.",_=>"회사 서버에서 요청을 완료하지 못했습니다. 잠시 후 다시 시도하거나 서버관리자에게 문의하세요."}
                };
                throw new HttpRequestException(message+$" (HTTP {(int)response.StatusCode})",null,response.StatusCode);
            }
            return await response.Content.ReadFromJsonAsync<T>(Json, ct) ?? throw new InvalidOperationException("서버 응답이 비어 있습니다.");
        }
    }
    public static void Save<T>(string file, T value)
    {
        AtomicWrite(file, JsonSerializer.SerializeToUtf8Bytes(value, Json));
    }
    public static void AtomicWrite(string file, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { stream.Write(bytes); stream.Flush(true); }
            // Windows file scanners/indexers can briefly open an existing destination without
            // FILE_SHARE_DELETE. Retry only the replace operation; retain the old file until
            // replacement succeeds, and still fail closed on persistent ACL/disk failures.
            var delays = new[] { 20, 40, 80, 160, 320, 400, 400 };
            for (var attempt = 0; ; attempt++)
            {
                try { File.Move(temp, file, true); break; }
                catch (Exception e) when (OperatingSystem.IsWindows() && attempt < delays.Length &&
                    e is IOException or UnauthorizedAccessException && (e.HResult & 0xffff) is 5 or 32 or 33)
                { Thread.Sleep(delays[attempt]); }
            }
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static T Load<T>(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return JsonSerializer.Deserialize<T>(stream, Json) ?? throw new InvalidOperationException("설정 파일이 비어 있습니다.");
    }
    public static string Arg(string[] args, string name, string? fallback = null)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback ?? throw new ArgumentException(name + " 인수가 필요합니다.");
    }
}

public record ServerReady(string Url, string AdminKey, int ProcessId, string Mode = "LoopbackSimulationOnly");
public record EnrollmentCode(string Code, DateTimeOffset ExpiresAt);
public record EnrollRequest(string Code, string Name, string User, string SessionId, HardwareIdentity? Hardware = null, string ComputerName = "");
public record EnrollReply(string DeviceId, string DeviceKey, string PublicKey);
public record ClientBootstrap(string Url, string Code, string Name, string Department = "");
public record Identity(string Url, string DeviceId, string DeviceKey, string PublicKey, HardwareIdentity? Hardware = null, string ComputerName = "", string Name = "", string Department = "");
public record Heartbeat(string SessionId, bool Paused, string[] VisibleDriveLetters, bool PushConnected = false, bool Quarantined = false, string IncidentId = "", ConnectionReport[]? Connections = null, int AppliedPolicyRevision = -1, string ComputerName = "");
public record DeviceView(string Id, string Name, string User, string SessionId, bool Approved, bool Paused,
    DateTimeOffset LastSeen, bool Online, string[] DriveLetters, bool PushConnected = false, bool Quarantined = false, string IncidentId = "", ConnectionReport[]? Connections = null, int AppliedPolicyRevision = -1, DeviceAssignment? Assignment = null, HardwareIdentity? Hardware = null, string RequestedDepartment = "", string ComputerName = "", DateTimeOffset RequestedAt = default, RegistrationCorrection? Correction = null, bool Archived = false, bool ConnectionsHeld = false);
public record Profile(string Id, string Name, string Mode, int Revision = 1, string Unc = "", string Drive = "", string Department = "");
public record SaveProfile(string Name, string Unc, string Drive, int ExpectedRevision = 0, string Department = "");
public record CreateCommand(string DeviceId, string ProfileId, int TtlSeconds = 60, string Operation = "ConnectProfile", string Text = "");
public record CommandPayload(string Id, string DeviceId, string SessionId, string ProfileId, int ProfileRevision,
    string Mode, DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt, string Operation = "ConnectProfile", string Text = "", Profile? TargetProfile = null);
public record Envelope(CommandPayload Payload, string Signature);
public record Lease(Envelope Envelope, string LeaseId);
public record LeaseReply(Lease? Lease);
public record ResultRequest(string LeaseId, string State, string Detail, string Mode = "Simulation");
public record CommandView(string Id, string DeviceId, string DeviceName, string ProfileId, string State,
    string Detail, DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt);
public record Receipt(string Id, string State, string Detail);
public record LocalState(string DeviceId, string Name, string Phase, string Message, string[] SimulatedProfiles,
    int Completed, DateTimeOffset UpdatedAt, bool PushConnected = false, bool Quarantined = false, string[]? Notifications = null);

public static class ProfileRules
{
    public static bool Available(Profile? profile)=>profile is not null && profile.Mode!="Disabled" && Valid(profile.Name,profile.Unc,profile.Drive);
    public static bool Valid(string? name, string? unc, string? drive)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80 || unc is null || unc.Length > 512 || drive is null) return false;
        if (!System.Text.RegularExpressions.Regex.IsMatch(drive, "^[A-Z]:$")) return false;
        if (!unc.StartsWith(@"\\") || unc.Contains('/') || unc.Any(char.IsControl)) return false;
        var pieces = unc[2..].Split('\\');
        return pieces.Length >= 2 && pieces.All(p => p.Length > 0 && p is not "." and not ".." && p.IndexOfAny([':', '*', '?', '"', '<', '>', '|', '@']) < 0)
            && pieces[0] is not "." and not "?" && !pieces[1].EndsWith('$');
    }
}

public static class Signature
{
    public static byte[] Bytes(CommandPayload payload) => JsonSerializer.SerializeToUtf8Bytes(payload, Wire.Json);
    public static string Sign(RSA rsa, CommandPayload payload) => Convert.ToBase64String(rsa.SignData(Bytes(payload),
        HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
    public static bool Verify(string publicKey, Envelope envelope)
    {
        try
        {
            using var rsa = RSA.Create(); rsa.ImportFromPem(publicKey);
            return rsa.VerifyData(Bytes(envelope.Payload), Convert.FromBase64String(envelope.Signature),
                HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        }
        catch (Exception e) when (e is CryptographicException or FormatException or ArgumentException) { return false; }
    }
}

public record SaveAssignment(string Department, string ExpectedAccount, string[] ProfileIds, bool Reconnect = true, bool ForceDisconnect = false, int ExpectedRevision = 0, bool ResumeStopped = false, string AccessRole = "직원", bool FollowDepartment = false);
public record DeviceAssignment(string Department, string ExpectedAccount, string[] ProfileIds, bool Reconnect, bool ForceDisconnect, int Revision, bool ResumeStopped = false, string AccessRole = "직원", bool FollowDepartment = false);
public record DepartmentPolicy(string DeviceId, string SessionId, int Revision, string Department, string ExpectedAccount, Profile[] Profiles, bool Reconnect, bool ForceDisconnect, bool ResumeStopped, DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt, int AssignmentRevision = 0, ProfileAccount[]? Accounts = null, string AccessRole = "직원", [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] bool ConnectionsHeld = false);
public record PolicyEnvelope(DepartmentPolicy Payload, string Signature);
public record ConnectionReport(string ProfileId, string Drive, string Unc, string Account, string AssignmentState, bool AppOwned, string EffectiveAccess = "미검증: 공유폴더 권한 확인 필요", string ConnectionState = "Unknown");
public static class AssignmentRules
{
    public static bool ValidDepartment(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 64 && !value.Any(char.IsControl);
    public static bool ValidAccount(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 160 && !value.Any(char.IsControl)
        && ((value.Split('\\') is [var domain, var user] && domain.Trim().Length > 0 && user.Trim().Length > 0) || (value.Split('@') is [var userName, var realm] && userName.Trim().Length > 0 && realm.Trim().Length > 0));
    public static bool DepartmentAllows(string department, Profile profile) => ValidDepartment(department) && (profile.Department == department || profile.Department == "공용");
    public static string Sign(RSA rsa, DepartmentPolicy policy) => Convert.ToBase64String(rsa.SignData(JsonSerializer.SerializeToUtf8Bytes(policy, Wire.Json), HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
    public static bool Verify(Identity identity, string session, PolicyEnvelope envelope)
    {
        try {
            var p = envelope.Payload; using var rsa = RSA.Create(); rsa.ImportFromPem(identity.PublicKey);
            return p.DeviceId == identity.DeviceId && p.SessionId == session && p.IssuedAt <= DateTimeOffset.UtcNow.AddSeconds(10)
                && p.ExpiresAt > DateTimeOffset.UtcNow && p.ExpiresAt - p.IssuedAt <= TimeSpan.FromSeconds(40)
                && p.Profiles.Length <= 26 && (p.Profiles.Length == 0 || (ValidDepartment(p.Department) && ValidAccount(p.ExpectedAccount)))
                && p.AccessRole is "직원" or "임원" && p.Profiles.All(x => ProfileRules.Available(x))
                && (p.Accounts is null || (p.Accounts.Length==p.Profiles.Length && p.Accounts.Select(a=>a.ProfileId).Distinct().Count()==p.Profiles.Length && p.Accounts.All(a=>p.Profiles.Any(x=>x.Id==a.ProfileId)&&ValidAccount(a.Account)))) && p.Profiles.Select(x=>x.Drive).Distinct().Count()==p.Profiles.Length
                && rsa.VerifyData(JsonSerializer.SerializeToUtf8Bytes(p, Wire.Json), Convert.FromBase64String(envelope.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        } catch(Exception e) when(e is CryptographicException or FormatException or ArgumentException or NullReferenceException){return false;}
    }
}

public record DepartmentAccountChange(string Department,string ExpectedAccount,string NewAccount);

public static class BetaChannels
{
    public const string Open = "open-beta", Closed = "closed-beta";
    public static bool Valid(string? channel) => channel is Open or Closed;
    public static string Label(string channel) => channel == Closed ? "클로즈베타" : "오픈베타";
}
public record BetaSelection(string Channel);
public record BetaAdministrator(bool Administrator);
public record BetaAccess(string Selected, string[] Allowed, string Department, bool Administrator);
public record BetaRelease(string Channel, string Version, string FileName, long Size, string Sha256, string Notes = "", string? FileVersion = null);

public record BulkConnectionRequest(string Operation, string[] DeviceIds);
public record BulkConnectionResult(string DeviceId, bool Accepted, string Detail);
