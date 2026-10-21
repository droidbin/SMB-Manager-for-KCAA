using System.Net.Http.Json;
using SmbManager.Local;

namespace SmbManager.Local.AdminDesktop;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        try
        {
            if(args.Contains("--beta-preview") && Wire.Arg(args,"--snapshot","").Length>0){var ready=Wire.Load<ServerReady>(Wire.Arg(args,"--ready"));using var betaHttp=Wire.Http(ready.Url,admin:ready.AdminKey);UiDiagnostics.Run(new BetaAccessDialog(betaHttp,Wire.Arg(args,"--device")),args);return;}
            if(args.Contains("--departments-preview") && Wire.Arg(args,"--snapshot","").Length>0){using var previewHttp=new HttpClient{BaseAddress=new Uri("http://127.0.0.1:1/")};UiDiagnostics.Run(new DepartmentManager(previewHttp),args);return;} if(args.Contains("--profiles-preview") && Wire.Arg(args,"--snapshot","").Length>0){using var previewHttp=new HttpClient{BaseAddress=new Uri("http://127.0.0.1:1/")};UiDiagnostics.Run(new ProfileManager(previewHttp),args);return;}
            if(args.Contains("--notification-preview") && Wire.Arg(args,"--snapshot","").Length>0){UiDiagnostics.Run(new TextRequest("알림 푸시","선택한 PC의 클라이언트에 표시할 알림을 입력하세요."),args);return;}
            if(args.Contains("--assignment-preview") && Wire.Arg(args,"--snapshot","").Length>0){
                using var http=new HttpClient{BaseAddress=new Uri("http://127.0.0.1:1/")};
                var share=new Profile("preview","사업부 공용 문서","Custom",1,@"\\FILESERVER01\사업부","R:","사업부");
                var device=new DeviceView("preview","사업부 PC 01","직원","preview",true,false,DateTimeOffset.Now,true,["R:"],
                    Connections:[new(share.Id,share.Drive,share.Unc,@"FILESERVER01\sales","배정 일치 · 공유폴더 ACL 미검증",true)],
                    Assignment:new("사업부",@"FILESERVER01\sales",[share.Id],true,false,1));
                UiDiagnostics.Run(new AssignmentEditor(http,device,[share],new(){["사업부"]=@"FILESERVER01\sales"}),args);
                return;
            }
            if (args.Contains("--monitor-local"))
            {
                var form = new Form { Text = "SMB Manager — 로컬 PC 연결 모니터", Size = new(1120, 850), MinimumSize = new(900, 760), Font = new("맑은 고딕", 10), Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) };
                form.Controls.Add(new LocalSmbMonitor()); UiDiagnostics.Run(form, args);
            }
            else UiDiagnostics.Run(new AdminForm(Wire.Load<ServerReady>(Wire.Arg(args, "--ready")),Wire.Arg(args,"--snapshot","").Length>0), args);
        }
        catch (Exception e) { MessageBox.Show(e.Message, "관리 프로그램 시작 실패"); Environment.ExitCode = 1; }
    }
}

