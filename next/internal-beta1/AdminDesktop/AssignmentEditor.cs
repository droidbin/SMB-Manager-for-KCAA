using System.Net.Http.Json;
using SmbManager.Local;
namespace SmbManager.Local.AdminDesktop;

internal sealed class AssignmentEditor : Form
{
    private sealed record Choice(Profile Profile) { public override string ToString() => $"{Profile.Name} · {Profile.Department} · {Profile.Drive} {Profile.Unc}"; }
    public AssignmentEditor(HttpClient http, DeviceView device, Profile[] profiles, Dictionary<string,string> departmentAccounts)
    {
        Text=$"{device.Name} — 부서·공유 권한 배정";UiTheme.Form(this,new(1000,820),new(820,700));Shown+=(_,_)=>UiTheme.Descendants(this);
        var root=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new(16),ColumnCount=1,RowCount=7,AutoScroll=true};
        root.RowStyles.Add(new(SizeType.AutoSize));root.RowStyles.Add(new(SizeType.AutoSize));root.RowStyles.Add(new(SizeType.Percent,45));
        root.RowStyles.Add(new(SizeType.AutoSize));root.RowStyles.Add(new(SizeType.Percent,55));root.RowStyles.Add(new(SizeType.AutoSize));root.RowStyles.Add(new(SizeType.AutoSize));Controls.Add(root);
        var intro=new Label{AutoSize=true,Text=$"PC: {device.Name} / Windows 사용자: {device.User}\n관리자가 저장하면 직원의 추가 승인 없이 연결합니다. 읽기·쓰기 권한은 공유 서버 설정이 최종 기준입니다."};
        root.Controls.Add(intro,0,0);root.SizeChanged+=(_,_)=>intro.MaximumSize=new(Math.Max(300,root.ClientSize.Width-40),0);
        var fields=new TableLayoutPanel{Dock=DockStyle.Fill,AutoSize=true,ColumnCount=2};fields.ColumnStyles.Add(new(SizeType.Absolute,130));fields.ColumnStyles.Add(new(SizeType.Percent,100));
        var department=new ComboBox{Dock=DockStyle.Fill,DropDownStyle=ComboBoxStyle.DropDown};
        department.Items.AddRange(profiles.Select(p=>p.Department).Where(x=>!string.IsNullOrEmpty(x)&&x!="공용").Distinct().Cast<object>().ToArray());
        department.Items.Add("임원"); foreach(var value in departmentAccounts.Keys.Append(device.Assignment?.Department??device.RequestedDepartment))if(value.Length>0&&!department.Items.Contains(value))department.Items.Add(value); department.Text=device.Assignment?.Department??device.RequestedDepartment;
        var role=new ComboBox{Dock=DockStyle.Fill,DropDownStyle=ComboBoxStyle.DropDownList};role.Items.AddRange(new object[]{"직원","임원"});role.SelectedItem=device.Assignment?.AccessRole??"직원";fields.Controls.Add(new Label{Text="사용자 권한",AutoSize=true},0,3);fields.Controls.Add(role,1,3); var account=new ComboBox{Dock=DockStyle.Fill,DropDownStyle=ComboBoxStyle.DropDown};
        account.Items.AddRange((device.Connections??[]).Select(c=>c.Account).Where(x=>!x.StartsWith("확인 불가")).Distinct().Cast<object>().ToArray());
        account.Text=device.Assignment?.ExpectedAccount??"";
        fields.Controls.Add(new Label{Text="PC 부서",AutoSize=true},0,0);fields.Controls.Add(department,1,0);
        fields.Controls.Add(new Label{Text=@"Drive 계정",AutoSize=true},0,1);fields.Controls.Add(account,1,1);
        fields.Controls.Add(new Label{Text=@"서버이름\계정 형태로 지정합니다. 실제 연결 계정과 다르면 연결 실패·경고로 보고합니다.",AutoSize=true},1,2);root.Controls.Add(fields,0,1);
        var shares=new CheckedListBox{Dock=DockStyle.Fill,CheckOnClick=true,HorizontalScrollbar=true};
        void Fill(){shares.Items.Clear();foreach(var p in profiles.Where(p=>ProfileRules.Available(p)))shares.Items.Add(new Choice(p),department.Text==device.Assignment?.Department&&(device.Assignment?.ProfileIds.Contains(p.Id)??false));}
        department.TextChanged+=(_,_)=>{Fill();if(departmentAccounts.TryGetValue(department.Text.Trim(),out var fixedAccount)){account.Text=fixedAccount;account.Enabled=false;}else{account.Enabled=true;account.Text=department.Text==device.Assignment?.Department?device.Assignment.ExpectedAccount:"";}};
        if(departmentAccounts.TryGetValue(department.Text.Trim(),out var currentAccount)){account.Text=currentAccount;account.Enabled=false;}Fill();root.Controls.Add(shares,0,2);
        role.SelectedIndexChanged+=(_,_)=>{if(role.Text=="임원")for(var i=0;i<shares.Items.Count;i++)shares.SetItemChecked(i,true);shares.Enabled=role.Text!="임원";};
        Shown+=async(_,_)=>{try{var templates=await http.GetFromJsonAsync<DepartmentTemplate[]>("api/admin/department-templates",Wire.Json)??[];void ApplyTemplate(){var t=templates.FirstOrDefault(t=>t.Department==department.Text);if(t is null)return;account.Text=t.ExpectedAccount;role.SelectedItem=t.AccessRole;for(var i=0;i<shares.Items.Count;i++)shares.SetItemChecked(i,t.AccessRole=="임원"||t.ProfileIds.Contains(((Choice)shares.Items[i]).Profile.Id));}department.TextChanged+=(_,_)=>ApplyTemplate();if(device.Assignment is null)ApplyTemplate();}catch(Exception e){MessageBox.Show(this,UiTheme.Error(e),"부서 설정 확인 실패");}};
        root.Controls.Add(new Label{Text="실제 연결 현황 (클라이언트 보고) — 배정과 일치해도 공유폴더의 실제 읽기·쓰기 ACL은 별도 확인이 필요합니다.",AutoSize=true},0,3);
        var actual=new DataGridView{Dock=DockStyle.Fill,ReadOnly=true,AllowUserToAddRows=false,AllowUserToDeleteRows=false,RowHeadersVisible=false,AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill,AutoSizeRowsMode=DataGridViewAutoSizeRowsMode.AllCells,DefaultCellStyle=new DataGridViewCellStyle{WrapMode=DataGridViewTriState.True}};
        actual.DataSource=(device.Connections??[]).Select(c=>new {드라이브=c.Drive,공유=c.Unc,연결계정=c.Account,배정상태=c.AssignmentState,소유=c.AppOwned?"앱":"외부",실제권한=c.EffectiveAccess}).ToArray();root.Controls.Add(actual,0,4);
        var options=new FlowLayoutPanel{Dock=DockStyle.Fill,AutoSize=true};
        var follow=new CheckBox{Text="부서 기본 설정 따름 (해제하면 PC 개별 설정)",Checked=device.Assignment?.FollowDepartment??false,AutoSize=true};options.Controls.Add(follow);
        var reconnect=new CheckBox{Text="끊기면 자동 재연결",Checked=device.Assignment?.Reconnect??true,AutoSize=true};
        var force=new CheckBox{Text="격리·회수 시 열린 파일도 강제 해제",Checked=device.Assignment?.ForceDisconnect??false,AutoSize=true};
        var resume=new CheckBox{Text="이 PC의 연결 중지 상태 해제",AutoSize=true};options.Controls.AddRange([reconnect,force,resume]);root.Controls.Add(options,0,5);
        bool dirty=false,discard=false,saving=false;int templateLoads=0;
        var changeAccount=new Button{Text="부서 전체 계정 변경",AutoSize=true};// Server accounts are managed in the department screen; this compatibility action stays hidden.
        changeAccount.Click+=async(_,_)=>{
            var dept=department.Text.Trim();if(!departmentAccounts.TryGetValue(dept,out var oldAccount)){MessageBox.Show(this,"새 부서는 먼저 PC 배정을 저장하세요.");return;}
            using var input=new TextRequest("부서 전체 Drive 계정 변경",$"{dept}의 현재 계정: {oldAccount}\n새 Drive 계정 표기를 입력하세요. 이 부서에 배정된 모든 PC에 적용됩니다.");
            if(input.ShowDialog(this)!=DialogResult.OK)return;
            if(MessageBox.Show(this,$"{dept} 전체 PC의 지정 계정을 {input.Value}(으)로 변경합니다.\n기존 앱 연결을 회수하고 재연결하며 Drive 자격 증명이 준비되어 있어야 합니다.","부서 계정 변경",MessageBoxButtons.YesNo)!=DialogResult.Yes)return;
            changeAccount.Enabled=false;
            try{using var response=await http.PostAsJsonAsync("api/admin/department-account",new DepartmentAccountChange(dept,oldAccount,input.Value),Wire.Json);response.EnsureSuccessStatusCode();saving=false;discard=true;DialogResult=DialogResult.OK;Close();}
            catch(Exception e){MessageBox.Show(this,UiTheme.Error(e),"부서 계정 변경 실패");}
            finally{if(!IsDisposed)changeAccount.Enabled=true;}
        };
        var save=new Button{Text="배정 저장 · 자동 적용",AutoSize=true,MinimumSize=new(200,40)};root.Controls.Add(save,0,6);
        async Task RefreshFollowTemplate(){
            if(!follow.Checked)return;templateLoads++;save.Enabled=false;var requestedDepartment=department.Text.Trim();
            try{var values=await http.GetFromJsonAsync<DepartmentTemplate[]>("api/admin/department-templates",Wire.Json)??[];
                if(IsDisposed||!follow.Checked||department.Text.Trim()!=requestedDepartment)return;
                var t=values.FirstOrDefault(t=>t.Department==requestedDepartment);
                if(t is null){follow.Checked=false;MessageBox.Show(this,"먼저 이 부서의 기본 서버·계정 설정을 저장하세요.");return;}
                account.Text=t.ExpectedAccount;role.SelectedItem=t.AccessRole;for(var i=0;i<shares.Items.Count;i++)shares.SetItemChecked(i,t.AccessRole=="임원"||t.ProfileIds.Contains(((Choice)shares.Items[i]).Profile.Id));
                shares.Enabled=false;role.Enabled=false;
            }catch(Exception e){if(!IsDisposed){follow.Checked=false;MessageBox.Show(this,UiTheme.Error(e),"부서 기본 설정 확인");}}
            finally{templateLoads--;if(!IsDisposed)save.Enabled=!saving&&templateLoads==0;}
        }
        follow.CheckedChanged+=async(_,_)=>await RefreshFollowTemplate();department.TextChanged+=async(_,_)=>await RefreshFollowTemplate();Shown+=async(_,_)=>await RefreshFollowTemplate();
        foreach(var control in new Control[]{department,account,role})control.TextChanged+=(_,_)=>dirty=true;
        shares.ItemCheck+=(_,_)=>dirty=true;foreach(var item in new[]{follow,reconnect,force,resume})item.CheckedChanged+=(_,_)=>dirty=true;
        follow.CheckedChanged+=(_,_)=>{shares.Enabled=!follow.Checked&&role.Text!="임원";role.Enabled=!follow.Checked;};
        Shown+=(_,_)=>{shares.Enabled=!follow.Checked&&role.Text!="임원";role.Enabled=!follow.Checked;dirty=false;};
        FormClosing+=(_,e)=>{if(saving||templateLoads>0){e.Cancel=true;return;}if(e.CloseReason==CloseReason.UserClosing&&dirty&&!discard&&MessageBox.Show(this,"배정 변경을 저장하지 않고 닫을까요?","미저장 배정",MessageBoxButtons.YesNo)!=DialogResult.Yes)e.Cancel=true;};
        save.Click+=async(_,_)=>{
            if(saving||templateLoads>0)return;saving=true;save.Enabled=false;
            try {
                if(follow.Checked){await RefreshFollowTemplate();if(!follow.Checked)return;}
                var request=new SaveAssignment(department.Text.Trim(),account.Text.Trim(),shares.CheckedItems.Cast<Choice>().Select(c=>c.Profile.Id).ToArray(),reconnect.Checked,force.Checked,device.Assignment?.Revision??0,resume.Checked,role.Text,follow.Checked);
                using var review=new ImpactReview("PC 배정 변경 확인",$"대상 성명: {device.Name}\n컴퓨터: {device.ComputerName}\n장치: {device.Id}\n부서: {request.Department}\n방식: {(follow.Checked?"부서 기본 설정 따름 — 서버에 저장된 기본 계정·폴더·역할을 적용":"PC 개별 설정")}\n폴더: {string.Join(" / ",shares.CheckedItems.Cast<Choice>().Select(c=>c.Profile.Name))}\n자동 재연결: {reconnect.Checked}\n열린 파일 강제 해제: {force.Checked}\n\n서버에 저장할까요? 실제 PC 적용·연결 완료는 메인 화면에서 확인하세요.");if(review.ShowDialog(this)!=DialogResult.OK)return;
                await Wire.Read<DeviceAssignment>(await http.PostAsJsonAsync($"api/admin/devices/{device.Id}/assignment",request,Wire.Json));
                saving=false;discard=true;DialogResult=DialogResult.OK;Close();
            } catch(Exception e){MessageBox.Show(this,UiTheme.Error(e),"배정 저장 실패");}
            finally{saving=false;if(!IsDisposed)save.Enabled=true;}
        };
    }
}

