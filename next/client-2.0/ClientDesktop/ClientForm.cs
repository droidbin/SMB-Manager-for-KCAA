using System.Net.Http.Json;
using SmbManager.Local;
using SmbManager.Local.Client;
namespace SmbManager.Local.ClientDesktop;
internal sealed class ClientForm:Form {
 static string DisplayVersion {
  get {
   var info=typeof(ClientForm).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute),false).Cast<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion??"확인 불가";
   var build=System.Text.RegularExpressions.Regex.Match(info,@"\+qa\.(\d+)");
   return info.Split('+')[0]+(build.Success?" · 빌드 "+build.Groups[1].Value:"");
  }
 }
 sealed record ServerChoice(string Label,string Address){public override string ToString()=>Label;}
 sealed record Preferences(string LastReadId,string LastAnnouncedId="",string ServerUrl="");
 readonly string stateDir,policyDir,preview;readonly bool snapshotMode;readonly string bootstrapFile;
 readonly ComboBox url=new(){Dock=DockStyle.Fill,DropDownStyle=ComboBoxStyle.DropDownList,AccessibleName="회사 서버"};
 readonly ComboBox department=new(){Dock=DockStyle.Fill,DropDownStyle=ComboBoxStyle.DropDownList,AccessibleName="부서"};
 readonly TextBox name=new(){Dock=DockStyle.Fill,PlaceholderText="예: 홍길동",MaxLength=80,AccessibleName="성명"};
 readonly Label summary=TextLabel("회사 연결을 준비하고 있습니다",15,true),detail=TextLabel("잠시 기다려 주세요.",10.5f),who=TextLabel("",10),hint=TextLabel("부서와 성명을 입력하고 등록 요청을 누르세요.",10),connectionHint=TextLabel("",10),unread=TextLabel("새 알림 없음",10),empty=TextLabel("배정된 폴더가 여기에 표시됩니다.",10.5f);
 readonly Button register=ActionButton("등록 요청",true),statusAction=ActionButton("서버 연결 확인"),registrationInfo=ActionButton("내 정보 변경"),checkServer=ActionButton("서버 연결 확인"),markRead=ActionButton("모두 읽음으로 표시"),expand=ActionButton("크게 보기");
 readonly Panel statusPanel=new(){Dock=DockStyle.Fill,AutoSize=true,Padding=new(14),BackColor=Color.White,Margin=new(0,0,0,8)};
 readonly TableLayoutPanel registrationContent=new(){Dock=DockStyle.Top,AutoSize=true,ColumnCount=1},folders=new(){Dock=DockStyle.Top,AutoSize=true,ColumnCount=1},identityRow=new(){Dock=DockStyle.Top,AutoSize=true,ColumnCount=2};
 readonly Panel folderViewport=new(){Dock=DockStyle.Fill,AutoScroll=true};
 readonly TextBox notifications=new(){Dock=DockStyle.Fill,ReadOnly=true,Multiline=true,ScrollBars=ScrollBars.Vertical,BackColor=Color.FromArgb(17,24,39),ForeColor=Color.FromArgb(226,232,240),Font=UiTheme.Font(11),BorderStyle=BorderStyle.FixedSingle,AccessibleName="회사 알림 내용"};
 readonly Label noNotifications=TextLabel("새로운 회사 알림이 도착하면 여기에 표시됩니다.",10);
 readonly System.Windows.Forms.Timer timer=new(){Interval=1500};readonly CancellationTokenSource stop=new();CancellationTokenSource? agentStop;readonly NotifyIcon tray=new();
 readonly TableLayoutPanel root;readonly GroupBox registrationCard,notificationCard,folderCard;CompanyNotificationForm? notificationPopup;ClientAgent? agent;Task? worker;
 bool loadingDepartments,legacyPending,legacyBusy,registering,reconnecting,allowExit,balloonShown,editingServer,exitConfirmationOpen,installPending,changingServer;
 DateTimeOffset nextServerCheck=DateTimeOffset.MinValue;string preferredServer="";string lastRows="",lastNotifications="",lastReadId="",lastAnnounced="";ClientExperience experience=new("연결 준비 중","");
 public ClientUpdateRequest? PendingUpdate {get;private set;}
 string Address=>url.SelectedItem is ServerChoice server&&url.Text==server.Label?server.Address:url.Text.Trim();
 static Label TextLabel(string text,float size=10,bool bold=false)=>new(){Text=text,AutoSize=true,Font=UiTheme.Font(size,bold),Margin=new(0,3,0,5),ForeColor=UiTheme.Ink};
 internal static Button ActionButton(string text,bool primary=false){var b=new Button{Text=text,AccessibleName=text};UiTheme.Button(b,primary);b.Font=UiTheme.Font(10,primary);b.MinimumSize=new(88,38);return b;}
 public ClientForm(string bootstrap,string directory,string policyDirectory,bool migrateLegacy=false,string preview=""){
  stateDir=directory;policyDir=policyDirectory;bootstrapFile=bootstrap;snapshotMode=!migrateLegacy;this.preview=preview;
  Text="SMB Manager · "+DisplayVersion;UiTheme.Form(this,new(860,780),new(680,650));Font=UiTheme.Font(10);
  root=UiTheme.ScrollingRoot(this,6,560,verticalOnly:true);root.Padding=new(16,12,16,12);
  root.RowStyles.Add(new(SizeType.AutoSize));root.RowStyles.Add(new(SizeType.AutoSize));root.RowStyles.Add(new(SizeType.AutoSize));root.RowStyles.Add(new(SizeType.Percent,58));root.RowStyles.Add(new(SizeType.Percent,42));root.RowStyles.Add(new(SizeType.AutoSize));
  var header=new TableLayoutPanel{Dock=DockStyle.Fill,AutoSize=true,ColumnCount=4,Margin=new(0,0,0,8)};header.ColumnStyles.Add(new(SizeType.Percent,100));header.ColumnStyles.Add(new(SizeType.AutoSize));header.ColumnStyles.Add(new(SizeType.AutoSize));header.ColumnStyles.Add(new(SizeType.AutoSize));header.Controls.Add(TextLabel("SMB Manager",18,true),0,0);
  Button settings=ActionButton("설정"),update=ActionButton("업데이트"),help=ActionButton("도움말");header.Controls.Add(settings,1,0);header.Controls.Add(update,2,0);header.Controls.Add(help,3,0);settings.Click+=async(_,_)=>await ShowSettings();root.Controls.Add(header,0,0);
  update.Click+=async(_,_)=>{if(agent is null){MessageBox.Show(this,"등록이 승인되면 회사에서 제공하는 업데이트를 확인할 수 있습니다.","업데이트 안내");return;}using var dialog=new BetaDialog(agent);dialog.ShowDialog(this);if(dialog.Installation is { } request)await PrepareInstallation(request);};help.Click+=(_,_)=>ShowSupport();
  var stateLayout=new TableLayoutPanel{Dock=DockStyle.Top,AutoSize=true,ColumnCount=1};stateLayout.Controls.Add(summary);stateLayout.Controls.Add(detail);stateLayout.Controls.Add(statusAction);UiTheme.Wrap(summary,stateLayout,4);UiTheme.Wrap(detail,stateLayout,4);statusPanel.Controls.Add(stateLayout);root.Controls.Add(statusPanel,0,1);statusAction.Click+=async(_,_)=>{switch(experience.Action){case "retry":await Reconnect();break;case "switch":await Reconnect(null,true);break;case "server":await CheckServer();break;case "install":await RetryInstallCompletion();break;case "settings":await ShowSettings();break;default:ShowSupport();break;}};
  url.Items.Add(new ServerChoice("요리광주 회사 서버 (기본)","https://kcookartgj.duckdns.org:48753/"));url.SelectedIndex=0;
  var fields=new TableLayoutPanel{Dock=DockStyle.Top,AutoSize=true,ColumnCount=2};fields.ColumnStyles.Add(new(SizeType.Absolute,78));fields.ColumnStyles.Add(new(SizeType.Percent,100));
  Control[] inputs=[url,department,name];string[] labels=["회사 서버","부서","성명"];for(var i=0;i<3;i++){fields.Controls.Add(new Label{Text=labels[i],AutoSize=true,Padding=new(0,9,0,6)},0,i);inputs[i].Margin=new(0,7,0,7);fields.Controls.Add(inputs[i],1,i);}
  var advanced=new LinkLabel{Text="서버 주소 직접 입력",AutoSize=true,Margin=new(0,4,0,6),AccessibleName="회사 서버 주소 직접 입력"};fields.Controls.Add(advanced,1,3);advanced.LinkClicked+=(_,_)=>{editingServer=!editingServer;var address=Address;url.DropDownStyle=editingServer?ComboBoxStyle.DropDown:ComboBoxStyle.DropDownList;if(editingServer){url.SelectedIndex=-1;url.Text=address;url.Focus();}else url.SelectedIndex=0;advanced.Text=editingServer?"기본 회사 서버 사용":"서버 주소 직접 입력";};
  fields.Controls.Add(register,1,4);registrationContent.Controls.Add(fields);registrationContent.Controls.Add(hint);registrationContent.Controls.Add(checkServer);UiTheme.Wrap(hint,registrationContent,8);
  registrationCard=UiTheme.AutoCard("처음 한 번만 등록하면 자동으로 연결됩니다",registrationContent);
  identityRow.ColumnStyles.Add(new(SizeType.Percent,100));identityRow.ColumnStyles.Add(new(SizeType.AutoSize));identityRow.Controls.Add(who,0,0);identityRow.Controls.Add(registrationInfo,1,0);UiTheme.Wrap(who,identityRow,160);identityRow.Visible=false;
  var registrationArea=new TableLayoutPanel{Dock=DockStyle.Fill,AutoSize=true,ColumnCount=1,Margin=new(0,0,0,8)};registrationArea.Controls.Add(registrationCard);registrationArea.Controls.Add(identityRow);root.Controls.Add(registrationArea,0,2);
  var folderContent=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=2};folderContent.RowStyles.Add(new(SizeType.Percent,100));folderContent.RowStyles.Add(new(SizeType.AutoSize));folders.Margin=Padding.Empty;folderViewport.Controls.Add(folders);folderViewport.Controls.Add(empty);empty.Dock=DockStyle.Top;empty.Padding=new(8);UiTheme.Wrap(empty,folderViewport,20);folderContent.Controls.Add(folderViewport,0,0);folderContent.Controls.Add(connectionHint,0,1);UiTheme.Wrap(connectionHint,folderContent,8);
  folderCard=UiTheme.Card("내 공유폴더",folderContent);folderCard.MinimumSize=new(0,220);folderCard.Margin=new(0,0,0,8);root.Controls.Add(folderCard,0,3);folderViewport.SizeChanged+=(_,_)=>{folders.Margin=Padding.Empty;folders.Width=Math.Max(1,folderViewport.ClientSize.Width);};
  var noticeContent=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=2};noticeContent.RowStyles.Add(new(SizeType.AutoSize));noticeContent.RowStyles.Add(new(SizeType.Percent,100));
  var noticeHead=new TableLayoutPanel{Dock=DockStyle.Fill,AutoSize=true,ColumnCount=3};noticeHead.ColumnStyles.Add(new(SizeType.Percent,100));noticeHead.ColumnStyles.Add(new(SizeType.AutoSize));noticeHead.ColumnStyles.Add(new(SizeType.AutoSize));noticeHead.Controls.Add(unread,0,0);noticeHead.Controls.Add(expand,1,0);noticeHead.Controls.Add(markRead,2,0);noticeContent.Controls.Add(noticeHead,0,0);var noticeBody=new Panel{Dock=DockStyle.Fill};noticeBody.Controls.Add(notifications);noticeBody.Controls.Add(noNotifications);noNotifications.Dock=DockStyle.Top;noticeContent.Controls.Add(noticeBody,0,1);notificationCard=UiTheme.Card("회사 알림",noticeContent);notificationCard.MinimumSize=new(0,100);notificationCard.Margin=new(0,0,0,6);root.Controls.Add(notificationCard,0,4);
  expand.Click+=(_,_)=>ShowNotificationHistory();markRead.Click+=(_,_)=>{if(agent is null)return;MarkReadThrough(agent.NotificationEntries.LastOrDefault()?.Id??"");};
  var footer=TextLabel("창을 닫아도 자동 연결과 회사 알림은 계속됩니다.",9);footer.ForeColor=UiTheme.Muted;UiTheme.Wrap(footer,root,8);var footerPanel=new FlowLayoutPanel{Dock=DockStyle.Fill,AutoSize=true,WrapContents=true};var reportBug=ActionButton("버그 신고");reportBug.Click+=(_,_)=>{if(agent is null){MessageBox.Show(this,"회사 등록 승인 후 버그를 신고할 수 있습니다.","버그 신고");return;}using var dialog=new BugReportDialog(agent);dialog.ShowDialog(this);};footerPanel.Controls.Add(reportBug);footerPanel.Controls.Add(footer);root.Controls.Add(footerPanel,0,5);
  tray.Icon=Icon;tray.Text="SMB Manager";var menu=new ContextMenuStrip();menu.Items.Add("화면 열기",null,(_,_)=>ShowWindow());menu.Items.Add("설정",null,async(_,_)=>{ShowWindow();await ShowSettings();});menu.Items.Add("앱 완전히 종료",null,(_,_)=>ExitApplication());tray.ContextMenuStrip=menu;tray.DoubleClick+=(_,_)=>ShowWindow();tray.BalloonTipClicked+=(_,_)=>ShowWindow();
  register.Click+=async(_,_)=>{if(registering||legacyBusy)return;if(legacyPending&&!await RemoveLegacy())return;await Register();};checkServer.Click+=async(_,_)=>await CheckServer();registrationInfo.Click+=async(_,_)=>await ShowRegistration();url.SelectionChangeCommitted+=async(_,_)=>await LoadDepartments();url.Leave+=async(_,_)=>{if(agent is null&&editingServer)await LoadDepartments();};
  timer.Tick+=async(_,_)=>{UpdateScreen();if(this.preview.Length==0&&agent is null&&!loadingDepartments&&!legacyBusy&&!legacyPending&&!registering&&!installPending&&!changingServer&&DateTimeOffset.UtcNow>=nextServerCheck)await LoadDepartments();};timer.Start();
  FormClosing+=(_,e)=>{
   if(snapshotMode||allowExit||e.CloseReason!=CloseReason.UserClosing)return;
   if(exitConfirmationOpen){e.Cancel=true;return;}
   if(agent is not null){e.Cancel=true;tray.Visible=true;Hide();if(!balloonShown){balloonShown=true;tray.ShowBalloonTip(3500,"연결 유지 중","자동 연결과 회사 알림을 계속합니다. 아이콘을 누르면 화면을 다시 엽니다.",ToolTipIcon.Info);}return;}
   e.Cancel=!ConfirmExit();
  };
  FormClosed+=async(_,_)=>{notificationPopup?.Close();timer.Stop();stop.Cancel();agentStop?.Cancel();tray.Visible=false;tray.Dispose();if(worker is not null)try{await worker;}catch(OperationCanceledException){}catch{}agent?.Dispose();agentStop?.Dispose();};
  Shown+=async(_,_)=>{if(this.preview.Length>0){UpdatePreview();return;}await InitializeClient(migrateLegacy);};
 }
 async Task InitializeClient(bool migrateLegacy){StartupStatus("Starting");try{
   var pref=Path.Combine(stateDir,"ui-preferences.protected");if(File.Exists(pref))try{var saved=SecretFile.Load<Preferences>(pref);lastReadId=saved.LastReadId;lastAnnounced=saved.LastAnnouncedId;preferredServer=saved.ServerUrl;}catch{hint.Text="이전 화면 설정을 복원하지 못했습니다.";}
   if(migrateLegacy){
    var completion=Path.Combine(stateDir,"install-completion.json");
    if(File.Exists(completion)){
     var installed=Wire.Load<InstallCompletionResult>(completion);
     if(!installed.Completed){installPending=true;UpdateScreen();return;}
     if(!installed.UserSessionCompleted){
      if(!InstallCompletion.IsInteractiveUserSession()){installPending=true;hint.Text="직원 앱은 바탕화면 또는 시작 메뉴에서 일반 권한으로 실행해 설치를 마무리해 주세요.";UpdateScreen();return;}
      var sid=System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
      installed=await Task.Run(()=>InstallCompletion.Run(sid));Wire.Save(completion,installed);
      if(!installed.Completed){installPending=true;hint.Text=installed.Message;UpdateScreen();return;}
     }
    }
   }
   if(migrateLegacy&&!await RemoveLegacy()){StartupStatus("LegacyCleanupBlocked");return;}StartupStatus("LoadingRegistration");var identity=Path.Combine(stateDir,"identity.json.protected");
   if(File.Exists(identity)){var id=SecretFile.Load<Identity>(identity);SetServer(preferredServer.Length>0?preferredServer:id.Url);name.Text=id.Name;if(id.Department.Length>0){department.Items.Add(id.Department);department.SelectedItem=id.Department;}if(ClientAgentServerEquals(Address,id.Url)){await Register(existing:true);return;}await LoadDepartments();return;}
   if(File.Exists(bootstrapFile)){var bootstrap=Wire.Load<ClientBootstrap>(bootstrapFile);SetServer(preferredServer.Length>0?preferredServer:bootstrap.Url);name.Text=bootstrap.Name;if(bootstrap.Department.Length>0){department.Items.Add(bootstrap.Department);department.SelectedItem=bootstrap.Department;}if(bootstrap.Code.Length>0&&preferredServer.Length==0){await Register(bootstrap.Code);return;}}
   else if(preferredServer.Length>0)SetServer(preferredServer);
   await LoadDepartments();
  }catch(Exception e){StartupStatus("InitializationFailed",e);SetExperience(new("회사 연결을 시작하지 못했습니다",UiTheme.Error(e),"warning","server","다시 확인"));}}
 void SetServer(string address){url.DropDownStyle=ComboBoxStyle.DropDown;url.SelectedIndex=-1;url.Text=address;editingServer=true;}
 static bool ClientAgentServerEquals(string a,string b)=>Uri.Compare(Wire.LocalUrl(a),Wire.LocalUrl(b),UriComponents.AbsoluteUri,UriFormat.SafeUnescaped,StringComparison.OrdinalIgnoreCase)==0;
 async Task RetryInstallCompletion(){
  if(legacyBusy||agent is not null)return;
  if(MessageBox.Show(this,"공유폴더의 문서와 탐색기 창을 저장하고 닫았나요? 이 Windows 사용자의 SMB 연결을 해제하고 회사 Drive의 저장 계정을 정리합니다.","설치 마무리",MessageBoxButtons.YesNo,MessageBoxIcon.Warning)!=DialogResult.Yes)return;
  legacyBusy=true;try{var sid=System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;var result=await Task.Run(()=>InstallCompletion.Run(sid));Wire.Save(Path.Combine(stateDir,"install-completion.json"),result);installPending=!result.Completed;if(!result.Completed){hint.Text=result.Message;return;}await InitializeClient(false);}catch(Exception e){hint.Text=UiTheme.Error(e);}finally{legacyBusy=false;UpdateScreen();}
 }
 async Task ShowSettings(){
  if(changingServer||registering||legacyBusy)return;
  using var dialog=new ClientSettingsDialog(Address,agent is not null,installPending,agent?.RegistrationLost==true,agent?.DiagnosticsEnabled==true);
  dialog.DiagnosticsChanged+=enabled=>agent?.SetDiagnosticsEnabled(enabled)??false;
  dialog.CheckServerRequested+=async address=>{using var http=Wire.Http(address);await http.GetFromJsonAsync<string[]>("api/registration/departments",Wire.Json,stop.Token);};
  if(dialog.ShowDialog(this)!=DialogResult.OK)return;
  if(dialog.Action=="registration"){await RecoverRegistration();return;}
  if(dialog.Action=="info"){await ShowRegistration();return;}
  if(dialog.Action=="retry"){await CheckServer();return;}
  if(ClientAgentServerEquals(Address,dialog.ServerAddress)){if(agent is null)await LoadDepartments();else await CheckServer();return;}
  if(installPending){hint.Text="설치 마무리 후 회사 서버를 변경할 수 있습니다.";return;}
  if(agent is not null&&MessageBox.Show(this,"회사 서버를 변경하면 기존 회사 폴더 연결을 해제하고 새 서버에 등록을 요청합니다. 작업 중인 문서를 저장하고 닫았나요?","회사 서버 변경",MessageBoxButtons.YesNo,MessageBoxIcon.Warning)!=DialogResult.Yes)return;
  changingServer=true;try{
   if(agent is not null){using var timeout=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);timeout.CancelAfter(TimeSpan.FromSeconds(25));await agent.PrepareServerChange(timeout.Token).WaitAsync(timeout.Token);agentStop?.Cancel();if(worker is not null)await worker.WaitAsync(TimeSpan.FromSeconds(15));agent.Dispose();agent=null;worker=null;agentStop?.Dispose();agentStop=null;}
   preferredServer=dialog.ServerAddress;SetServer(preferredServer);SaveNotificationPreferences();department.Items.Clear();registrationCard.Visible=true;identityRow.Visible=false;nextServerCheck=DateTimeOffset.MinValue;lastRows="";await LoadDepartments();
  }catch(Exception e){if(agent is not null&&worker is {IsCompleted:false})agent.Paused=false;hint.Text=connectionHint.Text=UiTheme.Error(e);MessageBox.Show(this,connectionHint.Text,"회사 서버 변경 보류",MessageBoxButtons.OK,MessageBoxIcon.Warning);}finally{changingServer=false;UpdateScreen();}
 }

 async Task RecoverRegistration(){
  if(agent?.RegistrationLost!=true||changingServer)return;
  if(MessageBox.Show(this,"이 PC의 회사 등록을 다시 요청할까요? 작업 파일을 저장하고 닫으세요. 기존 앱 소유 폴더 연결을 안전하게 해제한 뒤 담당자의 새 승인을 받습니다.","PC 등록 복구",MessageBoxButtons.YesNo,MessageBoxIcon.Warning)!=DialogResult.Yes)return;
  changingServer=true;try{using var timeout=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);timeout.CancelAfter(TimeSpan.FromSeconds(25));await agent.PrepareServerChange(timeout.Token).WaitAsync(timeout.Token);agentStop?.Cancel();if(worker is not null)await worker.WaitAsync(TimeSpan.FromSeconds(15));agent.Dispose();agent=null;worker=null;agentStop?.Dispose();agentStop=null;
   var archive=Path.Combine(stateDir,"registration-backups",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(archive);SecretFile.PrivateDirectory(archive);
   foreach(var file in new[]{"identity.json.protected","identity.json"}){var path=Path.Combine(stateDir,file);if(File.Exists(path))File.Move(path,Path.Combine(archive,file));}
   lastRows="";registrationCard.Visible=true;identityRow.Visible=false;nextServerCheck=DateTimeOffset.MinValue;await LoadDepartments();hint.Text="부서와 성명을 확인하고 등록 요청을 누르세요. 담당자 승인 후 다시 연결됩니다.";
  }catch(Exception e){if(agent is not null&&worker is {IsCompleted:false})agent.Paused=false;connectionHint.Text=UiTheme.Error(e);MessageBox.Show(this,connectionHint.Text,"등록 복구 보류",MessageBoxButtons.OK,MessageBoxIcon.Warning);}finally{changingServer=false;UpdateScreen();}
 }
 async Task PrepareInstallation(ClientUpdateRequest request){
  timer.Stop();SetExperience(new("업데이트 설치를 준비하고 있습니다","자동 연결 작업을 안전하게 마무리하고 있습니다. 잠시 기다려 주세요."));statusAction.Enabled=false;Enabled=false;stop.Cancel();agentStop?.Cancel();
  try{if(worker is not null)try{await worker;}catch(OperationCanceledException){}agent?.Dispose();agent=null;worker=null;PendingUpdate=request;allowExit=true;Close();}
  catch{Enabled=true;connectionHint.Text="앱 종료 준비를 완료하지 못했습니다. 담당자에게 문의하거나 앱을 완전히 종료한 뒤 다시 실행해 주세요.";}
 }
 void StartupStatus(string phase,Exception? error=null){try{Wire.Save(Path.Combine(stateDir,"startup-status.json"),new{phase,errorType=error?.GetType().Name,error=error is null?"":UiTheme.Error(error),at=DateTimeOffset.UtcNow});}catch{}}
 void SetExperience(ClientExperience value){experience=value;summary.Text=value.Title;detail.Text=value.Detail;statusAction.Text=value.ActionText;statusAction.AccessibleName=value.ActionText;statusAction.Visible=value.Action.Length>0;statusAction.Enabled=!loadingDepartments&&!reconnecting&&!legacyBusy;var colors=value.Tone switch{"success"=>(Color.FromArgb(235,247,240),Color.FromArgb(17,94,54)),"warning"=>(Color.FromArgb(255,247,230),Color.FromArgb(120,70,0)),"danger"=>(Color.FromArgb(255,238,238),Color.FromArgb(146,28,28)),_=>(Color.FromArgb(237,244,253),Color.FromArgb(30,65,110))};statusPanel.BackColor=colors.Item1;summary.ForeColor=colors.Item2;}
 void ShowWindow(){Show();WindowState=FormWindowState.Normal;Activate();}
 bool ConfirmExit(){
  if(exitConfirmationOpen)return false;
  exitConfirmationOpen=true;
  try{using var dialog=new ExitConfirmationForm();return dialog.ShowDialog(this)==DialogResult.OK;}
  finally{exitConfirmationOpen=false;}
 }
 void ExitApplication(){if(!ConfirmExit())return;allowExit=true;Close();}
 void RegistrationLayout(bool registered){folderCard.Visible=notificationCard.Visible=registered;root.RowStyles[3].SizeType=registered?SizeType.Percent:SizeType.Absolute;root.RowStyles[3].Height=registered?58:0;if(!registered){root.RowStyles[4].SizeType=SizeType.Absolute;root.RowStyles[4].Height=0;}}
 void UpdateScreen(){
  if(IsDisposed)return;if(preview.Length>0){UpdatePreview();return;}
  if(installPending){registrationCard.Visible=true;identityRow.Visible=false;RegistrationLayout(false);register.Enabled=false;checkServer.Enabled=false;SetExperience(new("설치 마무리가 필요합니다","공유폴더의 작업 파일과 탐색기 창을 닫은 뒤 아래 버튼을 누르세요. 완료될 때까지 회사 등록과 자동 연결은 시작하지 않습니다.","warning","install","설치 마무리 다시 시도"));statusAction.Enabled=!legacyBusy;return;}
  register.Enabled=!loadingDepartments&&!legacyBusy&&!registering&&agent is null;register.Text=registering?"등록 요청 중…":legacyPending?"구버전 정리 다시 시도":"등록 요청";checkServer.Enabled=!loadingDepartments&&!legacyBusy&&!registering;checkServer.Text=loadingDepartments?"서버 확인 중…":"서버 연결 확인";
  if(agent is null){UpdateNotifications([]);RegistrationLayout(false);return;}
  RegistrationLayout(true);registrationCard.Visible=false;identityRow.Visible=true;var info=agent.Registration;var person=info?.Name is {Length:>0}?info.Name:agent.Identity.Name;var dept=info?.Department is {Length:>0}?info.Department:info?.RequestedDepartment is {Length:>0}?info.RequestedDepartment:agent.Identity.Department;
  who.Text=$"{(person.Length>0?person:"성명 확인 중")} · {(dept.Length>0?dept:"부서 확인 중")}"+(info?.Correction is not null?"\n정보 변경 요청을 담당자가 확인하고 있습니다.":"");
  var reports=agent.ConnectionReports.Where(c=>c.ProfileId.Length>0).ToArray();SetExperience(ClientExperiencePresenter.Describe(agent.Current.Phase,info?.Archived==true,agent.Quarantined,agent.Paused,agent.Policy,reports,agent.PolicyError,agent.InitialConnectionsPending,agent.PolicyUtcNow));
  if(worker is {IsCompleted:true}&&!stop.IsCancellationRequested)SetExperience(new("자동 연결 작업이 중지됐습니다","설정에서 서버 연결을 다시 확인하세요. 문제가 계속되면 앱을 종료한 뒤 다시 실행해 주세요.","warning","server","연결 작업 다시 시작"));
  empty.Text=EmptyFolderMessage();UpdateFolders(DisplayReports(reports),agent.Policy?.Profiles??[]);UpdateNotifications(agent.NotificationEntries);
  if(!snapshotMode)tray.Visible=true;
 }
 string EmptyFolderMessage()=>experience.Title switch {
  "등록 요청을 보냈습니다"=>"승인 후 이곳에 사용할 회사 폴더가 표시됩니다.",
  "회사에서 연결을 잠시 중지했습니다"=>"현재 사용할 수 있는 폴더가 없습니다.",
  "공유폴더 배정을 기다리고 있습니다"=>"배정된 폴더가 아직 없습니다.",
  _=>"회사에서 배정한 폴더가 여기에 표시됩니다."
 };
 ConnectionReport[] DisplayReports(ConnectionReport[] reports)=>agent?.InitialConnectionsPending==true
  ?reports.Select(r=>r.ConnectionState is "Disconnected" or "Unavailable" or "Unknown"?r with{ConnectionState="Restoring"}:r).ToArray():reports;
 bool CanOpen(ConnectionReport row)=>preview.Length>0?preview is not ("quarantine" or "held" or "waiting")&&row.ConnectionState=="Connected":agent is not null&&!agent.Quarantined&&!agent.Paused&&agent.Policy?.ConnectionsHeld!=true&&ConnectionRules.CanOpen(row,agent.Policy,agent.PolicyUtcNow);
 bool CanRetry=>preview.Length>0?preview=="partial":agent is not null&&!agent.InitialConnectionsPending&&!reconnecting&&!agent.Quarantined&&!agent.Paused&&agent.Policy is {Profiles.Length:>0,ConnectionsHeld:false} p&&p.ExpiresAt>agent.PolicyUtcNow&&agent.Current.Phase is not ("Offline" or "WaitingApproval" or "Starting");
 void UpdateFolders(ConnectionReport[] reports,Profile[] profiles){
  empty.Visible=reports.Length==0;folders.Visible=reports.Length>0;var signature=System.Text.Json.JsonSerializer.Serialize(reports,Wire.Json)+string.Join('|',profiles.Select(p=>p.Name))+string.Join('|',reports.Select(r=>CanOpen(r)))+CanRetry+reconnecting;
  if(signature==lastRows)return;lastRows=signature;folders.SuspendLayout();foreach(Control c in folders.Controls.Cast<Control>().ToArray()){folders.Controls.Remove(c);c.Dispose();}folders.RowStyles.Clear();folders.RowCount=0;
  foreach(var row in reports){var rowPanel=new TableLayoutPanel{Dock=DockStyle.Top,AutoSize=true,ColumnCount=2,Padding=new(10,7,10,7),BackColor=Color.White,Margin=new(0,0,0,6)};rowPanel.ColumnStyles.Add(new(SizeType.Percent,100));rowPanel.ColumnStyles.Add(new(SizeType.AutoSize));
   var texts=new TableLayoutPanel{Dock=DockStyle.Top,AutoSize=true,ColumnCount=1};var title=TextLabel(profiles.FirstOrDefault(p=>p.Id==row.ProfileId)?.Name??"회사 공유폴더",12,true);var state=TextLabel($"{row.Drive} · {ClientExperiencePresenter.FolderState(row.ConnectionState)}",10);if(row.ConnectionState=="Connected"&&!CanOpen(row))state.Text=row.Drive+" · 회사 연결 정보 확인 필요";state.ForeColor=row.ConnectionState=="Connected"&&CanOpen(row)?Color.FromArgb(17,94,54):Color.FromArgb(120,70,0);texts.Controls.Add(title);texts.Controls.Add(state);UiTheme.Wrap(title,texts,12);UiTheme.Wrap(state,texts,12);rowPanel.Controls.Add(texts,0,0);
   var action=ActionButton(row.ConnectionState is "Disconnected" or "Unavailable"?"다시 연결":"열기",row.ConnectionState=="Connected");action.AccessibleName=title.Text+" "+action.Text;action.Anchor=AnchorStyles.Right;action.Enabled=row.ConnectionState is "Disconnected" or "Unavailable"?CanRetry:CanOpen(row);action.Click+=async(_,_)=>{if(row.ConnectionState is "Disconnected" or "Unavailable")await Reconnect(row.ProfileId);else OpenFolder(row);};rowPanel.Controls.Add(action,1,0);folders.Controls.Add(rowPanel,0,folders.RowCount++);
  }folders.ResumeLayout(true);
 }
 void OpenFolder(ConnectionReport row){if(preview.Length>0)return;var current=agent?.ConnectionReports.FirstOrDefault(r=>r.ProfileId==row.ProfileId);if(current is null||!CanOpen(current))return;if(!System.Text.RegularExpressions.Regex.IsMatch(current.Drive,"^[A-Z]:$"))return;try{System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(current.Drive+"\\"){UseShellExecute=true});}catch{connectionHint.Text="폴더를 열지 못했습니다. 연결 상태를 확인하거나 담당자에게 문의해 주세요.";}}
 async Task Reconnect(string? profileId=null,bool switchAccount=false){
  if(!CanRetry||agent is null)return;var reports=agent.ConnectionReports;if(!switchAccount&&reports.Any(r=>r.ConnectionState=="AuthenticationRequired"&&(profileId is null||profileId==r.ProfileId))){ShowSupport();return;}
  if(switchAccount&&MessageBox.Show(this,"공유폴더에서 작업 중인 파일을 저장하고 닫으세요. 이전 부서에 배정된 연결을 새 부서 계정으로 갱신할까요?","부서 연결 갱신",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return;
  var targets=switchAccount?null:reports.Where(r=>(profileId is null||r.ProfileId==profileId)&&r.ConnectionState is "Disconnected" or "Unavailable").Select(r=>r.ProfileId).ToArray();if(!switchAccount&&targets?.Length==0)return;
  reconnecting=true;connectionHint.Text="공유폴더를 다시 연결하고 있습니다.";UpdateScreen();try{var results=await agent.ReconnectNow(stop.Token,switchAccount,targets);connectionHint.Text=results.Length>0&&results.All(r=>r.State=="Succeeded")?"폴더 연결을 확인했습니다. 열기 버튼을 눌러 사용하세요.":"연결하지 못한 폴더가 있습니다. 상태를 확인하고 문제가 계속되면 담당자에게 문의하세요.";}catch(Exception e){connectionHint.Text=UiTheme.Error(e);}finally{reconnecting=false;UpdateScreen();}
 }
 void UpdateNotifications(NotificationEntry[] entries){var sig=string.Join('|',entries.Select(e=>e.Id));if(sig!=lastNotifications){NoticeContent.Update(notifications,string.Join("\r\n\r\n",entries.Select(e=>$"{e.ReceivedAt.ToLocalTime():MM/dd HH:mm}\r\n{e.Text}")));lastNotifications=sig;}
  var index=Array.FindIndex(entries,e=>e.Id==lastReadId);var count=entries.Length==0?0:entries.Length-index-1;unread.Text=count>0?$"새 알림 {count}개":"새 알림 없음";markRead.Text=count>0?$"새 알림 {count}개 읽음으로 표시":"모두 읽음으로 표시";markRead.Enabled=count>0;expand.Enabled=entries.Length>0;notifications.Visible=entries.Length>0;noNotifications.Visible=false;markRead.Visible=expand.Visible=entries.Length>0;
  notificationCard.MinimumSize=new(0,entries.Length>0?160:100);root.RowStyles[4].SizeType=entries.Length>0?SizeType.Percent:SizeType.Absolute;root.RowStyles[4].Height=entries.Length>0?42:104;
  var newest=entries.LastOrDefault()?.Id??"";if(count>0&&newest!=lastAnnounced&&!snapshotMode){EnsurePopup();notificationPopup!.SetEntries(entries.Skip(index+1).ToArray());if(!notificationPopup.Visible)notificationPopup.Show();lastAnnounced=newest;SaveNotificationPreferences();}
 }
 void MarkReadThrough(string id){if(agent is null)return;var entries=agent.NotificationEntries;var next=Array.FindIndex(entries,e=>e.Id==id);var previous=Array.FindIndex(entries,e=>e.Id==lastReadId);if(next>previous){lastReadId=id;SaveNotificationPreferences();}UpdateScreen();}
 void EnsurePopup(){if(notificationPopup is not null&&!notificationPopup.IsDisposed)return;notificationPopup=new CompanyNotificationForm();notificationPopup.ReadConfirmed+=id=>{MarkReadThrough(id);};}
 void ShowNotificationHistory(){if(agent is null||agent.NotificationEntries.Length==0)return;EnsurePopup();notificationPopup!.SetEntries(agent.NotificationEntries,true);if(!notificationPopup.Visible)notificationPopup.Show();notificationPopup.WindowState=FormWindowState.Normal;notificationPopup.Activate();}
 void SaveNotificationPreferences(){try{SecretFile.Save(Path.Combine(stateDir,"ui-preferences.protected"),new Preferences(lastReadId,lastAnnounced,preferredServer));}catch{connectionHint.Text="알림 읽음 상태를 저장하지 못했습니다. 다음 실행에 다시 표시될 수 있습니다.";}}
 async Task CheckServer(){if(loadingDepartments||preview.Length>0)return;if(agent is null){await LoadDepartments();return;}loadingDepartments=true;SetExperience(new("회사 서버를 확인하고 있습니다","잠시 기다려 주세요."));try{if(worker is {IsCompleted:true}){if(worker.IsFaulted)_=worker.Exception;agentStop?.Dispose();agentStop=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);worker=Task.Run(()=>agent.Run(agentStop.Token));}await agent.CheckConnection(stop.Token);connectionHint.Text="회사 서버 응답을 확인했습니다. 폴더 상태도 자동으로 갱신됩니다.";}catch(Exception e){connectionHint.Text=UiTheme.Error(e);}finally{loadingDepartments=false;UpdateScreen();}}
 async Task LoadDepartments(){if(agent is not null||loadingDepartments||legacyBusy||installPending)return;loadingDepartments=true;nextServerCheck=DateTimeOffset.UtcNow.AddSeconds(20);SetExperience(new("회사 서버를 확인하고 있습니다","연결이 확인되면 부서를 선택할 수 있습니다."));UpdateScreen();try{using var http=Wire.Http(Address);var selected=department.Text;var values=await http.GetFromJsonAsync<string[]>("api/registration/departments",Wire.Json,stop.Token)??[];if(IsDisposed)return;department.Items.Clear();department.Items.AddRange(values.Cast<object>().ToArray());if(values.Contains(selected))department.SelectedItem=selected;SetExperience(new("회사 서버에 연결되었습니다","부서와 성명을 입력하면 등록을 요청할 수 있습니다."));hint.Text="등록 요청 → 담당자 승인 → 폴더 자동 연결";checkServer.Visible=false;nextServerCheck=DateTimeOffset.MaxValue;}catch(Exception e){if(!IsDisposed){SetExperience(new("회사 서버에 연결하지 못했습니다",UiTheme.Error(e),"warning","server","서버 연결 다시 확인"));hint.Text="사내망 연결을 확인하세요. 약 20초 뒤에도 자동으로 확인합니다.";checkServer.Visible=false;}}finally{loadingDepartments=false;UpdateScreen();statusAction.Enabled=true;}}
 async Task<bool> RemoveLegacy(){legacyBusy=true;SetExperience(new("이전 설치 정보를 확인하고 있습니다","기존 설정과 저장된 암호를 보존합니다."));UpdateScreen();try{var result=await Task.Run(()=>LegacyMigration.RunForCurrentUser());if(IsDisposed)return false;legacyPending=!result.Completed;hint.Text=result.Message;if(!result.Completed)SetExperience(new("이전 프로그램 정리를 확인해야 합니다","기존 프로그램을 완전히 종료한 뒤 구버전 정리 다시 시도를 누르세요.","warning"));return result.Completed;}catch{legacyPending=true;hint.Text="이전 프로그램을 종료한 뒤 다시 시도해 주세요. 기존 설정과 연결은 임의 삭제하지 않습니다.";return false;}finally{legacyBusy=false;UpdateScreen();}}
 async Task Register(string code="",bool existing=false){if(agent is not null||registering)return;if(!existing&&code.Length==0){if(department.SelectedItem is null){hint.Text="부서를 먼저 선택해 주세요.";department.Focus();return;}if(string.IsNullOrWhiteSpace(name.Text)){hint.Text="성명을 입력해 주세요. 예: 홍길동";name.Focus();return;}}
  registering=true;SetExperience(new("등록 정보를 확인하고 있습니다","잠시 기다려 주세요."));UpdateScreen();try{StartupStatus("Connecting");agent=await ClientAgent.Create(new(Address,code,name.Text.Trim(),department.Text),stateDir,stop.Token,policyDir);agentStop=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);worker=Task.Run(()=>agent.Run(agentStop.Token));StartupStatus("AgentRunning");try{await agent.RefreshRegistration(stop.Token);}catch{}UpdateScreen();}catch(Exception e){StartupStatus("RegistrationFailed",e);hint.Text=UiTheme.Error(e);SetExperience(new("등록 요청을 완료하지 못했습니다",hint.Text,"warning","server","서버 연결 확인"));}finally{registering=false;UpdateScreen();}}
 async Task ShowRegistration(){if(agent is null)return;try{await agent.RefreshRegistration(stop.Token);var info=agent.Registration!;using var http=Wire.Http(agent.Identity.Url);var departments=await http.GetFromJsonAsync<string[]>("api/registration/departments",Wire.Json,stop.Token)??[];using var dialog=new RegistrationDialog(info,departments);if(dialog.ShowDialog(this)!=DialogResult.OK)return;await agent.RequestCorrection(dialog.Request,stop.Token);connectionHint.Text="정보 변경 요청을 보냈습니다. 담당자 승인 후 부서와 폴더가 변경됩니다.";UpdateScreen();}catch(Exception e){connectionHint.Text=UiTheme.Error(e);}}
 void ShowSupport(){var rows=agent?.ConnectionReports.Where(r=>r.ProfileId.Length>0).Select(r=>$"{agent.Policy?.Profiles.FirstOrDefault(p=>p.Id==r.ProfileId)?.Name??"회사 폴더"}: {ClientExperiencePresenter.FolderState(r.ConnectionState)}")??[];var text=$"SMB Manager 문제 확인\r\n버전: {DisplayVersion}\r\n시각: {DateTimeOffset.Now:yyyy-MM-dd HH:mm}\r\n회사 서버: {Address}\r\n상태: {summary.Text}\r\n안내: {detail.Text}\r\n"+string.Join("\r\n",rows);using var dialog=new SupportDialog(text);dialog.ShowDialog(this);}
 void UpdatePreview(){
  var registered=preview!="registration";registrationCard.Visible=!registered;identityRow.Visible=registered;who.Text="홍길동 · 사업부";if(!registered){UpdateNotifications([]);RegistrationLayout(false);department.Items.Clear();department.Items.Add("사업부");department.SelectedIndex=0;name.Text="홍길동";checkServer.Visible=false;SetExperience(new("처음 한 번만 등록해 주세요","부서와 성명을 확인하고 등록 요청을 누르세요."));return;}
  RegistrationLayout(true);var profiles=new[]{new Profile("public","지점공용","SimulateConnect",1,@"\\preview\public","Y:","사업부"),new Profile("business","사업부","SimulateConnect",1,@"\\preview\business","X:","사업부")};var policy=new DepartmentPolicy("preview","preview",1,"사업부","preview",profiles,true,false,false,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow.AddMinutes(5),1);
  ConnectionReport[] reports=profiles.Select(p=>new ConnectionReport(p.Id,p.Drive,p.Unc,"preview","연결됨",false,ConnectionState:"Connected")).ToArray();if(preview=="partial")reports[1]=reports[1] with{ConnectionState="Unavailable"};if(preview=="authentication")reports[1]=reports[1] with{ConnectionState="AuthenticationRequired"};if(preview=="conflict")reports[1]=reports[1] with{ConnectionState="Conflict"};if(preview=="held"){policy=policy with{ConnectionsHeld=true,Profiles=[]};reports=[];}if(preview=="waiting")reports=[];
  SetExperience(ClientExperiencePresenter.Describe(preview=="waiting"?"WaitingApproval":preview=="offline"?"Offline":"Ready",false,preview=="quarantine",false,policy,reports));empty.Text=EmptyFolderMessage();UpdateFolders(reports,profiles);UpdateNotifications(preview=="notice"?[new("preview-1","운영 안내\r\n오늘의 회사 공지입니다. 업무 일정과 공유폴더 사용 안내를 확인해 주세요.",DateTimeOffset.Now)]:[]);
 }
}
internal sealed class SupportDialog:Form {
 public SupportDialog(string content){Text="도움말 · 담당자에게 문의";UiTheme.Form(this,new(660,520),new(540,400));var root=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new(18),ColumnCount=1,RowCount=3};root.RowStyles.Add(new(SizeType.AutoSize));root.RowStyles.Add(new(SizeType.Percent,100));root.RowStyles.Add(new(SizeType.AutoSize));Controls.Add(root);var hint=UiTheme.Hint("사내망 연결을 확인한 뒤 문제가 계속되면 아래 내용을 회사 담당자에게 전달하세요. 비밀번호와 인증 키는 포함하지 않습니다.");UiTheme.Wrap(hint,root);root.Controls.Add(hint,0,0);var text=new TextBox{Text=content,Multiline=true,ReadOnly=true,Dock=DockStyle.Fill,ScrollBars=ScrollBars.Vertical,AccessibleName="담당자에게 전달할 문제 내용"};root.Controls.Add(text,0,1);var copy=ClientForm.ActionButton("문제 내용 복사",true);copy.Click+=(_,_)=>{try{Clipboard.SetText(content);copy.Text="복사했습니다";}catch{copy.Text="복사하지 못했습니다";}};root.Controls.Add(copy,0,2);}
}
