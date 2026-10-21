using System.Net.Http.Json;
using SmbManager.Local;

namespace SmbManager.Local.AdminDesktop;

internal sealed class ProfileManager : Form
{
    private readonly ListBox list = new() { Dock = DockStyle.Fill, DisplayMember = nameof(Profile.Name) };
    private readonly TextBox name = new() { Dock = DockStyle.Fill };
    private readonly TextBox unc = new() { Dock = DockStyle.Fill };
    private readonly TextBox department = new() { Dock = DockStyle.Fill };
    private readonly TextBox drive = new() { Dock = DockStyle.Fill, Text = "P:" };
    private readonly Label feedback = new() { AutoSize = true };
    private Profile? editing;
    public ProfileManager(HttpClient http)
    {
        bool dirty=false,loading=false,busy=false,discard=false;
        Text = "공유폴더 추가·관리";
        UiTheme.Form(this,new(820,680),new(700,580));Shown+=(_,_)=>UiTheme.Descendants(this);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(18), ColumnCount = 2, RowCount = 8, AutoScroll = true };
        root.ColumnStyles.Add(new(SizeType.Absolute, 130)); root.ColumnStyles.Add(new(SizeType.Percent, 100));
        root.RowStyles.Add(new(SizeType.Percent, 100)); for (var i = 1; i < 8; i++) root.RowStyles.Add(new(SizeType.AutoSize));
        Controls.Add(root); root.Controls.Add(list, 0, 0); root.SetColumnSpan(list, 2);
        var fields = new[] { ("표시 이름", name), ("공유폴더 경로", unc), ("드라이브", drive), ("분류 부서 / 공용", department) };
        for (var i = 0; i < fields.Length; i++)
        {
            root.Controls.Add(new Label { Text = fields[i].Item1, AutoSize = true, Padding = new(0, 6, 0, 6) }, 0, i + 1);
            fields[i].Item2.Margin = new(3, 6, 3, 6); root.Controls.Add(fields[i].Item2, 1, i + 1);
        }
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        var add = new Button { Text = "새 프로필", AutoSize = true, MinimumSize = new(130, 38) };
        var save = new Button { Text = "저장", AutoSize = true, MinimumSize = new(130, 38) };
        var toggle=new Button{Text="사용 중지·복원",AutoSize=true,MinimumSize=new(130,38)};buttons.Controls.Add(add); buttons.Controls.Add(save);buttons.Controls.Add(toggle); root.Controls.Add(buttons, 0, 5); root.SetColumnSpan(buttons, 2);
        root.Controls.Add(feedback, 0, 6); root.SetColumnSpan(feedback, 2);
        root.Controls.Add(new Label { AutoSize = true, Text = "공유 경로 예: \\\\fileserver01\\sales\\reports\n기본 시험 항목은 편집할 수 없습니다. 등록된 모든 공유를 부서별 서버 설정에서 선택해 배정할 수 있습니다.", Padding = new(0, 8, 0, 8) }, 0, 7); root.SetColumnSpan(root.GetControlFromPosition(0, 7)!, 2);
        async Task Reload() { loading=true;try{list.DataSource = await http.GetFromJsonAsync<Profile[]>("api/admin/profiles", Wire.Json) ?? [];}finally{loading=false;} }
        list.FormattingEnabled=true;list.Format+=(_,e)=>{if(e.ListItem is Profile p)e.Value=p.Name+(p.Mode=="Disabled"?" (사용 중지)":"");};
        void Fill(){loading=true;try{department.Text=editing?.Department??"";name.Text=editing?.Name??"";unc.Text=editing?.Unc??"";drive.Text=editing?.Drive??"P:";feedback.Text=editing is null?"새 공유폴더":$"개정 {editing.Revision} · {(editing.Mode=="Disabled"?"사용 중지 — 다시 사용해도 직원별 재배정 필요":"사용 중")} (저장은 즉시 연결 완료를 의미하지 않습니다.)";dirty=false;}finally{loading=false;}}
        bool Discard()=>!dirty||MessageBox.Show(this,"공유폴더 변경을 저장하지 않고 이동할까요?","미저장 변경",MessageBoxButtons.YesNo)==DialogResult.Yes;
        foreach(var field in new[]{name,unc,department,drive})field.TextChanged+=(_,_)=>{if(!loading)dirty=true;};
        list.SelectedIndexChanged += (_, _) => {if(loading)return;var chosen=list.SelectedItem as Profile;if(!Discard()){loading=true;list.SelectedItem=editing;loading=false;return;}editing=chosen;Fill();};
        add.Click += (_, _) => {if(!Discard())return;loading=true;list.ClearSelected();loading=false;editing=null;Fill();};
        toggle.Click+=async(_,_)=>{if(editing is null){feedback.Text="기존 공유폴더를 먼저 선택하세요.";return;}if(dirty){feedback.Text="입력 중인 변경을 먼저 저장하거나 취소하세요.";return;}busy=true;toggle.Enabled=false;try{var impact=await http.GetFromJsonAsync<ProfileImpact>($"api/admin/profiles/{editing.Id}/impact",Wire.Json)??throw new InvalidOperationException("영향 대상을 확인하지 못했습니다.");var enable=editing.Mode=="Disabled";using var review=new ImpactReview(enable?"공유 다시 사용":"공유 사용 중지",$"공유: {editing.Name}\n경로: {editing.Unc}\n영향 PC:\n{string.Join("\n",impact.Devices)}\n부서: {string.Join(" / ",impact.Departments)}\n\n"+(enable?"목록에서 다시 선택할 수 있게 합니다. 임원은 전체 공유 정책에 따라 접근하며 직원은 별도 재배정이 필요합니다.":"새 배정에서 제외하고 기존 배정에서도 회수합니다. 실제 서버의 폴더·파일은 삭제하지 않습니다. 열린 파일과 오프라인 PC의 회수 결과는 별도 확인해야 합니다."));if(review.ShowDialog(this)!=DialogResult.OK)return;var changed=await Wire.Read<Profile>(await http.PostAsJsonAsync($"api/admin/profiles/{editing.Id}/status",new ProfileStatusChange(enable,editing.Revision,impact.Revision),Wire.Json));await Reload();editing=((Profile[])list.DataSource!).Single(p=>p.Id==changed.Id);loading=true;list.SelectedItem=editing;loading=false;Fill();}catch(Exception e){feedback.Text=UiTheme.Error(e);}finally{busy=false;toggle.Enabled=true;}};
        save.Click += async (_, _) =>
        {
            if(busy)return;busy=true;save.Enabled = false;
            try
            {
                if(!AssignmentRules.ValidDepartment(department.Text.Trim()))throw new InvalidOperationException("공유의 소속 부서 또는 공용을 입력하세요.");
                var request = new SaveProfile(name.Text.Trim(), unc.Text.Trim(), drive.Text.Trim().ToUpperInvariant(), editing?.Revision ?? 0, department.Text.Trim());
                if(editing is not null&&(editing.Unc!=request.Unc||editing.Drive!=request.Drive)){
                    var impact=await http.GetFromJsonAsync<ProfileImpact>($"api/admin/profiles/{editing.Id}/impact",Wire.Json)??throw new InvalidOperationException("영향을 확인하지 못했습니다.");
                    using var review=new ImpactReview("공유 경로 변경 영향",$"공유: {editing.Name}\n기존: {editing.Drive} {editing.Unc}\n변경: {request.Drive} {request.Unc}\n영향 PC:\n{string.Join("\n",impact.Devices)}\n\n연결이 회수·재설정될 수 있습니다. 서버가 달라지면 기존 암호는 새 서버로 옮기지 않습니다. 저장할까요?");if(review.ShowDialog(this)!=DialogResult.OK)return;
                }
                var response = editing is null ? await http.PostAsJsonAsync("api/admin/profiles", request, Wire.Json)
                    : await http.PutAsJsonAsync("api/admin/profiles/" + editing.Id, request, Wire.Json);
                var saved = await Wire.Read<Profile>(response); dirty=false;await Reload();
                editing=((Profile[])list.DataSource!).First(p=>p.Id==saved.Id);loading=true;list.SelectedItem=editing;loading=false;Fill();feedback.Text="공유폴더를 서버에 저장했습니다. 부서 배정과 실제 연결 상태를 확인하세요.";
            }
            catch (Exception e) { feedback.Text = "저장 실패: " + UiTheme.Error(e); }
            finally { busy=false;if (!save.IsDisposed) save.Enabled = true; }
        };
        Shown += async (_, _) => { try { await Reload();editing=list.SelectedItem as Profile;Fill(); } catch (Exception e) { feedback.Text = UiTheme.Error(e); } };
        FormClosing+=(_,e)=>{if(busy)e.Cancel=true;else if(e.CloseReason==CloseReason.UserClosing&&!discard&&!Discard())e.Cancel=true;};
    }
}

internal sealed class TextRequest : Form
{
    private readonly TextBox text = new() { Dock = DockStyle.Fill, Multiline = true, AcceptsReturn = true, MaxLength = 1000, ScrollBars = ScrollBars.Vertical };
    public string Value => text.Text.Trim();
    public TextRequest(string title, string prompt)
    {
        Text = title; UiTheme.Form(this,new(560,380),new(450,320));Shown+=(_,_)=>UiTheme.Descendants(this);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(14), ColumnCount = 1, RowCount = 3 };
        root.RowStyles.Add(new(SizeType.AutoSize)); root.RowStyles.Add(new(SizeType.Percent, 100)); root.RowStyles.Add(new(SizeType.AutoSize));
        Controls.Add(root); root.Controls.Add(new Label { Text = prompt, AutoSize = true }, 0, 0); root.Controls.Add(text, 0, 1);
        var ok = new Button { Text = "보내기", AutoSize = true, Height = 38, DialogResult = DialogResult.OK }; root.Controls.Add(ok, 0, 2); AcceptButton = ok;
    }
}
