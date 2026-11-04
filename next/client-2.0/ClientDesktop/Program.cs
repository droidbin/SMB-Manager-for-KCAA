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
            if(args.Contains("--complete-install"))
            {
                SecretFile.PrivateDirectory(stateDir);
                var result=InstallerClientGate.Complete(stateDir,Wire.Arg(args,"--install-user-sid",""));
                Wire.Save(Path.Combine(stateDir,"install-completion.json"),result);
                Environment.ExitCode=result.Completed?0:2;
                if(!result.Completed && int.TryParse(Wire.Arg(args,"--install-ui-level","2"),out var level) && level>=3)MessageBox.Show(result.Message,"SMB Manager 설치 마무리",MessageBoxButtons.OK,MessageBoxIcon.Warning);
                return;
            }
            using var instance = new ClientInstance(stateDir);
            if (!instance.IsOwner) { if (!args.Contains("--headless")) instance.RequestActivation(); return; }
            using var instanceLock = new FileStream(Path.Combine(stateDir,"client.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
            if (args.Contains("--headless"))
            {
                using var agent = ClientAgent.Create(Wire.Load<ClientBootstrap>(bootstrapFile), stateDir, securityStateDir: policyDir).GetAwaiter().GetResult();
                agent.Run(CancellationToken.None).GetAwaiter().GetResult();
            }
            else
            {
                ApplicationConfiguration.Initialize();
                if(args.Contains("--bug-report-preview")){UiDiagnostics.Run(new BugReportDialog(),args);return;}
                if(args.Contains("--notification-popup-preview") && Wire.Arg(args,"--snapshot","").Length>0){var popup=new CompanyNotificationForm();popup.SetEntries([new("preview","회사 알림 시험\r\n한글 안내와 긴 메시지는 스크롤해서 확인할 수 있습니다.\r\n"+string.Join("\r\n",Enumerable.Repeat("업무 공지 내용을 확인해 주세요.",20)),DateTimeOffset.Now)]);UiDiagnostics.Run(popup,args);return;}
                if(args.Contains("--beta-preview") && Wire.Arg(args,"--snapshot","").Length>0){using var previewAgent=ClientAgent.Create(Wire.Load<ClientBootstrap>(bootstrapFile),stateDir).GetAwaiter().GetResult();UiDiagnostics.Run(new BetaDialog(previewAgent),args);return;}
                var form=new ClientForm(bootstrapFile,stateDir,policyDir,Wire.Arg(args,"--snapshot","").Length==0,Wire.Arg(args,"--snapshot","").Length>0?Wire.Arg(args,"--ux-preview",""):"");
                instance.Listen(form);
                UiDiagnostics.Run(form,args);
                if(form.PendingUpdate is { } pending){instanceLock.Dispose();pending.Start();}
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

internal sealed class RegistrationDialog : Form
{
    private readonly TextBox name=new(){Dock=DockStyle.Fill,MaxLength=80};
    private readonly ComboBox department=new(){Dock=DockStyle.Fill,DropDownStyle=ComboBoxStyle.DropDownList};
    public CorrectionRequest Request=>new(name.Text.Trim(),department.Text);
    public RegistrationDialog(ClientRegistration info,string[] values){
        Text="내 정보 변경 요청";UiTheme.Form(this,new(600,460),new(500,400));
        var root=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new(16),ColumnCount=1,AutoScroll=true};Controls.Add(root);
        var label=UiTheme.Hint($"등록 성명: {info.Name}\n요청 부서: {info.RequestedDepartment}\n배정 부서: {(info.Department.Length>0?info.Department:"준비 중")}\n요청 시각: {(info.RequestedAt==default?"이전 버전 등록":info.RequestedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"))}\n\n잘못 입력했다면 아래에서 정정을 요청하세요. 실제 권한은 관리자가 별도로 확인합니다.");root.Controls.Add(label);UiTheme.Wrap(label,root);
        root.Controls.Add(new Label{Text="성명",AutoSize=true});name.Text=info.Correction?.Name??info.Name;root.Controls.Add(name);
        root.Controls.Add(new Label{Text="요청 부서",AutoSize=true});department.Items.AddRange(values.Cast<object>().ToArray());department.SelectedItem=info.Correction?.Department??info.RequestedDepartment;root.Controls.Add(department);
        var submit=new Button{Text="변경 요청 보내기",AutoSize=true};UiTheme.Button(submit,true);root.Controls.Add(submit);
        submit.Click+=(_,_)=>{if(string.IsNullOrWhiteSpace(name.Text)||department.SelectedItem is null){MessageBox.Show(this,"성명과 요청 부서를 입력하세요.");return;}DialogResult=DialogResult.OK;Close();};
    }
}

