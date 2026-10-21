using System.Net.Http.Json;
using SmbManager.Local;
namespace SmbManager.Local.AdminDesktop;
internal sealed class DepartmentManager : Form
{
    private sealed record Choice(Profile P){public override string ToString()=>$"{P.Name} · {P.Drive} {P.Unc}";}
    private sealed record ServerChoice(Profile P){public override string ToString()=>P.Unc.Split('\\')[2];}
    public DepartmentManager(HttpClient http){
        Text="부서 기본 설정 · 서버별 계정";UiTheme.Form(this,new(960,920),new(800,760));
        var root=new TableLayoutPanel{Dock=DockStyle.Fill,AutoScroll=true,Padding=new(16),ColumnCount=2};root.ColumnStyles.Add(new(SizeType.Absolute,140));root.ColumnStyles.Add(new(SizeType.Percent,100));Controls.Add(root);
        var department=new ComboBox{Dock=DockStyle.Fill,DropDownStyle=ComboBoxStyle.DropDownList};
        var role=new ComboBox{Dock=DockStyle.Fill,DropDownStyle=ComboBoxStyle.DropDownList};role.Items.AddRange(new object[]{"직원","임원"});role.SelectedIndex=0;
        var account=new TextBox{Dock=DockStyle.Fill,PlaceholderText=@"서버이름\부서계정"};
        var shares=new CheckedListBox{Dock=DockStyle.Fill,CheckOnClick=true,HorizontalScrollbar=true,MinimumSize=new(0,180),Height=210};
        var includeIndividual=new CheckBox{Text="PC 개별 설정도 부서 기본값으로 변경 (기본: 보존)",AutoSize=true};
        var includePending=new CheckBox{Text="이미 승인됐지만 미배정인 요청 PC도 검토 후 배정",AutoSize=true};
        var credentialServer=new ComboBox{Dock=DockStyle.Fill,DropDownStyle=ComboBoxStyle.DropDownList};
        var credentialAccount=new TextBox{Dock=DockStyle.Fill,PlaceholderText=@"이 서버에 사용할 실제 계정"};
        var password=new TextBox{Dock=DockStyle.Fill,UseSystemPasswordChar=true,MaxLength=256,PlaceholderText="새 암호를 입력하세요. 저장된 암호는 표시하지 않습니다."};
        var showPassword=new CheckBox{Text="암호 표시·한글 입력",AutoSize=true};
        var passwordPanel=new TableLayoutPanel{Dock=DockStyle.Fill,AutoSize=true,ColumnCount=1};passwordPanel.Controls.Add(password);passwordPanel.Controls.Add(showPassword);passwordPanel.Controls.Add(new Label{Text="한글 암호는 암호 표시를 켜고 입력하거나 붙여넣으세요.",AutoSize=true});
        showPassword.CheckedChanged+=(_,_)=>{password.UseSystemPasswordChar=!showPassword.Checked;if(showPassword.Checked)password.ImeMode=ImeMode.On;password.Focus();password.SelectionStart=password.TextLength;};
        FormClosed+=(_,_)=>password.Clear();
        var credentialInfo=UiTheme.Hint("서버를 선택하면 저장된 계정과 적용 폴더를 확인합니다.");
        var status=UiTheme.Hint("부서 기본 설정과 서버 계정은 각각 저장합니다.");
        var save=new Button{Text="영향 확인 후 부서 설정 저장"};var saveCredential=new Button{Text="영향 확인 후 서버 계정 저장"};UiTheme.Button(save,true);UiTheme.Button(saveCredential);
        var add=new Button{Text="새 부서 만들기"};UiTheme.Button(add);
        var selector=new TableLayoutPanel{Dock=DockStyle.Fill,AutoSize=true,ColumnCount=2};selector.ColumnStyles.Add(new(SizeType.Percent,100));selector.ColumnStyles.Add(new(SizeType.AutoSize));selector.Controls.Add(department,0,0);selector.Controls.Add(add,1,0);
        void Field(int row,string text,Control value){root.RowStyles.Add(new(row==3?SizeType.Percent:SizeType.AutoSize,row==3?100:0));root.Controls.Add(new Label{Text=text,AutoSize=true,Padding=new(0,8,0,8)},0,row);value.Margin=new(3,6,3,6);root.Controls.Add(value,1,row);}
        Field(0,"부서",selector);Field(1,"공유 접근 역할",role);Field(2,"기본 Drive 계정",account);Field(3,"기본 공유폴더",shares);
        var options=new FlowLayoutPanel{Dock=DockStyle.Fill,AutoSize=true,FlowDirection=FlowDirection.TopDown,WrapContents=false};options.Controls.Add(includeIndividual);options.Controls.Add(includePending);Field(4,"적용 범위",options);Field(5,"부서 설정",save);
        Field(6,"계정을 저장할 서버",credentialServer);Field(7,"실제 Drive 계정",credentialAccount);Field(8,"새 Drive 암호",passwordPanel);Field(9,"서버 계정",saveCredential);
        root.Controls.Add(credentialInfo,0,10);root.SetColumnSpan(credentialInfo,2);UiTheme.Wrap(credentialInfo,root);root.Controls.Add(status,0,11);root.SetColumnSpan(status,2);UiTheme.Wrap(status,root);
        var hint=UiTheme.Hint("서버 설정 저장과 직원의 연결 완료는 별도입니다. PC 개별 배정은 기본적으로 유지됩니다. 임원은 모든 사용 중인 공유에 접근하며 서버관리 권한은 없습니다. 동일 서버의 모든 공유에는 같은 계정을 적용합니다. 암호는 관리 PC에 암호화 저장하며 실제 읽기·쓰기 권한은 Drive 서버 설정이 기준입니다.");root.Controls.Add(hint,0,12);root.SetColumnSpan(hint,2);UiTheme.Wrap(hint,root);
        DepartmentTemplate[] templates=[];Profile[] profiles=[];CredentialSummary[] stored=[];bool loading=true,policyDirty=false,credentialDirty=false,busy=false;string selectedDepartment="";int lastServer=-1;int[] staffChecks=[];
        void Dirty(bool credential=false){if(loading)return;if(credential)credentialDirty=true;else policyDirty=true;Text="부서 기본 설정 · 서버별 계정"+((policyDirty||credentialDirty)?" · 미저장 변경 있음":"");}
        bool Discard(string message)=>!(policyDirty||credentialDirty)||MessageBox.Show(this,message,"미저장 변경",MessageBoxButtons.YesNo,MessageBoxIcon.Warning)==DialogResult.Yes;
        void CredentialLoad(){var c=credentialServer.SelectedItem as ServerChoice;var previous=loading;loading=true;var saved=c is null?null:stored.FirstOrDefault(x=>x.Department==department.Text&&string.Equals(x.Server,c.ToString(),StringComparison.OrdinalIgnoreCase));credentialAccount.Text=saved?.Account??account.Text;password.Clear();showPassword.Checked=false;credentialInfo.Text=c is null?"서버를 선택하세요.":$"서버: {c}\n저장 계정: {saved?.Account??"아직 없음"} · 암호: {(saved?.PasswordSaved==true?"등록됨":"미등록 또는 빈 암호")}\n함께 적용되는 공유: {string.Join(" / ",profiles.Where(p=>p.Unc.StartsWith(@"\\"+c+@"\",StringComparison.OrdinalIgnoreCase)).Select(p=>p.Name))}";credentialDirty=false;loading=previous;lastServer=credentialServer.SelectedIndex;}
        void Load(){loading=true;selectedDepartment=department.Text;var t=templates.FirstOrDefault(t=>t.Department==selectedDepartment);account.Text=t?.ExpectedAccount??"";role.SelectedItem=t?.AccessRole??"직원";for(var i=0;i<shares.Items.Count;i++)shares.SetItemChecked(i,role.Text=="임원"||(t?.ProfileIds.Contains(((Choice)shares.Items[i]).P.Id)??false));shares.Enabled=role.Text!="임원";includeIndividual.Checked=includePending.Checked=false;CredentialLoad();policyDirty=credentialDirty=false;loading=false;Text="부서 기본 설정 · 서버별 계정";}
        department.SelectedIndexChanged+=(_,_)=>{if(loading)return;if(!Discard("다른 부서로 이동하면 저장하지 않은 변경이 사라집니다. 이동할까요?")){loading=true;department.SelectedItem=selectedDepartment;loading=false;return;}Load();};
        credentialServer.SelectedIndexChanged+=(_,_)=>{if(loading)return;if(credentialDirty&&MessageBox.Show(this,"입력 중인 서버 계정을 저장하지 않고 서버를 바꿀까요?","미저장 계정",MessageBoxButtons.YesNo)!=DialogResult.Yes){loading=true;credentialServer.SelectedIndex=lastServer;loading=false;return;}CredentialLoad();};
        role.SelectedIndexChanged+=(_,_)=>{if(loading)return;if(role.Text=="임원"){staffChecks=shares.CheckedIndices.Cast<int>().ToArray();for(var i=0;i<shares.Items.Count;i++)shares.SetItemChecked(i,true);}else{for(var i=0;i<shares.Items.Count;i++)shares.SetItemChecked(i,staffChecks.Contains(i));}shares.Enabled=role.Text!="임원";Dirty();};
        account.TextChanged+=(_,_)=>Dirty();shares.ItemCheck+=(_,_)=>Dirty();includeIndividual.CheckedChanged+=(_,_)=>Dirty();includePending.CheckedChanged+=(_,_)=>Dirty();credentialAccount.TextChanged+=(_,_)=>Dirty(true);password.TextChanged+=(_,_)=>Dirty(true);
        add.Click+=(_,_)=>{if(!Discard("새 부서를 만들면 저장하지 않은 변경이 사라집니다. 계속할까요?"))return;using var input=new TextRequest("새 부서 만들기","새 부서 이름을 입력하세요. 저장 전에는 직원에게 권한이 배정되지 않습니다.");if(input.ShowDialog(this)!=DialogResult.OK)return;if(!AssignmentRules.ValidDepartment(input.Value)||input.Value=="공용"){MessageBox.Show(this,"올바른 부서 이름을 입력하세요.");return;}if(!department.Items.Contains(input.Value))department.Items.Add(input.Value);loading=true;department.SelectedItem=input.Value;loading=false;Load();};
        Shown+=async(_,_)=>{try{profiles=(await http.GetFromJsonAsync<Profile[]>("api/admin/profiles",Wire.Json)??[]).Where(p=>ProfileRules.Available(p)).ToArray();foreach(var p in profiles)shares.Items.Add(new Choice(p));foreach(var p in profiles.DistinctBy(p=>p.Unc.Split('\\')[2],StringComparer.OrdinalIgnoreCase))credentialServer.Items.Add(new ServerChoice(p));templates=await http.GetFromJsonAsync<DepartmentTemplate[]>("api/admin/department-templates",Wire.Json)??[];stored=await http.GetFromJsonAsync<CredentialSummary[]>("api/admin/share-credentials",Wire.Json)??[];foreach(var value in new[]{"운영부","사업부","바리스타학과","제과제빵학과","조리학과","임원"}.Concat(templates.Select(t=>t.Department)).Distinct())department.Items.Add(value);if(credentialServer.Items.Count>0)credentialServer.SelectedIndex=0;department.SelectedIndex=0;Load();}catch(Exception e){status.Text=UiTheme.Error(e);}finally{loading=false;}};
        save.Click+=async(_,_)=>{if(busy)return;busy=true;save.Enabled=false;try{
            var request=new SaveDepartmentTemplate(department.Text,account.Text.Trim(),shares.CheckedItems.Cast<Choice>().Select(c=>c.P.Id).ToArray(),role.Text,includeIndividual.Checked,includePending.Checked);
            var impact=await Wire.Read<TemplateImpact>(await http.PostAsJsonAsync("api/admin/department-template-preview",request,Wire.Json));
            string List(string[] values)=>values.Length==0?"없음":string.Join("\n",values);
            var affected=$"부서: {request.Department}\n기본 계정: {request.ExpectedAccount}\n역할: {request.AccessRole}\n저장할 폴더: {string.Join(" / ",shares.CheckedItems.Cast<Choice>().Select(c=>c.P.Name))}\n\n부서 기본값을 따르는 PC {impact.Following.Length}대:\n{List(impact.Following)}\n\n개별 설정 PC {impact.Individual.Length}대 ({(request.IncludeIndividual?"기본값으로 변경":"보존")}):\n{List(impact.Individual)}\n\n승인 후 미배정 {impact.ApprovedUnassigned.Length}대 ({(request.IncludeApprovedUnassigned?"요청 부서 확인 후 배정":"대기 유지")}):\n{List(impact.ApprovedUnassigned)}\n\n이 변경을 서버에 저장할까요? 실제 PC 연결 완료는 메인에서 확인하세요.";
            using var review=new ImpactReview("부서 변경 영향 확인",affected);if(review.ShowDialog(this)!=DialogResult.OK)return;
            var saved=await Wire.Read<DepartmentTemplate>(await http.PostAsJsonAsync("api/admin/department-templates",request with{ExpectedRevision=impact.Revision},Wire.Json));templates=templates.Where(x=>x.Department!=saved.Department).Append(saved).ToArray();policyDirty=false;status.Text="부서 설정을 서버에 저장했습니다. PC 전달·폴더 연결 완료는 메인 화면에서 확인하세요.";Text="부서 기본 설정 · 서버별 계정"+(credentialDirty?" · 서버 계정 미저장":"");
        }catch(Exception e){status.Text="저장하지 못했습니다. "+UiTheme.Error(e);}finally{busy=false;save.Enabled=true;}};
        saveCredential.Click+=async(_,_)=>{if(busy)return;if(credentialServer.SelectedItem is not ServerChoice c){status.Text="계정을 저장할 서버를 선택하세요.";return;}if(!AssignmentRules.ValidAccount(credentialAccount.Text.Trim())){status.Text=@"실제 계정을 서버이름\계정 또는 계정@도메인 형태로 입력하세요.";return;}busy=true;saveCredential.Enabled=false;try{
            var request=new SaveShareCredential(department.Text,c.P.Id,credentialAccount.Text.Trim(),password.Text);var impact=await Wire.Read<CredentialImpact>(await http.PostAsJsonAsync("api/admin/share-credential-preview",request with{Password=""},Wire.Json));
            using var review=new ImpactReview("서버 계정 변경 영향 확인",$"부서: {request.Department}\n서버: {impact.Server}\n현재 저장 계정: {impact.Account}\n새 계정: {request.Account}\n암호: {(request.Password.Length==0?"빈 암호로 변경합니다. 기존 암호를 유지하는 동작이 아닙니다.":"입력한 새 암호로 변경")}\n\n함께 적용되는 공유:\n{string.Join("\n",impact.Profiles)}\n\n이 서버의 해당 부서 연결이 재설정될 수 있습니다. 저장할까요?");if(review.ShowDialog(this)!=DialogResult.OK)return;
            await Wire.Read<System.Text.Json.JsonElement>(await http.PostAsJsonAsync("api/admin/share-credential",request with{ExpectedRevision=impact.Revision},Wire.Json));stored=await http.GetFromJsonAsync<CredentialSummary[]>("api/admin/share-credentials",Wire.Json)??[];CredentialLoad();status.Text="서버 계정을 암호화 저장했습니다. 실제 적용 계정·연결 결과는 메인에서 확인하세요.";
        }catch(Exception e){status.Text="계정을 저장하지 못했습니다. "+UiTheme.Error(e);}finally{busy=false;saveCredential.Enabled=true;}};
        FormClosing+=(_,e)=>{if(busy){e.Cancel=true;return;}if(e.CloseReason==CloseReason.UserClosing&&!Discard("저장하지 않은 변경이 있습니다. 저장하지 않고 닫을까요?"))e.Cancel=true;};
    }
}
internal sealed class ImpactReview : Form
{
    public ImpactReview(string title,string message){Text=title;UiTheme.Form(this,new(700,640),new(560,460));var root=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new(16),ColumnCount=1,RowCount=2};root.RowStyles.Add(new(SizeType.Percent,100));root.RowStyles.Add(new(SizeType.AutoSize));Controls.Add(root);root.Controls.Add(new TextBox{Text=message.ReplaceLineEndings(Environment.NewLine),Dock=DockStyle.Fill,ReadOnly=true,Multiline=true,ScrollBars=ScrollBars.Vertical,BackColor=Color.White},0,0);var buttons=new FlowLayoutPanel{Dock=DockStyle.Fill,AutoSize=true};var save=new Button{Text="확인하고 적용",DialogResult=DialogResult.OK};var cancel=new Button{Text="취소",DialogResult=DialogResult.Cancel};UiTheme.Button(save,true);UiTheme.Button(cancel);buttons.Controls.Add(save);buttons.Controls.Add(cancel);root.Controls.Add(buttons,0,1);CancelButton=cancel;}
}
