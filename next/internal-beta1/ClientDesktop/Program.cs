using System.Net.Http.Json;
using SmbManager.Local;
using SmbManager.Local.Client;

namespace SmbManager.Local.ClientDesktop;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var stateDir = Path.GetFullPath(Wire.Arg(args, "--state-dir", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SMB Manager Internal Beta 1", "client")));
        var bootstrapFile = Wire.Arg(args, "--bootstrap", "");
        var policyDir = Path.GetFullPath(Wire.Arg(args, "--policy-dir", Path.Combine(stateDir, "real-smb")));
        try
        {
            Directory.CreateDirectory(stateDir);
            using var instanceLock = new FileStream(Path.Combine(stateDir,"client.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
            if(args.Contains("--complete-install"))
            {
                SecretFile.PrivateDirectory(stateDir);
                var result=InstallCompletion.Run(Wire.Arg(args,"--install-user-sid",""));
                Wire.Save(Path.Combine(stateDir,"install-completion.json"),result);
                Environment.ExitCode=result.Completed?0:2;
                if(!result.Completed && int.TryParse(Wire.Arg(args,"--install-ui-level","2"),out var level) && level>=3)MessageBox.Show(result.Message,"SMB Manager 설치 마무리",MessageBoxButtons.OK,MessageBoxIcon.Warning);
                return;
            }
            if (args.Contains("--headless"))
            {
                using var agent = ClientAgent.Create(Wire.Load<ClientBootstrap>(bootstrapFile), stateDir, securityStateDir: policyDir).GetAwaiter().GetResult();
                agent.Run(CancellationToken.None).GetAwaiter().GetResult();
            }
            else
            {
                ApplicationConfiguration.Initialize();
                if(args.Contains("--notification-popup-preview") && Wire.Arg(args,"--snapshot","").Length>0){var popup=new CompanyNotificationForm();popup.SetEntries([new("preview","회사 알림 시험\r\n한글 안내와 긴 메시지는 스크롤해서 확인할 수 있습니다.\r\n"+string.Join("\r\n",Enumerable.Repeat("업무 공지 내용을 확인해 주세요.",20)),DateTimeOffset.Now)]);UiDiagnostics.Run(popup,args);return;}
                if(args.Contains("--beta-preview") && Wire.Arg(args,"--snapshot","").Length>0){using var previewAgent=ClientAgent.Create(Wire.Load<ClientBootstrap>(bootstrapFile),stateDir).GetAwaiter().GetResult();UiDiagnostics.Run(new BetaDialog(previewAgent),args);return;}
                UiDiagnostics.Run(new ClientForm(bootstrapFile, stateDir, policyDir, Wire.Arg(args,"--snapshot","").Length==0), args);
            }
        }
        catch (Exception e)
        {
            Directory.CreateDirectory(stateDir);
            File.WriteAllText(Path.Combine(stateDir, "startup-error.txt"), e.Message);
            if (!args.Contains("--headless") && !args.Contains("--complete-install")) MessageBox.Show(e.Message, "Client 시작 실패");
            Environment.ExitCode = 1;
        }
    }
}

internal sealed class ClientForm : Form
{
    private sealed record ServerChoice(string Label,string Address){public override string ToString()=>Label;}
    private sealed record Preferences(string LastReadId, string LastAnnouncedId="");
    private CompanyNotificationForm? notificationPopup;
    private readonly ComboBox url=new(){Dock=DockStyle.Fill,DropDownStyle=ComboBoxStyle.DropDown};
    private readonly ComboBox department=new(){Dock=DockStyle.Fill,DropDownStyle=ComboBoxStyle.DropDownList};
    private readonly TextBox name=new(){Dock=DockStyle.Fill,PlaceholderText="예: 홍길동",MaxLength=80};
    private readonly Label summary=UiTheme.Hint("등록 준비");
    private readonly Label hint=UiTheme.Hint("부서를 선택하고 성명을 입력하세요. 등록 코드는 필요하지 않습니다.");
    private readonly Label legacyNotice=UiTheme.Hint("");
    private readonly Label who=UiTheme.Hint("등록 정보를 확인하고 있습니다.");
    private readonly Label empty=UiTheme.Hint("등록 후 관리자가 승인하고 폴더를 배정하면 여기에 나타납니다.");
    private readonly Label connectionHint=UiTheme.Hint("");
    private readonly Label unread=UiTheme.Hint("회사 알림");
    private readonly ListView connections=new(){Visible=false,Dock=DockStyle.Fill,View=View.Details,FullRowSelect=true,MultiSelect=false,ShowItemToolTips=true};
    private readonly TextBox notifications=new(){Dock=DockStyle.Fill,ReadOnly=true,Multiline=true,ScrollBars=ScrollBars.Vertical,BackColor=Color.FromArgb(17,24,39),ForeColor=Color.FromArgb(209,213,219),BorderStyle=BorderStyle.None};
    private readonly Button register=new(){Text="등록 요청"};
    private readonly Button checkServer=new(){Text="서버 연결 다시 확인"};
    private readonly Button reconnect=new(){Text="다시 연결",Enabled=false};
    private readonly Button open=new(){Text="폴더 열기",Enabled=false};
    private readonly Button registrationInfo=new(){Text="등록 정보·정정 요청",Visible=false};
    private readonly System.Windows.Forms.Timer timer=new(){Interval=1500};
    private readonly CancellationTokenSource stop=new();
    private readonly NotifyIcon tray=new();
    private readonly string stateDir,policyDir;
    private readonly bool snapshotMode;
    private readonly TableLayoutPanel fields;
    private readonly GroupBox registrationCard;
    private ClientAgent? agent;private Task? worker;
    private bool loadingDepartments,legacyPending,legacyBusy,registering,reconnecting,allowExit,balloonShown;
    private DateTimeOffset nextServerCheck=DateTimeOffset.MinValue;
    private string lastRows="",lastNotifications="",lastReadId="",lastAnnounced="";
    private string Address=>url.SelectedItem is ServerChoice server&&url.Text==server.Label?server.Address:url.Text.Trim();
    public ClientForm(string bootstrapFile,string directory,string policyDirectory,bool migrateLegacy=false){
        stateDir=directory;policyDir=policyDirectory;snapshotMode=!migrateLegacy;
        Text="SMB Manager · 베타 3 · 빌드 11";UiTheme.Form(this,new(800,820),new(660,650));
        var root=UiTheme.ScrollingRoot(this,4,620);
        root.RowStyles.Add(new(SizeType.AutoSize));root.RowStyles.Add(new(SizeType.AutoSize));root.RowStyles.Add(new(SizeType.Percent,100));root.RowStyles.Add(new(SizeType.Absolute,130));
        var header=new TableLayoutPanel{Dock=DockStyle.Fill,AutoSize=true,ColumnCount=2,Padding=new(14),BackColor=Color.White,Margin=new(0,0,0,10)};
        header.RowCount=2;header.RowStyles.Add(new(SizeType.AutoSize));header.RowStyles.Add(new(SizeType.AutoSize));
        header.ColumnStyles.Add(new(SizeType.Percent,100));header.ColumnStyles.Add(new(SizeType.AutoSize));
        header.Controls.Add(new Label{Text="SMB Manager",AutoSize=true,Font=UiTheme.Font(16,true)},0,0);
        var headerActions=new FlowLayoutPanel{AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,Anchor=AnchorStyles.Top|AnchorStyles.Right,WrapContents=false};
        var update=new Button{Text="업데이트 확인"};UiTheme.Button(update);headerActions.Controls.Add(update);
        update.Click+=(_,_)=>{if(agent is null){MessageBox.Show(this,"회사 서버에 등록한 뒤 업데이트를 확인할 수 있습니다.","업데이트 확인",MessageBoxButtons.OK,MessageBoxIcon.Information);return;}using var dialog=new BetaDialog(agent);dialog.ShowDialog(this);};
        var hide=new Button{Text="창 닫기"};UiTheme.Button(hide);hide.Click+=(_,_)=>Close();headerActions.Controls.Add(hide);header.Controls.Add(headerActions,1,0);
        header.Controls.Add(summary,0,1);header.SetColumnSpan(summary,2);UiTheme.Wrap(summary,header);root.Controls.Add(header,0,0);
        url.Items.Add(new ServerChoice("요리광주 회사 서버 (기본)","https://kcookartgj.duckdns.org:48753/"));url.SelectedIndex=0;
        fields=new TableLayoutPanel{Dock=DockStyle.Fill,AutoSize=true,ColumnCount=2};fields.ColumnStyles.Add(new(SizeType.Absolute,100));fields.ColumnStyles.Add(new(SizeType.Percent,100));
        Control[] inputs=[url,department,name];string[] labels=["회사 서버","부서","성명"];
        for(var i=0;i<inputs.Length;i++){fields.Controls.Add(new Label{Text=labels[i],AutoSize=true,Padding=new(0,8,0,8)},0,i);inputs[i].Margin=new(3,7,3,7);fields.Controls.Add(inputs[i],1,i);}
        var advanced=new LinkLabel{Text="서버 주소 변경",AutoSize=true,Margin=new(3,6,3,6)};fields.Controls.Add(advanced,1,3);
        advanced.LinkClicked+=(_,_)=>{var address=Address;url.SelectedIndex=-1;url.Text=address;url.Focus();};
        UiTheme.Button(register,true);fields.Controls.Add(register,1,4);
        var registrationContent=new TableLayoutPanel{Dock=DockStyle.Fill,AutoSize=true,ColumnCount=1};
        UiTheme.Button(registrationInfo);registrationContent.Controls.Add(fields);who.Visible=false;registrationContent.Controls.Add(who);registrationContent.Controls.Add(registrationInfo);
        var serverActions=new FlowLayoutPanel{Dock=DockStyle.Fill,AutoSize=true,WrapContents=true};UiTheme.Button(checkServer);serverActions.Controls.Add(checkServer);registrationContent.Controls.Add(serverActions);
        registrationContent.Controls.Add(hint);registrationContent.Controls.Add(legacyNotice);legacyNotice.Visible=false;
        UiTheme.Wrap(who,registrationContent);UiTheme.Wrap(hint,registrationContent);UiTheme.Wrap(legacyNotice,registrationContent);
        registrationCard=UiTheme.AutoCard("처음 한 번만 등록",registrationContent);root.Controls.Add(registrationCard,0,1);
        var body=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=3};body.RowStyles.Add(new(SizeType.Percent,100));body.RowStyles.Add(new(SizeType.AutoSize));body.RowStyles.Add(new(SizeType.AutoSize));
        connections.Columns.Add("공유폴더",200);connections.Columns.Add("드라이브",75);connections.Columns.Add("연결 상태",280);
        var listPanel=new Panel{Dock=DockStyle.Fill,MinimumSize=new(0,100)};listPanel.Controls.Add(connections);empty.Dock=DockStyle.Fill;empty.Padding=new(10);listPanel.Controls.Add(empty);body.Controls.Add(listPanel,0,0);
        body.Controls.Add(connectionHint,0,1);UiTheme.Wrap(connectionHint,body);UiTheme.Wrap(empty,listPanel);
        var actions=new FlowLayoutPanel{Dock=DockStyle.Fill,AutoSize=true,WrapContents=true};UiTheme.Button(open,true);UiTheme.Button(reconnect);actions.Controls.Add(open);actions.Controls.Add(reconnect);
        var explorer=new Button{Text="파일 탐색기 열기"};UiTheme.Button(explorer);actions.Controls.Add(explorer);explorer.Click+=(_,_)=>{try{System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe"){UseShellExecute=true});}catch(Exception e){connectionHint.Text=UiTheme.Error(e);}};
        body.Controls.Add(actions,0,2);var folderCard=UiTheme.Card("내 공유폴더",body);folderCard.MinimumSize=new(0,230);root.Controls.Add(folderCard,0,2);
        var notificationPanel=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=2};notificationPanel.ColumnStyles.Add(new(SizeType.Percent,100));notificationPanel.ColumnStyles.Add(new(SizeType.AutoSize));notificationPanel.RowStyles.Add(new(SizeType.AutoSize));notificationPanel.RowStyles.Add(new(SizeType.Percent,100));
        notificationPanel.Controls.Add(unread,0,0);var markRead=new Button{Text="읽음 표시"};UiTheme.Button(markRead);notificationPanel.Controls.Add(markRead,1,0);notificationPanel.Controls.Add(notifications,0,1);notificationPanel.SetColumnSpan(notifications,2);notifications.Text="받은 알림이 없습니다.";root.Controls.Add(UiTheme.Card("회사 알림",notificationPanel),0,3);
        markRead.Click+=(_,_)=>{if(agent is null)return;lastReadId=agent.NotificationEntries.LastOrDefault()?.Id??"";try{SecretFile.Save(Path.Combine(stateDir,"ui-preferences.protected"),new Preferences(lastReadId,lastAnnounced));}catch(Exception e){hint.Text=UiTheme.Error(e);}UpdateScreen();};
        tray.Icon=Icon;tray.Text="SMB Manager";var menu=new ContextMenuStrip();menu.Items.Add("화면 열기",null,(_,_)=>ShowWindow());menu.Items.Add("앱 완전히 종료",null,(_,_)=>ExitApplication());tray.ContextMenuStrip=menu;tray.DoubleClick+=(_,_)=>ShowWindow();tray.BalloonTipClicked+=(_,_)=>ShowWindow();
        register.Click+=async(_,_)=>{if(registering||legacyBusy)return;if(legacyPending&&!await RemoveLegacy())return;await Register();};
        checkServer.Click+=async(_,_)=>await CheckServer();url.SelectionChangeCommitted+=async(_,_)=>await LoadDepartments();url.Leave+=async(_,_)=>{if(agent is null)await LoadDepartments();};
        reconnect.Click+=async(_,_)=>await Reconnect();open.Click+=(_,_)=>OpenFolder();connections.DoubleClick+=(_,_)=>OpenFolder();connections.SelectedIndexChanged+=(_,_)=>UpdateOpen();
        connections.SizeChanged+=(_,_)=>ResizeColumns();registrationInfo.Click+=async(_,_)=>await ShowRegistration();
        timer.Tick+=async(_,_)=>{UpdateScreen();if(agent is null&&!loadingDepartments&&!legacyBusy&&!legacyPending&&!registering&&DateTimeOffset.UtcNow>=nextServerCheck)await LoadDepartments();};timer.Start();
        FormClosing+=(_,e)=>{if(!snapshotMode&&!allowExit&&agent is not null&&e.CloseReason==CloseReason.UserClosing){e.Cancel=true;tray.Visible=true;Hide();if(!balloonShown){balloonShown=true;tray.ShowBalloonTip(3500,"SMB Manager","자동 재연결과 회사 알림 수신을 계속합니다. 아이콘을 누르면 화면을 다시 엽니다.",ToolTipIcon.Info);}}};
        FormClosed+=async(_,_)=>{notificationPopup?.Close();timer.Stop();stop.Cancel();tray.Visible=false;tray.Dispose();if(worker is not null)try{await worker;}catch(OperationCanceledException){}catch{}agent?.Dispose();};
        Shown+=async(_,_)=>{
            StartupStatus("Starting");
            try{
                var pref=Path.Combine(stateDir,"ui-preferences.protected");if(File.Exists(pref))try{var saved=SecretFile.Load<Preferences>(pref);lastReadId=saved.LastReadId;lastAnnounced=saved.LastAnnouncedId;}catch{hint.Text="이전 알림의 읽음 상태를 복원하지 못했습니다. 등록 정보는 계속 확인합니다.";}
                if(migrateLegacy&&!await RemoveLegacy()){StartupStatus("LegacyCleanupBlocked");return;}
                StartupStatus("LoadingRegistration");
                var identity=Path.Combine(stateDir,"identity.json.protected");
                if(File.Exists(identity)){var id=SecretFile.Load<Identity>(identity);url.SelectedIndex=-1;url.Text=id.Url;name.Text=id.Name;if(id.Department.Length>0){department.Items.Add(id.Department);department.SelectedItem=id.Department;}await Register(existing:true);return;}
                if(File.Exists(bootstrapFile)){var b=Wire.Load<ClientBootstrap>(bootstrapFile);url.Text=b.Url;name.Text=b.Name;if(b.Department.Length>0){department.Items.Add(b.Department);department.SelectedItem=b.Department;}if(b.Code.Length>0){await Register(b.Code);return;}}
                await LoadDepartments();
            }catch(Exception e){StartupStatus("InitializationFailed",e);hint.Text=UiTheme.Error(e);}
        };
    }
    private void StartupStatus(string phase,Exception? error=null){try{Wire.Save(Path.Combine(stateDir,"startup-status.json"),new{phase,errorType=error?.GetType().Name,error=error is null?"":UiTheme.Error(error),at=DateTimeOffset.UtcNow});}catch{}}
    private void ShowWindow(){Show();WindowState=FormWindowState.Normal;Activate();}
    private void ExitApplication(){if(agent is not null&&MessageBox.Show(this,"자동 재연결과 회사 알림 수신이 중단됩니다. 기존 공유폴더를 즉시 모두 해제하는 작업은 아닙니다. 앱을 종료할까요?","SMB Manager 종료",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return;allowExit=true;Close();}
    private void ResizeColumns(){var width=Math.Max(300,connections.ClientSize.Width-8);connections.Columns[1].Width=70;connections.Columns[0].Width=Math.Max(100,(width-70)*40/100);connections.Columns[2].Width=Math.Max(130,width-70-connections.Columns[0].Width);}
    private void UpdateScreen(){
        if(IsDisposed)return;
        checkServer.Enabled=!loadingDepartments&&!legacyBusy&&!registering;
        checkServer.Text=loadingDepartments?"서버 확인 중…":"서버 연결 다시 확인";
        register.Enabled=!loadingDepartments&&!legacyBusy&&!registering&&agent is null;
        if(agent is null){open.Enabled=reconnect.Enabled=false;return;}
        fields.Visible=false;who.Visible=true;registrationInfo.Visible=true;registrationCard.Text="내 등록 정보";
        var current=agent.Current;var info=agent.Registration;var policy=agent.Policy;
        var person=info?.Name is {Length:>0}?info.Name:agent.Identity.Name;
        var requested=info?.RequestedDepartment is {Length:>0}?info.RequestedDepartment:agent.Identity.Department;
        who.Text=$"{(person.Length>0?person:"성명 확인 중")} · 요청 부서: {(requested.Length>0?requested:info is null?"확인 중":"이전 등록 · 요청 기록 없음")} · 배정 부서: {(info?.Department is {Length:>0}?info.Department:"준비 중")}"+(info?.Correction is not null?"\n등록 정보 정정 요청이 접수되어 있습니다.":"");
        var reports=agent.ConnectionReports.Where(c=>c.ProfileId.Length>0).ToArray();
        var allConnected=policy is {Profiles.Length:>0}&&reports.Length==policy.Profiles.Length&&reports.All(c=>c.ConnectionState=="Connected"&&c.AssignmentState=="연결됨");
        summary.Text=info?.Archived==true?"등록이 보관된 PC입니다. 서버관리자에게 문의하세요.":agent.Quarantined?"보호를 위해 연결이 잠겼습니다 — 서버관리자에게 문의하세요":current.Phase=="WaitingApproval"?"관리자 승인 대기 — 추가 입력 없이 기다리면 자동으로 진행됩니다":current.Phase=="Offline"?"회사 관리 서버 연결 확인 필요 — 아래 폴더 상태도 함께 확인하세요":policy is null?"폴더 배정 정보 확인 중":policy.Profiles.Length==0?"폴더 배정 대기 — 서버관리자가 공유폴더를 준비하고 있습니다":allConnected?"공유폴더 연결 완료 — 폴더를 선택해 열 수 있습니다":"공유폴더 연결 확인 중 — 일부 폴더는 추가 확인이 필요합니다";
        empty.Text=current.Phase=="WaitingApproval"?"관리자가 등록을 승인하면 폴더가 자동으로 나타납니다. 회사 서버관리자에게 성명과 요청 부서를 알려주세요.":"배정된 공유폴더를 아직 확인하지 못했습니다. 회사 서버관리자에게 배정 상태를 문의하세요.";
        empty.Visible=reports.Length==0;connections.Visible=reports.Length>0;
        reconnect.Enabled=!reconnecting&&!agent.Quarantined&&policy is {Profiles.Length:>0}&&current.Phase is not ("WaitingApproval" or "Offline" or "Starting");reconnect.Text=reconnecting?"다시 연결 중…":"다시 연결";
        var signature=System.Text.Json.JsonSerializer.Serialize(reports,Wire.Json)+string.Join("|",policy?.Profiles.Select(p=>p.Name)??[]);
        if(signature!=lastRows){var selected=connections.SelectedItems.Count>0?connections.SelectedItems[0].Tag as ConnectionReport:null;connections.BeginUpdate();connections.Items.Clear();foreach(var c in reports){var label=policy?.Profiles.FirstOrDefault(p=>p.Id==c.ProfileId)?.Name??c.Unc;var row=new ListViewItem(new[]{label,c.Drive,c.AssignmentState}){Tag=c,ToolTipText=c.Unc};connections.Items.Add(row);if(selected?.ProfileId==c.ProfileId)row.Selected=true;}connections.EndUpdate();lastRows=signature;ResizeColumns();}
        UpdateOpen();
        var entries=agent.NotificationEntries;var entriesSignature=string.Join("|",entries.Select(e=>e.Id));
        if(entriesSignature!=lastNotifications){notifications.Text=entries.Length>0?string.Join("\r\n\r\n",entries.Reverse().Select(e=>$"{e.ReceivedAt.ToLocalTime():MM/dd HH:mm}\r\n{e.Text}")):current.Notifications is {Length:>0}?string.Join("\r\n\r\n",current.Notifications.Reverse()):"받은 알림이 없습니다.";lastNotifications=entriesSignature;}
        var readIndex=Array.FindIndex(entries,e=>e.Id==lastReadId);var count=entries.Length==0?0:entries.Length-readIndex-1;unread.Text=count>0?$"새 알림 {count}개":"새 알림 없음";
        var newest=entries.LastOrDefault()?.Id??"";
        if(count>0&&newest!=lastAnnounced&&!snapshotMode){
            if(notificationPopup is null||notificationPopup.IsDisposed){
                notificationPopup=new CompanyNotificationForm();
                notificationPopup.ReadConfirmed+=id=>{lastReadId=id;SaveNotificationPreferences();UpdateScreen();};
            }
            notificationPopup.SetEntries(entries.Skip(readIndex+1).ToArray());
            if(!notificationPopup.Visible)notificationPopup.Show();
            notificationPopup.WindowState=FormWindowState.Normal;notificationPopup.Activate();
            lastAnnounced=newest;SaveNotificationPreferences();
        }
        if(!snapshotMode)tray.Visible=true;
    }
    private void SaveNotificationPreferences(){try{SecretFile.Save(Path.Combine(stateDir,"ui-preferences.protected"),new Preferences(lastReadId,lastAnnounced));}catch{hint.Text="알림 확인 상태를 저장하지 못했습니다. 다음 실행에 다시 표시될 수 있습니다.";}}
    private bool CanOpen(ConnectionReport row)=>agent is not null&&!agent.Quarantined&&!agent.Paused&&ConnectionRules.CanOpen(row,agent.Policy,DateTimeOffset.UtcNow);
    private void UpdateOpen(){open.Enabled=connections.SelectedItems.Count==1&&connections.SelectedItems[0].Tag is ConnectionReport row&&CanOpen(row);}
    private void OpenFolder(){
        if(connections.SelectedItems.Count!=1||connections.SelectedItems[0].Tag is not ConnectionReport row||!CanOpen(row))return;
        if(!System.Text.RegularExpressions.Regex.IsMatch(row.Drive,"^[A-Z]:$")||agent!.Policy?.Profiles.Any(p=>p.Id==row.ProfileId&&p.Drive==row.Drive)!=true)return;
        try{System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(row.Drive+"\\"){UseShellExecute=true});}catch(Exception){connectionHint.Text="폴더를 열지 못했습니다. 다시 연결을 누르거나 서버관리자에게 문의하세요.";}
    }
    private async Task Reconnect(){if(agent is null||reconnecting)return;var replacePrevious=false;var policy=agent.Policy;var mismatch=policy is not null&&agent.ConnectionReports.Any(c=>c.ProfileId.Length>0&&(c.ConnectionState is "Connected" or "AccountMismatch")&&policy.Profiles.Any(p=>p.Id==c.ProfileId)&&!string.Equals(c.Account,policy.Accounts?.FirstOrDefault(a=>a.ProfileId==c.ProfileId)?.Account??policy.ExpectedAccount,StringComparison.OrdinalIgnoreCase));if(mismatch){if(MessageBox.Show(this,"이전에 배정된 부서 계정으로 연결된 폴더를 새 부서 계정으로 전환할까요? 작업 중인 파일을 저장하고 닫아주세요. 이전 배정과 일치하는 폴더만 해제하며 다른 연결은 보존합니다.","부서 계정 전환",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return;replacePrevious=true;}reconnecting=true;connectionHint.Text="배정된 폴더를 다시 연결하고 있습니다. 잠시 기다려 주세요.";UpdateScreen();try{var results=await agent.ReconnectNow(stop.Token,replacePrevious);connectionHint.Text=results.Length==0?"배정된 폴더가 없습니다. 서버관리자에게 문의하세요.":results.All(r=>r.State=="Succeeded")?"공유폴더 연결을 확인했습니다. 목록에서 폴더를 선택해 열어 주세요.":"일부 폴더를 연결하지 못했습니다. 목록의 연결 상태를 확인하고 서버관리자에게 알려주세요.";}catch(Exception e){connectionHint.Text=UiTheme.Error(e);}finally{reconnecting=false;UpdateScreen();}}
    private async Task CheckServer(){if(loadingDepartments)return;if(agent is null){await LoadDepartments();return;}loadingDepartments=true;UpdateScreen();try{await agent.RefreshRegistration(stop.Token);hint.Text="회사 서버 연결을 확인했습니다. 승인·폴더 연결 상태는 자동으로 갱신됩니다.";}catch(Exception e){hint.Text=UiTheme.Error(e);}finally{loadingDepartments=false;UpdateScreen();}}
    private async Task LoadDepartments(){
        if(agent is not null||loadingDepartments||legacyBusy)return;loadingDepartments=true;nextServerCheck=DateTimeOffset.UtcNow.AddSeconds(20);UpdateScreen();summary.Text="회사 서버 연결 확인 중";
        try{using var http=Wire.Http(Address);var selected=department.Text;var values=await http.GetFromJsonAsync<string[]>("api/registration/departments",Wire.Json,stop.Token)??[];if(IsDisposed)return;department.Items.Clear();department.Items.AddRange(values.Cast<object>().ToArray());if(values.Contains(selected))department.SelectedItem=selected;summary.Text="회사 서버 연결됨 · 등록 준비";hint.Text="부서를 선택하고 성명을 입력하세요. 신청 후에는 관리자 승인을 기다리면 됩니다.";nextServerCheck=DateTimeOffset.MaxValue;}
        catch(Exception e){if(!IsDisposed){summary.Text="회사 서버에 연결하지 못했습니다";hint.Text=UiTheme.Error(e)+"\n'서버 연결 다시 확인'을 누르세요. 약 20초 뒤에도 자동 확인합니다.";}}
        finally{loadingDepartments=false;UpdateScreen();}
    }
    private async Task<bool> RemoveLegacy(){
        legacyBusy=true;UpdateScreen();summary.Text="구버전 정리 중";legacyNotice.Visible=true;legacyNotice.Text="기존 설정·저장 비밀번호·연결된 폴더를 보존하며 구버전을 정리합니다.";
        try{var result=await Task.Run(()=>LegacyMigration.RunForCurrentUser());if(IsDisposed)return false;legacyPending=!result.Completed;summary.Text=result.Completed?"등록 준비":"구버전 정리 확인 필요";legacyNotice.Text=result.Message+"\n기존 공유폴더는 파일 탐색기에서 확인할 수 있습니다. 새 프로그램의 등록·승인은 별도로 진행합니다.";register.Text=result.Completed?"등록 요청":"구버전 정리 다시 시도";return result.Completed;}
        catch(Exception){if(!IsDisposed){legacyPending=true;register.Text="구버전 정리 다시 시도";summary.Text="구버전 정리 확인 필요";legacyNotice.Text="구버전을 종료한 뒤 다시 시도하거나 서버관리자에게 문의하세요. 기존 연결과 저장 데이터는 임의 삭제하지 않습니다.";}return false;}
        finally{legacyBusy=false;UpdateScreen();}
    }
    private async Task Register(string code="",bool existing=false){
        if(agent is not null||registering)return;
        if(!existing&&code.Length==0){if(department.SelectedItem is null){hint.Text="부서를 먼저 선택하세요.";department.Focus();return;}if(string.IsNullOrWhiteSpace(name.Text)){hint.Text="성명을 입력하세요. 예: 홍길동";name.Focus();return;}if(!snapshotMode&&MessageBox.Show(this,$"성명: {name.Text.Trim()}\n요청 부서: {department.Text}\n\n이 정보로 등록을 요청할까요?", "등록 정보 확인",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return;}
        registering=true;UpdateScreen();
        try{StartupStatus("Connecting");agent=await ClientAgent.Create(new(Address,code,name.Text.Trim(),department.Text),stateDir,stop.Token,policyDir);worker=Task.Run(()=>agent.Run(stop.Token));StartupStatus("AgentRunning");hint.Text="앱 실행 중 승인·배정·연결 상태를 자동 확인합니다. 창을 닫아도 계속 실행되며 완전 종료는 알림 영역 아이콘 메뉴에서 선택합니다.";try{await agent.RefreshRegistration(stop.Token);}catch{}UpdateScreen();}
        catch(Exception e){StartupStatus("RegistrationFailed",e);hint.Text=UiTheme.Error(e);}
        finally{registering=false;UpdateScreen();}
    }
    private async Task ShowRegistration(){
        if(agent is null)return;
        try{
            await agent.RefreshRegistration(stop.Token);var info=agent.Registration!;using var http=Wire.Http(agent.Identity.Url);var departments=await http.GetFromJsonAsync<string[]>("api/registration/departments",Wire.Json,stop.Token)??[];
            using var dialog=new RegistrationDialog(info,departments);if(dialog.ShowDialog(this)!=DialogResult.OK)return;
            await agent.RequestCorrection(dialog.Request,stop.Token);hint.Text="등록 정보 정정 요청이 접수됐습니다. 관리자가 확인하며 실제 폴더 권한은 자동 변경되지 않습니다.";UpdateScreen();
        }catch(Exception e){hint.Text=UiTheme.Error(e);}
    }
}
internal sealed class RegistrationDialog : Form
{
    private readonly TextBox name=new(){Dock=DockStyle.Fill,MaxLength=80};
    private readonly ComboBox department=new(){Dock=DockStyle.Fill,DropDownStyle=ComboBoxStyle.DropDownList};
    public CorrectionRequest Request=>new(name.Text.Trim(),department.Text);
    public RegistrationDialog(ClientRegistration info,string[] values){
        Text="내 등록 정보·정정 요청";UiTheme.Form(this,new(600,460),new(500,400));
        var root=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new(16),ColumnCount=1,AutoScroll=true};Controls.Add(root);
        var label=UiTheme.Hint($"등록 성명: {info.Name}\n요청 부서: {info.RequestedDepartment}\n배정 부서: {(info.Department.Length>0?info.Department:"준비 중")}\n요청 시각: {(info.RequestedAt==default?"이전 버전 등록":info.RequestedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"))}\n\n잘못 입력했다면 아래에서 정정을 요청하세요. 실제 권한은 관리자가 별도로 확인합니다.");root.Controls.Add(label);UiTheme.Wrap(label,root);
        root.Controls.Add(new Label{Text="성명",AutoSize=true});name.Text=info.Correction?.Name??info.Name;root.Controls.Add(name);
        root.Controls.Add(new Label{Text="요청 부서",AutoSize=true});department.Items.AddRange(values.Cast<object>().ToArray());department.SelectedItem=info.Correction?.Department??info.RequestedDepartment;root.Controls.Add(department);
        var submit=new Button{Text="정정 요청",AutoSize=true};UiTheme.Button(submit,true);root.Controls.Add(submit);
        submit.Click+=(_,_)=>{if(string.IsNullOrWhiteSpace(name.Text)||department.SelectedItem is null){MessageBox.Show(this,"성명과 요청 부서를 입력하세요.");return;}DialogResult=DialogResult.OK;Close();};
    }
}
