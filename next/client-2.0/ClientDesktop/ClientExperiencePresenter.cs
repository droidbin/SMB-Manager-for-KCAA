using SmbManager.Local;
namespace SmbManager.Local.ClientDesktop;
internal sealed record ClientExperience(string Title,string Detail,string Tone="neutral",string Action="",string ActionText="");
internal static class ClientExperiencePresenter {
 public static ClientExperience Describe(string phase,bool archived,bool quarantined,bool paused,DepartmentPolicy? policy,ConnectionReport[] reports,string policyError="",bool restoring=false,DateTimeOffset? verifiedUtcNow=null){
  if(phase=="RegistrationLost")return new("PC 등록을 다시 확인해야 합니다","회사 서버에서 이 PC의 인증을 거부했습니다. 설정의 등록 복구에서 재등록을 요청하거나 담당자에게 문의하세요.","warning","settings","설정에서 등록 복구");
  if(archived)return new("이 PC의 사용이 중지되어 있습니다","회사 담당자에게 PC 등록 상태를 확인해 주세요.","warning","help","담당자에게 문의");
  if(quarantined)return new("보호를 위해 공유폴더 연결을 잠갔습니다","보호 상태는 담당자가 확인한 뒤 해제할 수 있습니다.","danger","help","담당자에게 문의");
  if(policy?.ConnectionsHeld==true)return new("회사에서 연결을 잠시 중지했습니다","다시 연결할 수 있는 시점은 회사 담당자에게 문의해 주세요.","warning","help","담당자에게 문의");
  if(paused)return new("자동 연결이 일시 중지되어 있습니다","회사 담당자에게 연결 상태를 확인해 주세요.","warning","help","담당자에게 문의");
  if(phase=="WaitingApproval")return new("등록 요청을 보냈습니다","담당자가 승인하면 폴더가 자동으로 연결됩니다. 추가 입력 없이 기다려 주세요.","neutral");
  var connected=reports.Count(r=>r.ConnectionState=="Connected");
  if(phase=="Offline")return new("회사 관리 서버 연결을 확인해 주세요",connected>0?$"폴더 {connected}개에서 연결 상태가 보고됐습니다. 각 폴더의 사용 가능 여부도 함께 확인해 주세요.":"사내망에 연결되어 있는지 확인하세요. 연결이 복구되면 폴더를 자동으로 확인합니다.","warning","server","서버 연결 확인");
  if(policyError.Length>0)return new("회사 연결 정보를 적용하지 못했습니다",policyError,"warning","server","배정 정보 다시 확인");
  if(policy is null)return new("회사 연결 정보를 확인하고 있습니다","잠시 기다려 주세요. 연결이 되지 않으면 서버 연결을 다시 확인하세요.","neutral","server","서버 연결 확인");
  if(policy.ExpiresAt<=(verifiedUtcNow??DateTimeOffset.UtcNow))return new("회사 연결 정보를 다시 확인해야 합니다","새 연결 정보가 확인될 때까지 폴더 열기와 재연결을 잠시 보류합니다.","warning","server","서버 연결 확인");
  if(policy.Profiles.Length==0)return new("공유폴더 배정을 기다리고 있습니다","담당자가 폴더를 배정하면 자동으로 연결됩니다.","neutral","help","담당자에게 문의");
  if(reports.Any(r=>r.ConnectionState=="CredentialsMissing"))return new("Drive 계정이 배정되지 않았습니다","회사 서버 등록은 완료됐습니다. 담당자가 해당 부서의 서버 계정과 암호를 저장하면 자동으로 연결됩니다.","warning","server","배정 정보 다시 확인");
  if(reports.Any(r=>r.ConnectionState=="AuthenticationRequired"))return new("회사 폴더 계정 확인이 필요합니다","반복 연결 시도를 멈췄습니다. 담당자가 계정과 암호를 확인해야 합니다.","warning","help","담당자에게 문의");
  if(reports.Any(r=>r.ConnectionState=="AccountMismatch"))return new("이전 부서의 연결 정보가 남아 있습니다","공유폴더에서 작업 중인 파일을 저장하고 닫은 뒤 부서 연결을 갱신하세요.","warning","switch","부서 연결 갱신");
  if(reports.Any(r=>r.ConnectionState=="ChangePending"))return new("공유폴더 변경을 기다리고 있습니다","기존 드라이브에서 작업 중인 파일을 저장하고 탐색기 창을 닫아 주세요. 닫으면 새 배정으로 자동 연결합니다.","warning","help","담당자에게 문의");
  if(reports.Any(r=>r.ConnectionState=="Conflict"))return new("다른 드라이브와 충돌하고 있습니다","다른 연결을 임의로 끊지 않았습니다. 담당자에게 드라이브 배정을 확인해 주세요.","warning","help","담당자에게 문의");
  if(reports.Any(r=>r.ConnectionState=="ReconciliationRequired"))return new("이전 연결 작업을 확인해야 합니다","안전한 확인을 위해 새 연결을 보류했습니다. 담당자에게 문의해 주세요.","warning","help","담당자에게 문의");
  if(connected==policy.Profiles.Length&&reports.Length==policy.Profiles.Length)return new($"공유폴더 {connected}개가 연결되었습니다","아래에서 필요한 폴더의 열기 버튼을 누르세요.","success");
  if(restoring)return new("회사 폴더 연결을 복원하고 있습니다","배정된 폴더를 확인하고 자동으로 연결합니다. 추가 입력 없이 잠시 기다려 주세요.","neutral");
  if(reports.Any(r=>r.ConnectionState is "Disconnected" or "Unavailable"))return new($"공유폴더 {Math.Max(1,policy.Profiles.Length-connected)}개를 연결하지 못했습니다","자동 연결을 확인하고 있습니다. 다시 연결을 누르거나 문제가 계속되면 담당자에게 문의하세요.","warning","retry","연결 안 된 폴더 다시 연결");
  return new("공유폴더 연결을 준비하고 있습니다","폴더의 연결 상태가 확인되면 이 화면에 표시됩니다.","neutral");
 }
 public static string FolderState(string state)=>state switch{"Connected"=>"연결됨","Restoring"=>"자동 연결 중","Disconnected"=>"연결 끊김","Unavailable"=>"접근 확인 필요","CredentialsMissing"=>"Drive 계정 배정 필요","AuthenticationRequired"=>"담당자 계정 확인 필요","AccountMismatch"=>"부서 연결 갱신 필요","Conflict"=>"드라이브 충돌","ReconciliationRequired"=>"이전 연결 확인 필요","ChangePending"=>"변경 대기 · 파일 저장 후 닫기",_=>"연결 상태 확인 중"};
}
