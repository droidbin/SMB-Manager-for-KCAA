using System.Net.Http.Json;
using SmbManager.Local;
namespace SmbManager.Local.ClientDesktop;
internal sealed class ClientSettingsDialog:Form {
 readonly TextBox address=new(){Dock=DockStyle.Fill,AccessibleName="회사 서버 주소"};
 readonly Label result=UiTheme.Hint("서버 주소는 회사 담당자가 안내한 HTTPS 주소를 입력하세요.");
 public event Func<string,Task>? CheckServerRequested;
 public event Func<bool,bool>? DiagnosticsChanged;
 public string ServerAddress=>Wire.LocalUrl(address.Text.Trim()).AbsoluteUri;
 public string Action {get;private set;}="save";
 public ClientSettingsDialog(string server,bool registered,bool pending,bool registrationLost=false,bool diagnosticsEnabled=true){
  Text="설정";UiTheme.Form(this,new(660,760),new(540,640));
  var root=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new(18),ColumnCount=1,AutoScroll=true};Controls.Add(root);
  root.Controls.Add(UiTheme.Hint("회사 서버"));address.Text=server;root.Controls.Add(address);root.Controls.Add(result);UiTheme.Wrap(result,root);
  var check=ClientForm.ActionButton("입력한 서버 확인");root.Controls.Add(check);
  check.Click+=async(_,_)=>{check.Enabled=false;try{var url=ServerAddress;if(CheckServerRequested is not null)await CheckServerRequested(url);result.Text="회사 서버 응답을 확인했습니다. 저장을 누르면 적용됩니다.";}catch(Exception e){result.Text=UiTheme.Error(e);}finally{check.Enabled=true;}};
  var save=ClientForm.ActionButton("서버 설정 저장",true);save.Enabled=!pending;root.Controls.Add(save);save.Click+=(_,_)=>{try{_=ServerAddress;Action="save";DialogResult=DialogResult.OK;Close();}catch(Exception e){result.Text=UiTheme.Error(e);}};
  var info=ClientForm.ActionButton("성명·부서 변경 요청");info.Enabled=registered&&!pending;root.Controls.Add(info);info.Click+=(_,_)=>{Action="info";DialogResult=DialogResult.OK;Close();};
  var retry=ClientForm.ActionButton("현재 서버·배정 다시 확인");retry.Enabled=!pending;root.Controls.Add(retry);retry.Click+=(_,_)=>{Action="retry";DialogResult=DialogResult.OK;Close();};
  var recover=ClientForm.ActionButton("등록 복구 · 재등록 요청");recover.Enabled=registrationLost&&!pending;root.Controls.Add(recover);recover.Click+=(_,_)=>{Action="registration";DialogResult=DialogResult.OK;Close();};
  var hint=UiTheme.Hint(pending?"설치 마무리를 먼저 완료해 주세요.":"부서 변경은 담당자 승인 후 적용됩니다. 공유폴더 계정과 암호는 회사에서 관리합니다.");UiTheme.Wrap(hint,root);root.Controls.Add(hint);
  var diagnostics=new CheckBox{Text="최소 진단 로그 수집",AutoSize=true,Checked=diagnosticsEnabled,Enabled=registered};root.Controls.Add(diagnostics);
  var privacy=UiTheme.Hint("오류 코드·상태·앱 버전·Windows 빌드·연결 개수만 관리자에게 전송합니다. 성명·계정·IP·MAC·폴더 경로·파일 내용·암호는 진단 로그에 넣지 않습니다. 서버에는 가명 코드로 최대 7일 보관합니다. 변경은 즉시 적용되며 끄면 대기 로그를 삭제합니다. 이미 전송 중이거나 저장된 로그는 관리자가 삭제하거나 보관 기한 후 삭제됩니다.");root.Controls.Add(privacy);UiTheme.Wrap(privacy,root);
  bool changingDiagnostics=false;var savedDiagnostics=diagnosticsEnabled;
  diagnostics.CheckedChanged+=(_,_)=>{if(changingDiagnostics)return;var next=diagnostics.Checked;if(DiagnosticsChanged?.Invoke(next)==true){savedDiagnostics=next;result.Text=next?"최소 진단 수집을 켰습니다.":"진단 수집을 끄고 대기 로그를 삭제했습니다.";}else{changingDiagnostics=true;diagnostics.Checked=savedDiagnostics;changingDiagnostics=false;result.Text="진단 설정을 저장하지 못했습니다. 저장소를 확인하세요. 폴더 연결은 계속 사용할 수 있습니다.";}};
  var cancel=ClientForm.ActionButton("닫기");cancel.DialogResult=DialogResult.Cancel;root.Controls.Add(cancel);CancelButton=cancel;
 }
}
