using System.Diagnostics;
using System.Reflection;
using SmbManager.Local;
namespace SmbManager.Local.AdminDesktop;
internal sealed class AdminUpdateDialog:Form {
 readonly AdminGithubUpdate updater=new();readonly CancellationTokenSource stop=new();
 readonly CheckBox beta=new(){Text="베타 릴리즈 포함",AutoSize=true};
 readonly TextBox detail=new(){Dock=DockStyle.Fill,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical};
 readonly Button check=new(){Text="업데이트 확인"},download=new(){Text="관리자 설치 ZIP 저장",Enabled=false},openFolder=new(){Text="저장 폴더 열기",Enabled=false};
 readonly AdminReleaseVersion? installed;AdminGithubRelease? release;string? saved;bool busy;
 public AdminUpdateDialog(bool preview=false){
  var assembly=typeof(AdminUpdateDialog).Assembly;var info=assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion??"";
  var version=info.Split('+')[0];var suffix=info.Split('+').Skip(1).FirstOrDefault()??"";var buildMatch=System.Text.RegularExpressions.Regex.Match(suffix,@"\Aqa\.(\d+)(?:\.|\z)");var build=buildMatch.Success&&int.TryParse(buildMatch.Groups[1].Value,out var number)?number:0;installed=AdminReleaseVersion.Parse(version,build);
  Text="SMB Manager — 관리자 업데이트";UiTheme.Form(this,new(780,650),new(620,520));beta.Checked=version.Contains('-');
  var root=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new(18),ColumnCount=1,RowCount=4};root.RowStyles.Add(new(SizeType.AutoSize));root.RowStyles.Add(new(SizeType.AutoSize));root.RowStyles.Add(new(SizeType.Percent,100));root.RowStyles.Add(new(SizeType.AutoSize));Controls.Add(root);
  var hint=UiTheme.Hint($"현재 관리자: {version} · 빌드 {build}\nGitHub의 관리자용 암호 ZIP만 확인합니다. 회사 서버·Drive 암호는 전송하지 않습니다.");root.Controls.Add(hint,0,0);UiTheme.Wrap(hint,root);
  var actions=new FlowLayoutPanel{Dock=DockStyle.Fill,AutoSize=true,WrapContents=true};actions.Controls.Add(beta);foreach(var button in new[]{check,download,openFolder}){UiTheme.Button(button,button==check);actions.Controls.Add(button);}root.Controls.Add(actions,0,1);root.Controls.Add(detail,0,2);
  var instructions=UiTheme.Hint("설치 순서: ZIP 저장 → 별도로 받은 암호로 압축 해제 → 공유폴더 작업 저장 → 관리 서버 완전 종료 → MSI 설치 → 관리자 다시 실행.\n설치 시 현재 Windows 사용자의 기존 SMB 연결이 정리됩니다. 서버 중단 동안 직원 PC의 연결이 회수될 수 있으므로 업무가 끝난 뒤 진행하세요.");root.Controls.Add(instructions,0,3);UiTheme.Wrap(instructions,root);
  beta.CheckedChanged+=(_,_)=>{release=null;download.Enabled=false;detail.Text="선택한 채널로 업데이트 확인을 눌러주세요.";};
  check.Click+=async(_,_)=>await Run(async()=>{
   release=null;detail.Text="GitHub 릴리즈 확인 중…";var found=await updater.Latest(beta.Checked,stop.Token);if(IsDisposed)return;
   if(found is null){detail.Text="선택한 채널에 검증 가능한 관리자용 배포 파일이 없습니다. 직원용·이전 1.7 설치본은 관리자 업데이트로 사용하지 않습니다.";return;}
   if(installed is null)throw new InvalidOperationException("현재 관리자 버전을 확인할 수 없어 자동 비교를 중지했습니다.");
   var comparison=found.Version.CompareTo(installed);release=comparison>0?found:null;
   detail.Text=(comparison>0?"새 관리자 업데이트가 있습니다.":comparison==0?"최신 관리자 버전입니다.":"현재 설치된 관리자 버전이 더 최신입니다.")+$"\r\n\r\n{found.Tag} · 빌드 {found.Version.Build}\r\n{found.FileName}\r\n{found.Size/1048576d:F1} MB\r\n\r\n{found.Notes}";
  });
  download.Click+=async(_,_)=>await Run(async()=>{
   var chosen=release??throw new InvalidOperationException("업데이트를 다시 확인하세요.");using var dialog=new SaveFileDialog{FileName=chosen.FileName,Filter="암호 ZIP (*.zip)|*.zip",OverwritePrompt=true};if(dialog.ShowDialog(this)!=DialogResult.OK)return;
   detail.Text="다운로드·SHA-256 검증 중…";await updater.Download(chosen,dialog.FileName,new Progress<int>(p=>{if(!IsDisposed)detail.Text=$"관리자 설치 ZIP 다운로드·검증 중… {p}%";}),stop.Token);if(IsDisposed)return;
   saved=dialog.FileName;openFolder.Enabled=true;detail.Text="다운로드 및 SHA-256 검증 완료\r\n"+saved+"\r\n\r\n별도로 받은 배포 암호로 압축을 푼 뒤, 아래 순서에 따라 설치하세요. 기존 설정 폴더를 삭제하지 마세요. 현재 관리 서버는 계속 실행 중입니다.";
  });
  openFolder.Click+=(_,_)=>{try{if(saved is not null&&File.Exists(saved))Process.Start(new ProcessStartInfo("explorer.exe"){UseShellExecute=true,ArgumentList={"/select,"+saved}});}catch{detail.Text="저장 폴더를 열지 못했습니다. 위 파일 경로에서 직접 확인하세요.";}};
  FormClosing+=(_,_)=>stop.Cancel();FormClosed+=(_,_)=>{if(!busy){updater.Dispose();stop.Dispose();}};
  if(preview)detail.Text="관리자 업데이트 화면 검사\r\nGitHub에서 최신 관리자 릴리즈를 확인하고 무결성 검증 후 암호 ZIP을 저장합니다.";
  else Shown+=(_,_)=>check.PerformClick();
 }
 async Task Run(Func<Task> operation){if(busy)return;busy=true;beta.Enabled=check.Enabled=download.Enabled=false;try{await operation();}catch(OperationCanceledException){if(!IsDisposed)detail.Text="업데이트 작업을 취소했습니다.";}catch(Exception e){if(!IsDisposed)detail.Text="업데이트 작업 실패\r\n"+UiTheme.Error(e)+"\r\n기존 관리자와 저장 설정은 유지됩니다.";}finally{busy=false;if(!IsDisposed){beta.Enabled=check.Enabled=true;download.Enabled=release is not null;}else{updater.Dispose();stop.Dispose();}}}
}