internal sealed class AdminForm : Form
{
    private readonly HttpClient http;
    private readonly DataGridView devices=Grid(),commands=Grid(),audit=Grid();
    private readonly Label summary=UiTheme.Hint("회사 관리 서버 연결 확인 중");
    private readonly TextBox notice=new(){Dock=DockStyle.Fill,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical};
    private readonly TextBox detail=new(){Dock=DockStyle.Fill,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,BorderStyle=BorderStyle.None,BackColor=Color.White};
    private readonly CheckBox showArchived=new(){Text="보관된 PC도 표시",AutoSize=true};
    private readonly System.Windows.Forms.Timer timer=new(){Interval=2000};
    private readonly NotifyIcon tray=new();
    private readonly bool snapshotMode;
    private bool refreshing,serverFresh,allowExit;
    private DateTimeOffset lastRefresh;
    private DeviceView[] latestDevices=[];
    private Profile[] latestProfiles=[];
    private CredentialSummary[] credentials=[];
    private string lastRows="",lastJobs="",lastAudit="";
    public AdminForm(ServerReady ready,bool snapshot=false){
        snapshotMode=snapshot;http=Wire.Http(ready.Url,admin:ready.AdminKey);
        Text="SMB Manager 서버관리자 · 베타 2 · 빌드 10";UiTheme.Form(this,new(1220,920),new(1000,760));
        var root=UiTheme.ScrollingRoot(this,4,960);root.RowStyles.Add(new(SizeType.AutoSize));root.RowStyles.Add(new(SizeType.AutoSize));root.RowStyles.Add(new(SizeType.Percent,100));root.RowStyles.Add(new(SizeType.Absolute,100));
        root.Controls.Add(UiTheme.Header("서버관리자",summary,Close),0,0);
        var toolbar=new FlowLayoutPanel{Dock=DockStyle.Fill,AutoSize=true,WrapContents=true};
        toolbar.Controls.Add(Action("등록 승인·복원",Approve));toolbar.Controls.Add(Action("PC 폴더·부서 배정",Assign));
        toolbar.Controls.Add(Action("부서 기본 설정·계정",async()=>{using var dialog=new DepartmentManager(http);dialog.ShowDialog(this);await RefreshData();}));
        toolbar.Controls.Add(Action("공유폴더 추가·관리",async()=>{using var dialog=new ProfileManager(http);dialog.ShowDialog(this);await RefreshData();}));
        toolbar.Controls.Add(Action("정정 요청 검토",Review));toolbar.Controls.Add(Action("시험 채널 자격",async()=>{using var dialog=new BetaAccessDialog(http,Selected());dialog.ShowDialog(this);await RefreshData();}));
        toolbar.Controls.Add(Action("접속 PC 일괄 재연결",()=>BulkConnections("Reconnect")));
        toolbar.Controls.Add(Action("접속 PC 일괄 연결 해제",()=>BulkConnections("Disconnect")));
        toolbar.Controls.Add(Action("등록 거절·보관",Archive));toolbar.Controls.Add(Action("새로고침",RefreshData,true));toolbar.Controls.Add(showArchived);
        showArchived.CheckedChanged+=async(_,_)=>{lastRows="";await RefreshData();};
        var hint=UiTheme.Hint("준비 순서: 공유폴더 추가 → 부서 기본 설정·서버 계정 → 직원 승인·배정 → PC 적용·연결 확인. 직원은 등록 코드를 입력하지 않습니다.");
        var setup=new TableLayoutPanel{Dock=DockStyle.Fill,AutoSize=true,ColumnCount=1};setup.Controls.Add(toolbar);setup.Controls.Add(hint);UiTheme.Wrap(hint,setup);root.Controls.Add(UiTheme.AutoCard("직원 등록 · 사용 준비",setup),0,1);
        var tabs=new TabControl{Dock=DockStyle.Fill,MinimumSize=new(0,410)};root.Controls.Add(tabs,0,2);
        var management=new TabPage("직원 · 폴더 연결"){Padding=new(8),BackColor=UiTheme.Background};var history=new TabPage("작업 내역"){Padding=new(8),BackColor=UiTheme.Background};var local=new TabPage("이 PC로 들어오는 SMB"){BackColor=UiTheme.Background};var auditTab=new TabPage("관리 변경 이력"){Padding=new(8)};auditTab.Controls.Add(audit);tabs.TabPages.AddRange([management,history,auditTab,local]);UiTheme.Grid(audit);
        UiTheme.Grid(devices);UiTheme.Grid(commands);
        var body=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2};body.ColumnStyles.Add(new(SizeType.Percent,64));body.ColumnStyles.Add(new(SizeType.Percent,36));management.Controls.Add(body);body.Controls.Add(UiTheme.Card("직원별 준비·연결 상태",devices),0,0);
        var side=new TableLayoutPanel{Dock=DockStyle.Fill,RowCount=2,ColumnCount=1};side.RowStyles.Add(new(SizeType.Percent,100));side.RowStyles.Add(new(SizeType.AutoSize));side.Controls.Add(detail,0,0);
        var actions=new FlowLayoutPanel{Dock=DockStyle.Fill,AutoSize=true,WrapContents=true};actions.Controls.Add(Action("배정 폴더 복구",async()=>{var id=Selected();await Wire.Read<CommandView>(await http.PostAsync($"api/admin/devices/{id}/reconnect",null));notice.Text="복구 요청을 접수했습니다. 작업 내역과 실제 연결 상태에서 결과를 확인하세요.";await RefreshData();},primary:true));
        actions.Controls.Add(Action("회사 알림 보내기",async()=>{using var dialog=new TextRequest("회사 알림 보내기",Target(SelectedDevice())+"\n직원 알림함으로 전달할 내용을 입력하세요.");if(dialog.ShowDialog(this)==DialogResult.OK)await SendControl("Notify",dialog.Value);}));
        actions.Controls.Add(Action("보호 연결 차단",()=>SendControl("Quarantine","서버관리자 보호 요청")));actions.Controls.Add(Action("보호 잠금 해제",()=>SendControl("ReleaseQuarantine",SelectedDevice().IncidentId)));
        side.Controls.Add(actions,0,1);body.Controls.Add(UiTheme.Card("선택 직원 · 연결 정보",side),1,0);
        var jobLayout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=2};jobLayout.RowStyles.Add(new(SizeType.AutoSize));jobLayout.RowStyles.Add(new(SizeType.Percent,100));var jobHint=UiTheme.Hint("접수·전달·실행·완료를 구분합니다. 알림 수신 완료는 직원의 읽음을 보장하지 않습니다. 보호 차단은 전체 네트워크 격리가 아닙니다.");UiTheme.Wrap(jobHint,jobLayout);jobLayout.Controls.Add(jobHint,0,0);jobLayout.Controls.Add(commands,0,1);history.Controls.Add(jobLayout);local.Controls.Add(new LocalSmbMonitor());
        root.Controls.Add(notice,0,3);notice.Text="창을 닫아도 관리 서버는 백그라운드에서 계속 실행됩니다. 완전 종료는 알림 영역 아이콘 메뉴에서 선택하세요.\r\n실제 읽기·쓰기 권한은 Drive 서버의 공유 권한 설정을 확인해야 합니다.";
        detail.Text="목록에서 직원을 선택하세요. 성명·컴퓨터명·배정·실제 적용 계정을 확인할 수 있습니다.";
        devices.SelectionChanged+=(_,_)=>ShowDetails();timer.Tick+=async(_,_)=>await RefreshData();timer.Start();Shown+=async(_,_)=>await RefreshData();
        tray.Icon=Icon;tray.Text="SMB Manager 서버관리자";tray.Visible=!snapshot;var menu=new ContextMenuStrip();menu.Items.Add("관리 화면 열기",null,(_,_)=>ShowWindow());menu.Items.Add("관리 서버 완전히 종료",null,(_,_)=>ExitServer());tray.ContextMenuStrip=menu;tray.DoubleClick+=(_,_)=>ShowWindow();tray.BalloonTipClicked+=(_,_)=>ShowWindow();
        FormClosing+=(_,e)=>{if(!snapshotMode&&!allowExit&&e.CloseReason==CloseReason.UserClosing){e.Cancel=true;Hide();tray.ShowBalloonTip(3000,"SMB Manager 서버관리자","관리 서버를 계속 실행합니다. 아이콘을 누르면 관리 화면이 열립니다.",ToolTipIcon.Info);}};
        FormClosed+=(_,_)=>{timer.Stop();tray.Visible=false;tray.Dispose();http.Dispose();};
    }
    private bool bulkBusy;
    private async Task BulkConnections(string operation){
        if(bulkBusy)throw new InvalidOperationException("다른 일괄 요청이 처리 중입니다.");
        bulkBusy=true;
        try{
            await RefreshData();if(!serverFresh)throw new InvalidOperationException("최신 접속 상태를 확인하세요.");
            var targets=latestDevices.Where(d=>d.Online&&!d.Archived&&d.Approved&&d.Assignment is not null&&(operation=="Disconnect"||(!d.Quarantined&&!d.Paused))).ToArray();
            if(targets.Length==0)throw new InvalidOperationException("현재 요청 가능한 접속 PC가 없습니다.");
            if(targets.Length>500)throw new InvalidOperationException("한 번에 최대 500대까지 처리할 수 있습니다.");
            var title=operation=="Disconnect"?"접속 PC 일괄 연결 해제":"접속 PC 일괄 재연결";
            var impact=operation=="Disconnect"?"앱이 관리하는 Drive 연결을 회수하고 자동 연결을 보류합니다. 부서·폴더 배정은 유지됩니다. 열린 파일은 강제로 닫지 않으므로 해제가 남을 수 있습니다. 재개하려면 재연결을 요청하세요.":"배정된 Drive 연결을 복구합니다. 보호 잠금·사용자 일시중지 PC는 제외합니다. 잘못된 암호는 인증 실패 후 자동 중지됩니다.";
            using var review=new ImpactReview(title+" 확인",$"대상 {targets.Length}대\n{impact}\n\n"+string.Join("\n",targets.Select(d=>$"{d.Name} / {d.ComputerName} / {d.Assignment!.Department} / {d.Id}")));
            if(review.ShowDialog(this)!=DialogResult.OK)return;
            var results=await Wire.Read<BulkConnectionResult[]>(await http.PostAsJsonAsync("api/admin/devices/connections",new BulkConnectionRequest(operation,targets.Select(d=>d.Id).ToArray()),Wire.Json));
            await RefreshData();notice.Text=$"{title}: 접수 {results.Count(r=>r.Accepted)}대 / 제외 {results.Count(r=>!r.Accepted)}대. 접수는 완료를 뜻하지 않습니다. PC 연결 상태와 작업 내역을 확인하세요.\r\n"+string.Join("\r\n",results.Where(r=>!r.Accepted).Select(r=>$"{targets.FirstOrDefault(d=>d.Id==r.DeviceId)?.ComputerName??r.DeviceId}: {r.Detail}"));
        }finally{bulkBusy=false;}
    }
    private void ShowWindow(){Show();WindowState=FormWindowState.Normal;Activate();}
    private void ExitServer(){if(MessageBox.Show(this,"관리 서버도 종료됩니다. 직원 PC가 정책을 갱신하지 못하면 약 30초 뒤 앱이 만든 공유 연결을 회수할 수 있습니다.\n\n관리 서버를 완전히 종료할까요?","관리 서버 종료 영향",MessageBoxButtons.YesNo,MessageBoxIcon.Warning)!=DialogResult.Yes)return;allowExit=true;Close();}
    private static DataGridView Grid()=>new(){Dock=DockStyle.Fill,ReadOnly=true,AllowUserToAddRows=false,AllowUserToDeleteRows=false,RowHeadersVisible=false,SelectionMode=DataGridViewSelectionMode.FullRowSelect,MultiSelect=false,AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill,AutoSizeRowsMode=DataGridViewAutoSizeRowsMode.AllCells};
    private Button Action(string text,Func<Task> action,bool allowOffline=false,bool primary=false){var button=new Button{Text=text};UiTheme.Button(button,primary);button.Click+=async(_,_)=>{button.Enabled=false;try{if(!allowOffline&&(!serverFresh||DateTimeOffset.UtcNow-lastRefresh>TimeSpan.FromSeconds(8)))throw new InvalidOperationException("회사 관리 서버의 최신 상태를 확인하지 못했습니다. 새로고침 후 다시 시도하세요.");await action();}catch(Exception e){notice.Text=UiTheme.Error(e);}finally{if(!button.IsDisposed)button.Enabled=true;}};return button;}
    private string Selected()=>devices.CurrentRow?.Cells["Id"].Value?.ToString()??throw new InvalidOperationException("먼저 목록에서 직원을 선택하세요.");
    private DeviceView SelectedDevice()=>latestDevices.First(d=>d.Id==Selected());
    private static string Target(DeviceView d)=>$"성명: {d.Name}\n컴퓨터: {(d.ComputerName.Length>0?d.ComputerName:"이전 등록 · PC명 미확인")}\n요청 부서: {d.RequestedDepartment}\n현재 배정: {d.Assignment?.Department??"미배정"}\n장치 ID: {d.Id}\nMAC: {string.Join(" / ",d.Hardware?.MacAddresses??[])}".ReplaceLineEndings(Environment.NewLine);
    private string Preparation(DeviceView d){if(d.Archived)return "등록 보관됨";if(!d.Approved)return "등록 승인 대기";if(d.Quarantined)return "보호 잠금 · 전체 차단 미확인";if(d.ConnectionsHeld)return "관리자 연결 해제 · 자동 연결 보류";if(d.Assignment is null)return "폴더 배정 필요";if(!d.Online)return "PC 통신 확인 필요";if(d.Assignment.ProfileIds.Length==0&&d.Assignment.AccessRole!="임원")return "배정 폴더 없음";if(d.AppliedPolicyRevision<d.Assignment.Revision)return "PC 적용 대기";var expected=latestProfiles.Where(p=>ProfileRules.Available(p)&&(d.Assignment.AccessRole=="임원"||d.Assignment.ProfileIds.Contains(p.Id))).Select(p=>p.Id).ToArray();if(expected.Length==0)return "배정 폴더 없음";var reports=(d.Connections??[]).Where(c=>expected.Contains(c.ProfileId)).ToArray();return reports.Length==expected.Length&&reports.All(c=>c.ConnectionState=="Connected"&&c.AssignmentState.StartsWith("배정 일치"))?"사용 준비 완료":reports.Length==0?"폴더 보고 대기":"폴더 연결 확인 필요";}
    private async Task Approve(){
        var id=Selected();var d=(await http.GetFromJsonAsync<DeviceView[]>("api/admin/devices",Wire.Json)??[]).Single(x=>x.Id==id);var templates=await http.GetFromJsonAsync<DepartmentTemplate[]>("api/admin/department-templates",Wire.Json)??[];var t=templates.FirstOrDefault(x=>x.Department==d.RequestedDepartment);
        using var review=new ImpactReview("직원 등록 승인·복원",Target(d)+"\n\n"+(t is null?"부서 기본 설정이 없습니다. 승인만 저장하며 폴더는 연결되지 않습니다. 이후 부서 설정에서 '승인 후 미배정 PC'를 확인해 배정하세요.":$"배정할 부서: {t.Department}\n역할: {t.AccessRole}\n기본 계정: {t.ExpectedAccount}\n폴더: {string.Join(" / ",t.ProfileIds.Select(key=>latestProfiles.FirstOrDefault(p=>p.Id==key)?.Name??key))}\n직원의 요청 부서와 실제 소속이 맞는지 확인하세요.")+(d.Correction is not null?"\n정정 요청이 남아 있습니다. 승인 전 요청 내용을 확인하는 것이 좋습니다.":"")+(d.Assignment is not null?"\n기존 PC 개별 배정은 이 승인으로 변경하지 않습니다.":""));
        if(review.ShowDialog(this)!=DialogResult.OK)return;await Wire.Read<System.Text.Json.JsonElement>(await http.PostAsync($"api/admin/devices/{id}/approve",null));notice.Text="등록 승인을 저장했습니다. PC 적용·폴더 연결까지 완료됐는지 준비 상태를 확인하세요.";await RefreshData();
    }
    private async Task Assign(){var d=SelectedDevice();if(!d.Approved)throw new InvalidOperationException("먼저 직원의 등록 요청을 확인하고 승인하세요.");using var dialog=new AssignmentEditor(http,d,latestProfiles,await http.GetFromJsonAsync<Dictionary<string,string>>("api/admin/departments",Wire.Json)??[]);dialog.ShowDialog(this);await RefreshData();}
    private async Task Review(){var d=SelectedDevice();if(d.Correction is null)throw new InvalidOperationException("선택 직원의 정정 요청이 없습니다.");using var dialog=new CorrectionReview(d);if(dialog.ShowDialog(this)!=DialogResult.OK)return;await Wire.Read<System.Text.Json.JsonElement>(await http.PostAsJsonAsync($"api/admin/devices/{d.Id}/correction",new ReviewCorrection(dialog.Accepted),Wire.Json));notice.Text="정정 요청을 처리했습니다. 실제 부서·폴더 권한은 자동 변경하지 않았습니다. 소속이 바뀌었다면 PC 배정을 별도로 검토하세요.";await RefreshData();}
    private async Task Archive(){var d=SelectedDevice();using var review=new ImpactReview("등록 거절·보관 영향",Target(d)+"\n\n새 작업·시험 채널 접근을 중지하고 배정을 회수합니다. 연결된 PC는 정책 만료 후 앱 소유 연결을 회수할 수 있으며 즉시 전체 네트워크 차단을 보장하지 않습니다.\n\n설정·자격 증명과 장치 기록을 영구 삭제하지 않습니다. 복원할 때는 보관된 PC를 표시해 다시 승인·배정하세요.");if(review.ShowDialog(this)!=DialogResult.OK)return;await Wire.Read<System.Text.Json.JsonElement>(await http.PostAsync($"api/admin/devices/{d.Id}/archive",null));notice.Text="등록을 보관했습니다. PC가 오프라인이면 연결 회수 여부를 별도로 확인하세요.";await RefreshData();}
    private async Task SendControl(string operation,string text){var d=SelectedDevice();var title=Operation(operation);var impact=operation=="Quarantine"?"\n앱이 만든 공유 연결을 회수합니다. 강제 해제를 선택한 PC는 열린 파일의 미저장 작업에 영향을 줄 수 있습니다. 전체 네트워크 차단은 구현되지 않았습니다.":operation=="ReleaseQuarantine"?"\n보호 잠금 해제를 요청합니다. 탐지 상태 확인이 필요하며 폴더 연결 재개는 별도 확인하세요.":"\n알림함 수신과 직원의 읽음은 서로 다릅니다.";using var review=new ImpactReview(title+" 확인",Target(d)+impact);if(review.ShowDialog(this)!=DialogResult.OK)return;await Wire.Read<CommandView>(await http.PostAsJsonAsync("api/admin/commands",new CreateCommand(d.Id,"",Operation:operation,Text:text),Wire.Json));notice.Text=title+" 요청을 접수했습니다. 작업 내역에서 결과를 확인하세요.";await RefreshData();}
    private static string Operation(string code)=>code switch{"Notify"=>"회사 알림 전송","Quarantine"=>"보호 연결 차단","ReleaseQuarantine"=>"보호 잠금 해제","ReconnectAssigned"=>"배정 폴더 복구","ConnectApprovedSmb"=>"개별 공유 연결","ConnectProfile"=>"모의 연결 시험",_=>code};
    private static string State(string code)=>code switch{"Queued"=>"접수·대기","Leased"=>"PC 전달됨","Running"=>"실행 중","Succeeded"=>"처리 완료","Failed"=>"실패·부분 처리","Expired"=>"시작 전 만료","Rejected"=>"거절됨","Unknown"=>"결과 재확인 필요",_=>code};
    private void ShowDetails(){if(devices.CurrentRow?.Cells["Id"].Value?.ToString() is not string id)return;var d=latestDevices.FirstOrDefault(x=>x.Id==id);if(d is null)return;
        var shares=d.Assignment is null?[]:latestProfiles.Where(p=>ProfileRules.Available(p)&&(d.Assignment.AccessRole=="임원"||d.Assignment.ProfileIds.Contains(p.Id))).ToArray();
        var accounts=shares.GroupBy(p=>p.Unc.Split('\\')[2],StringComparer.OrdinalIgnoreCase).Select(g=>$"{g.Key}: {credentials.FirstOrDefault(c=>c.Department==d.Assignment?.Department&&string.Equals(c.Server,g.Key,StringComparison.OrdinalIgnoreCase))?.Account??d.Assignment?.ExpectedAccount} {(credentials.Any(c=>c.Department==d.Assignment?.Department&&string.Equals(c.Server,g.Key,StringComparison.OrdinalIgnoreCase))?"(관리 PC 저장)":"(Windows 저장 계정 확인 필요)")}");
        detail.Text=(serverFresh?"":"[이전 조회 정보 — 현재 상태 미확인]\r\n")+Target(d)+$"\r\n\r\n준비: {Preparation(d)}\r\n배정 방식: {(d.Assignment?.FollowDepartment==true?"부서 기본값 따름":"PC 개별 설정")}\r\n기본 계정: {d.Assignment?.ExpectedAccount??"미배정"}\r\n실제 적용할 서버별 계정:\r\n{string.Join("\r\n",accounts)}\r\n배정 개정: {d.Assignment?.Revision.ToString()??"없음"} / PC 적용 개정: {d.AppliedPolicyRevision}\r\n허용 폴더: {string.Join(" / ",shares.Select(p=>p.Name))}\r\n\r\n실제 연결 보고:\r\n{string.Join("\r\n",(d.Connections??[]).Select(c=>$"{c.Drive} {c.Unc}\r\n{c.Account} · {c.AssignmentState}"))}\r\n\r\n정정 요청: {(d.Correction is null?"없음":d.Correction.Name+" / "+d.Correction.Department)}\r\n마지막 PC 통신: {(d.LastSeen==DateTimeOffset.MinValue?"없음":d.LastSeen.ToLocalTime().ToString("MM-dd HH:mm:ss"))}\r\nIP (PC 보고): {string.Join(" / ",d.Hardware?.IpAddresses??[])}\r\n장비 UUID: {d.Hardware?.SystemUuid??"미확인"}\r\n\r\n공유폴더의 실제 읽기·쓰기·삭제 권한은 서버 권한 설정을 별도로 확인해야 합니다.";
    }
    private async Task RefreshData(){if(refreshing||IsDisposed)return;refreshing=true;try{
        var list=await http.GetFromJsonAsync<DeviceView[]>("api/admin/devices",Wire.Json)??[];var jobs=await http.GetFromJsonAsync<CommandView[]>("api/admin/commands",Wire.Json)??[];var profiles=await http.GetFromJsonAsync<Profile[]>("api/admin/profiles",Wire.Json)??[];var accounts=await http.GetFromJsonAsync<CredentialSummary[]>("api/admin/share-credentials",Wire.Json)??[];var auditEntries=await http.GetFromJsonAsync<AuditEntry[]>("api/admin/audit",Wire.Json)??[];if(IsDisposed)return;
        var auditSignature=System.Text.Json.JsonSerializer.Serialize(auditEntries,Wire.Json);if(auditSignature!=lastAudit){audit.DataSource=auditEntries.Select(e=>new{시각=e.At.ToLocalTime().ToString("MM-dd HH:mm:ss"),작업=e.Action,대상=e.Target,내용=e.Summary}).ToArray();lastAudit=auditSignature;}
        latestDevices=list;latestProfiles=profiles;credentials=accounts;serverFresh=true;lastRefresh=DateTimeOffset.UtcNow;devices.Enabled=true;devices.DefaultCellStyle.ForeColor=UiTheme.Ink;summary.Text=$"서버 연결됨 · PC {list.Count(d=>!d.Archived)}대 · 사용 준비 {list.Count(d=>Preparation(d)=="사용 준비 완료")}대 · 갱신 {lastRefresh.ToLocalTime():HH:mm:ss}";
        var visible=list.Where(d=>showArchived.Checked||!d.Archived).ToArray();var signature=System.Text.Json.JsonSerializer.Serialize(visible,Wire.Json);
        if(signature!=lastRows){var selected=devices.CurrentRow?.Cells["Id"].Value?.ToString();var scroll=devices.FirstDisplayedScrollingRowIndex;
            devices.DataSource=visible.Select(d=>new{d.Id,성명=d.Name,컴퓨터=d.ComputerName.Length>0?d.ComputerName:"이전 등록",부서=d.Assignment?.Department??(d.RequestedDepartment.Length>0?d.RequestedDepartment+" (요청)":"미배정"),준비상태=Preparation(d)+(d.Correction is not null?" · 정정 요청":""),연결=d.Online?$"정상 {(d.Connections??[]).Count(c=>c.ProfileId.Length>0&&c.ConnectionState=="Connected")} / 확인 {(d.Connections??[]).Count(c=>c.ProfileId.Length>0&&c.ConnectionState!="Connected")}":"현재 확인 불가"}).ToArray();if(devices.Columns.Contains("Id"))devices.Columns["Id"]!.Visible=false;
            foreach(DataGridViewColumn col in devices.Columns){col.MinimumWidth=65;col.SortMode=DataGridViewColumnSortMode.NotSortable;}if(devices.Columns.Contains("준비상태"))devices.Columns["준비상태"]!.FillWeight=180;
            foreach(DataGridViewRow row in devices.Rows)if(row.Cells["Id"].Value?.ToString()==selected){devices.ClearSelection();devices.CurrentCell=row.Cells[1];row.Selected=true;break;}if(scroll>=0&&scroll<devices.Rows.Count)devices.FirstDisplayedScrollingRowIndex=scroll;lastRows=signature;
        }
        ShowDetails();var jobSignature=System.Text.Json.JsonSerializer.Serialize(jobs,Wire.Json);if(jobSignature!=lastJobs){commands.DataSource=jobs.OrderByDescending(j=>j.IssuedAt).Select(j=>new{요청시각=j.IssuedAt.ToLocalTime().ToString("MM-dd HH:mm:ss"),대상=j.DeviceName,작업=Operation(latestProfiles.FirstOrDefault(p=>p.Id==j.ProfileId)?.Name??j.ProfileId),상태=State(j.State),시작기한=j.ExpiresAt.ToLocalTime().ToString("HH:mm:ss"),상세=j.Detail}).ToArray();if(commands.Columns.Contains("상세"))commands.Columns["상세"]!.FillWeight=220;lastJobs=jobSignature;}
    }catch(Exception e){if(!IsDisposed){serverFresh=false;summary.Text="관리 서버 연결 끊김 · 표시 정보는 현재 상태가 아닙니다"+(lastRefresh==default?"":$" · 마지막 갱신 {lastRefresh.ToLocalTime():MM-dd HH:mm:ss}");devices.DefaultCellStyle.ForeColor=Color.Gray;notice.Text=UiTheme.Error(e)+"\r\n목록은 이전 조회 결과입니다. 새로고침으로 서버 연결을 확인하세요.";ShowDetails();}}finally{refreshing=false;}}
}
internal sealed class CorrectionReview : Form
{
    public bool Accepted{get;private set;}
    public CorrectionReview(DeviceView device){Text="등록 정보 정정 검토";UiTheme.Form(this,new(650,500),new(540,420));var root=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new(16),ColumnCount=1,RowCount=2};root.RowStyles.Add(new(SizeType.Percent,100));root.RowStyles.Add(new(SizeType.AutoSize));Controls.Add(root);root.Controls.Add(new TextBox{Dock=DockStyle.Fill,ReadOnly=true,Multiline=true,Text=$"현재 성명: {device.Name}\r\n요청 성명: {device.Correction?.Name}\r\n요청 부서: {device.Correction?.Department}\r\n현재 배정 부서: {device.Assignment?.Department??"미배정"}\r\n장치: {device.ComputerName} / {device.Id}\r\n\r\n수락은 등록 정보만 정정하며 실제 공유 권한을 자동 변경하지 않습니다. 부서가 바뀌었다면 PC 배정 화면에서 소속·폴더를 검토하세요."},0,0);var buttons=new FlowLayoutPanel{Dock=DockStyle.Fill,AutoSize=true};foreach(var accept in new[]{true,false}){var b=new Button{Text=accept?"정정 수락":"정정 거절"};UiTheme.Button(b,accept);b.Click+=(_,_)=>{Accepted=accept;DialogResult=DialogResult.OK;Close();};buttons.Controls.Add(b);}var cancel=new Button{Text="취소",DialogResult=DialogResult.Cancel};UiTheme.Button(cancel);buttons.Controls.Add(cancel);root.Controls.Add(buttons,0,1);CancelButton=cancel;}
}
