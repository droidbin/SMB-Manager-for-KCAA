using System.Text.Json.Serialization;

namespace SmbManager.Local;

// An allowlist schema: no free-form text, identities, addresses, paths or exception messages.
public enum DiagnosticCode { Started, StateChanged, PolicyApplied, PolicyRejected, ManagementUnavailable, ConnectionsChanged, CommandSucceeded, CommandFailed }
public enum DiagnosticPhase { Unknown, Starting, Ready, WaitingApproval, Offline, Paused, Quarantined, RegistrationLost, Running, Succeeded, Failed }
public enum DiagnosticFailure { None, Network, Authentication, PolicyValidation, Storage, Unexpected, Configuration }
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ClientDiagnosticEvent(Guid EventId, DateTimeOffset OccurredUtc, DiagnosticCode Code, DiagnosticPhase Phase,
    DiagnosticFailure Failure, string AppVersion, int WindowsBuild, int ConnectedCount, int OtherConnectionCount);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ClientDiagnosticBatch(ClientDiagnosticEvent[] Events);
public sealed record ClientDiagnosticRow(string DeviceAlias, DateTimeOffset ReceivedUtc, ClientDiagnosticEvent Event);
public sealed record ClientDiagnosticView(string DeviceAlias, int RetentionDays, ClientDiagnosticRow[] Entries);
public sealed record ClientDiagnosticAck(Guid[] Accepted);

public static class DiagnosticPrivacy
{
    public const int BatchLimit=20, DeviceLimit=200, TotalLimit=10000, RetentionDays=7;
    public static DateTimeOffset Minute(DateTimeOffset value) => new(value.UtcTicks-value.UtcTicks%TimeSpan.TicksPerMinute,TimeSpan.Zero);
    public static bool Valid(ClientDiagnosticEvent? e) => e is not null && e.EventId!=Guid.Empty && Enum.IsDefined(e.Code)
        && Enum.IsDefined(e.Phase) && Enum.IsDefined(e.Failure) && e.AppVersion is { Length: <= 32 }
        && Version.TryParse(e.AppVersion,out var version) && version.Build>=0 && version.Revision>=0 && version.ToString(4)==e.AppVersion
        && e.WindowsBuild is > 0 and <= 99999 && e.ConnectedCount is >= 0 and <= 26 && e.OtherConnectionCount is >= 0 and <= 26
        && e.ConnectedCount+e.OtherConnectionCount<=26 && e.OccurredUtc.Offset==TimeSpan.Zero
        && e.OccurredUtc.Ticks%TimeSpan.TicksPerMinute==0;
    public static string Description(DiagnosticCode code) => code switch {
        DiagnosticCode.Started=>"클라이언트 시작", DiagnosticCode.StateChanged=>"클라이언트 상태 변경",
        DiagnosticCode.PolicyApplied=>"관리 배정 적용", DiagnosticCode.PolicyRejected=>"관리 배정 확인 실패",
        DiagnosticCode.ManagementUnavailable=>"관리 서버 통신 실패", DiagnosticCode.ConnectionsChanged=>"폴더 연결 상태 변경",
        DiagnosticCode.CommandSucceeded=>"관리 작업 완료", DiagnosticCode.CommandFailed=>"관리 작업 실패", _=>"알 수 없는 이벤트" };
    public static string PhaseLabel(DiagnosticPhase phase)=>phase switch{
        DiagnosticPhase.Starting=>"시작 중",DiagnosticPhase.Ready=>"준비됨",DiagnosticPhase.WaitingApproval=>"승인 대기",DiagnosticPhase.Offline=>"서버 연결 끊김",DiagnosticPhase.Paused=>"일시 중지",DiagnosticPhase.Quarantined=>"보호 잠금",DiagnosticPhase.RegistrationLost=>"등록 확인 필요",DiagnosticPhase.Running=>"처리 중",DiagnosticPhase.Succeeded=>"완료",DiagnosticPhase.Failed=>"실패",_=>"확인 필요"};
    public static string FailureLabel(DiagnosticFailure failure)=>failure switch{
        DiagnosticFailure.None=>"없음",DiagnosticFailure.Network=>"네트워크",DiagnosticFailure.Authentication=>"인증",DiagnosticFailure.PolicyValidation=>"배정 검증",DiagnosticFailure.Storage=>"저장소",DiagnosticFailure.Configuration=>"설정 충돌",_=>"기타"};
}
