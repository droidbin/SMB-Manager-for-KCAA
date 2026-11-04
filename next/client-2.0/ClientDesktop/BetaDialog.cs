using System.Diagnostics;
using System.Security.Cryptography;
using SmbManager.Local;
using SmbManager.Local.Client;
namespace SmbManager.Local.ClientDesktop;
internal sealed record ClientUpdateRequest(string File,long Size,string Sha256){
 public void Start(){
  var cache=Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"SMB Manager Internal Beta 1","updates"))+Path.DirectorySeparatorChar;var file=Path.GetFullPath(File);
  if(!file.StartsWith(cache,StringComparison.OrdinalIgnoreCase)||!file.EndsWith(".msi",StringComparison.OrdinalIgnoreCase)||(System.IO.File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0)throw new IOException("업데이트 파일 위치를 확인할 수 없습니다.");
  using(var stream=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read)){if(stream.Length!=Size||!Convert.ToHexString(SHA256.HashData(stream)).Equals(Sha256,StringComparison.OrdinalIgnoreCase))throw new IOException("업데이트 파일이 변경되었습니다. 다시 받아 주세요.");}
  Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"msiexec.exe")){UseShellExecute=true,Arguments="/i \""+file+"\""})?.Dispose();
 }
}
internal sealed class BetaDialog:Form {
 readonly ComboBox channels=new(){DropDownStyle=ComboBoxStyle.DropDownList,Width=240,AccessibleName="업데이트 채널"};
 readonly TextBox notes=new(){Multiline=true,ReadOnly=true,Dock=DockStyle.Fill,ScrollBars=ScrollBars.Vertical,Font=UiTheme.Font(10),AccessibleName="업데이트 변경 내용"};
 readonly Label status=new(){Text="회사 업데이트를 확인하고 있습니다.",AutoSize=true,Font=UiTheme.Font(14,true),Margin=new(0,0,0,10)};
 readonly Label hint=UiTheme.Hint(""),progressText=UiTheme.Hint("");readonly ProgressBar progress=new(){Dock=DockStyle.Top,Style=ProgressBarStyle.Marquee,Height=8,Visible=false};
 readonly Button check=ClientForm.ActionButton("다시 확인"),download=ClientForm.ActionButton("업데이트 받기",true),install=ClientForm.ActionButton("작업을 저장하고 설치",true);
 readonly ClientAgent agent;readonly CancellationTokenSource stop=new();readonly string installed=FileVersionInfo.GetVersionInfo(typeof(BetaDialog).Assembly.Location).FileVersion??"";
 readonly string productVersion=typeof(BetaDialog).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute),false).Cast<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0]??"확인 불가";
 BetaRelease? release;ClientUpdateRequest? prepared;bool busy;
 public ClientUpdateRequest? Installation {get;private set;}
 public BetaDialog(ClientAgent client){
  agent=client;Text="SMB Manager · 업데이트";UiTheme.Form(this,new(740,600),new(640,460));var root=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new(20),ColumnCount=1,RowCount=6};root.RowStyles.Add(new(SizeType.AutoSize));root.RowStyles.Add(new(SizeType.AutoSize));root.RowStyles.Add(new(SizeType.AutoSize));root.RowStyles.Add(new(SizeType.AutoSize));root.RowStyles.Add(new(SizeType.Percent,100));root.RowStyles.Add(new(SizeType.AutoSize));Controls.Add(root);
  root.Controls.Add(status,0,0);UiTheme.Wrap(status,root);hint.Text=$"현재 버전: {productVersion}\n회사가 허용한 업데이트만 확인할 수 있습니다.";root.Controls.Add(hint,0,1);UiTheme.Wrap(hint,root);
  var choices=new FlowLayoutPanel{Dock=DockStyle.Fill,AutoSize=true,WrapContents=true};choices.Controls.Add(channels);choices.Controls.Add(check);root.Controls.Add(choices,0,2);root.Controls.Add(progress,0,3);root.Controls.Add(notes,0,4);
  var bottom=new TableLayoutPanel{Dock=DockStyle.Fill,AutoSize=true,ColumnCount=1};bottom.Controls.Add(progressText);var actions=new FlowLayoutPanel{Dock=DockStyle.Fill,AutoSize=true,WrapContents=true};actions.Controls.Add(download);actions.Controls.Add(install);bottom.Controls.Add(actions);UiTheme.Wrap(progressText,bottom);root.Controls.Add(bottom,0,5);download.Enabled=false;install.Enabled=false;
  channels.SelectedIndexChanged+=(_,_)=>{if(busy)return;release=null;prepared=null;download.Enabled=install.Enabled=false;status.Text="선택한 채널을 확인해 주세요";progressText.Text="다시 확인을 누르면 이 채널의 업데이트를 확인합니다.";};
  check.Click+=async(_,_)=>await Run(Check);download.Click+=async(_,_)=>await Run(Prepare);
  install.Click+=(_,_)=>{if(prepared is null)return;var answer=MessageBox.Show(this,"공유폴더에서 작업 중인 파일을 저장하고 닫아 주세요.\n\n설치 과정에서 이 Windows 사용자의 모든 공유폴더 연결을 해제합니다. 설치 후 앱을 다시 실행하면 배정된 폴더를 자동으로 연결합니다.\n\n작업을 저장했고 설치를 진행할까요?","업데이트 설치 준비",MessageBoxButtons.YesNo,MessageBoxIcon.Information,MessageBoxDefaultButton.Button2);if(answer!=DialogResult.Yes)return;Installation=prepared;DialogResult=DialogResult.OK;Close();};
  Shown+=async(_,_)=>await Run(Check);FormClosing+=(_,_)=>stop.Cancel();
 }
 async Task Run(Func<Task> action){if(busy)return;busy=true;channels.Enabled=check.Enabled=download.Enabled=install.Enabled=false;progress.Visible=true;try{await action();}catch(OperationCanceledException){}catch(Exception e){release=null;prepared=null;if(!IsDisposed){status.Text="업데이트를 확인하지 못했습니다";notes.Text=UiTheme.Error(e);progressText.Text="사내망 연결을 확인한 뒤 다시 확인을 눌러 주세요.";}}finally{busy=false;if(!IsDisposed){progress.Visible=false;channels.Enabled=check.Enabled=true;download.Enabled=release is not null&&prepared is null;install.Enabled=prepared is not null;}}}
 async Task Check(){
  prepared=null;release=null;status.Text="회사 업데이트를 확인하고 있습니다";progressText.Text="";var selected=(channels.SelectedItem as Choice)?.Id;var access=await agent.GetBetaAccess(stop.Token);if(IsDisposed)return;
  channels.DataSource=access.Allowed.OrderBy(c=>c==BetaChannels.Open?0:1).Select(c=>new Choice(c)).ToArray();channels.DisplayMember=nameof(Choice.Name);channels.SelectedItem=channels.Items.Cast<Choice>().FirstOrDefault(c=>c.Id==(selected??access.Selected))??channels.Items.Cast<Choice>().FirstOrDefault();
  if(channels.SelectedItem is not Choice choice){status.Text="등록 승인 후 업데이트할 수 있습니다";notes.Text="담당자가 이 PC의 등록을 승인하면 회사 업데이트가 표시됩니다.";return;}
  await agent.SelectBetaChannel(choice.Id,stop.Token);var found=await agent.GetBetaRelease(choice.Id,stop.Token);if(IsDisposed)return;
  if(found is null){status.Text="최신 버전입니다.";notes.Text=$"{choice.Name} 채널에 아직 배포된 업데이트가 없습니다.\r\n업데이트 문의는 회사 전산 담당자에게 확인해 주세요.";return;}
  var comparison=BetaUpdate.Compare(found,installed,productVersion);status.Text=comparison switch{1=>"새 업데이트가 있습니다",0=>"최신 버전입니다.",-1=>"최신 버전입니다.",_=>"담당자의 버전 확인이 필요합니다"};
  // Unverifiable version metadata never enables an installation action.
  release=comparison==1?found:null;notes.Text=$"{choice.Name} 채널\r\n새 버전: {found.Version}\r\n파일 크기: {found.Size/1048576d:F1} MB\r\n\r\n{found.Notes}";progressText.Text=release is not null?"업데이트 받기 → 작업 저장 → 설치 진행":"";
 }
 async Task Prepare(){
  var selected=release??throw new InvalidOperationException("업데이트를 먼저 확인해 주세요.");status.Text="업데이트 파일을 받고 있습니다";progressText.Text="다운로드한 파일도 함께 확인합니다. 창을 닫으면 받기를 취소합니다.";
  var cache=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"SMB Manager Internal Beta 1","updates");SecretFile.PrivateDirectory(cache);var dir=Path.Combine(cache,selected.Sha256.ToLowerInvariant());SecretFile.PrivateDirectory(dir);var target=Path.Combine(dir,selected.FileName);
  var valid=false;if(File.Exists(target)){using var stream=new FileStream(target,FileMode.Open,FileAccess.Read,FileShare.Read);valid=stream.Length==selected.Size&&Convert.ToHexString(SHA256.HashData(stream)).Equals(selected.Sha256,StringComparison.OrdinalIgnoreCase);if(!valid){dir=Path.Combine(cache,selected.Sha256.ToLowerInvariant()+"-"+Guid.NewGuid().ToString("N"));SecretFile.PrivateDirectory(dir);target=Path.Combine(dir,selected.FileName);}}if(!valid)await agent.DownloadBeta(selected,target,stop.Token);
  if(IsDisposed)return;prepared=new(target,selected.Size,selected.Sha256);status.Text="업데이트 설치 준비가 끝났습니다";progressText.Text="공유폴더의 작업을 저장한 뒤 아래 설치 버튼을 누르세요. Windows 승인창이 나타날 수 있습니다.";
 }
 sealed record Choice(string Id){public string Name=>BetaChannels.Label(Id);}
}