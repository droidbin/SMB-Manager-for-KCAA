using SmbManager.Local;
using SmbManager.Local.Client;
using System.Diagnostics;

namespace SmbManager.Local.ClientDesktop;

internal sealed class BetaDialog : Form
{
    private readonly ComboBox channels = new() { DropDownStyle=ComboBoxStyle.DropDownList,Width=190 };
    private readonly TextBox detail = new() { Multiline=true,ReadOnly=true,Dock=DockStyle.Fill,ScrollBars=ScrollBars.Vertical };
    private readonly Button check=new(){Text="업데이트 확인"},download=new(){Text="설치 파일 저장",Enabled=false};
    private readonly ClientAgent agent;
    private readonly CancellationTokenSource stop=new();
    private readonly string installed=FileVersionInfo.GetVersionInfo(typeof(BetaDialog).Assembly.Location).FileVersion??"확인 불가";
    private readonly string productVersion=typeof(BetaDialog).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute),false).Cast<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0]??"확인 불가";
    private BetaRelease? release;
    public BetaDialog(ClientAgent client)
    {
        agent=client;Text="SMB Manager — 업데이트";UiTheme.Form(this,new(720,540),new(620,440));
        var root=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new(18),ColumnCount=1,RowCount=3};
        root.RowStyles.Add(new(SizeType.AutoSize));root.RowStyles.Add(new(SizeType.AutoSize));root.RowStyles.Add(new(SizeType.Percent,100));Controls.Add(root);
        var hint=UiTheme.Hint($"현재 버전: {productVersion} · 설치 빌드 {installed}\n회사 서버에서 접근이 허용된 시험 채널을 확인합니다. 클로즈베타는 운영부·관리자 전용입니다.");
        root.Controls.Add(hint,0,0);UiTheme.Wrap(hint,root);
        var actions=new FlowLayoutPanel{Dock=DockStyle.Fill,AutoSize=true,WrapContents=true};
        actions.Controls.Add(channels);UiTheme.Button(check,true);UiTheme.Button(download);actions.Controls.Add(check);actions.Controls.Add(download);
        root.Controls.Add(actions,0,1);root.Controls.Add(detail,0,2);
        async Task Run(Func<Task> action){actions.Enabled=false;download.Enabled=false;try{await action();}catch(OperationCanceledException){}catch(Exception e){release=null;detail.Text="업데이트 확인 실패\r\n"+UiTheme.Error(e)+"\r\n회사 서버 연결 상태를 확인한 뒤 다시 시도해 주세요.";}finally{if(!IsDisposed){actions.Enabled=true;download.Enabled=release is not null;}}}
        check.Click+=async(_,_)=>await Run(RefreshAndCheck);
        channels.SelectedIndexChanged+=(_,_)=>{release=null;download.Enabled=false;detail.Text="채널을 선택했습니다. 업데이트 확인을 눌러주세요.";};
        download.Click+=async(_,_)=>await Run(async()=>{
            var selected=release??throw new InvalidOperationException("먼저 업데이트를 확인하세요.");
            using var save=new SaveFileDialog{FileName=selected.FileName,Filter="Windows 설치 파일 (*.msi)|*.msi",OverwritePrompt=true};
            if(save.ShowDialog(this)!=DialogResult.OK)return;
            if(File.Exists(save.FileName))throw new InvalidOperationException("기존 파일을 보존하려면 새 파일 이름을 선택하세요.");
            detail.Text="다운로드 및 무결성 검증 중…";
            await agent.DownloadBeta(selected,save.FileName,stop.Token);
            if(!IsDisposed)detail.Text="저장·SHA256 검증 완료\r\n"+save.FileName+"\r\n\r\n저장한 설치 파일을 실행해 업데이트하세요. 설치 중 앱 종료 안내가 나오면 알림 영역에서 SMB Manager를 완전히 종료하세요.";
        });
        Shown+=async(_,_)=>await Run(RefreshAndCheck);
        FormClosing+=(_,_)=>stop.Cancel();
    }
    private async Task RefreshAndCheck()
    {
        release=null;
        var preferred=(channels.SelectedItem as Choice)?.Id;
        var access=await agent.GetBetaAccess(stop.Token);if(IsDisposed)return;
        channels.DataSource=access.Allowed.Select(c=>new Choice(c)).ToArray();channels.DisplayMember=nameof(Choice.Name);
        channels.SelectedItem=channels.Items.Cast<Choice>().FirstOrDefault(c=>c.Id==(preferred??access.Selected))??channels.Items.Cast<Choice>().FirstOrDefault();
        if(channels.SelectedItem is not Choice choice){detail.Text="관리자가 PC 등록을 승인한 뒤 업데이트를 확인할 수 있습니다.";return;}
        await agent.SelectBetaChannel(choice.Id,stop.Token);
        var found=await agent.GetBetaRelease(choice.Id,stop.Token);if(IsDisposed)return;
        if(found is null){detail.Text=BetaChannels.Label(choice.Id)+"에 등록된 업데이트 설치 파일이 없습니다.\r\n서버관리자가 시험 배포 파일을 등록하면 이 화면에서 확인할 수 있습니다.\r\n\r\n현재 설치 빌드: "+installed;return;}
        var comparison=BetaUpdate.Compare(found,installed,productVersion);
        var status=comparison switch{1=>"새 업데이트가 있습니다.",0=>"현재 등록된 최신 빌드를 사용 중입니다.",-1=>"설치된 빌드가 서버 배포 파일보다 최신입니다.",_=>"배포 파일은 있으나 빌드 정보가 없어 업데이트 여부를 비교할 수 없습니다."};
        release=comparison is 0 or -1?null:found;
        detail.Text=$"{status}\r\n\r\n{BetaChannels.Label(choice.Id)} · {found.Version}\r\n배포 빌드: {found.FileVersion??"미제공"}\r\n{found.FileName}\r\n{found.Size/1048576d:F1} MB\r\n\r\n{found.Notes}";
    }
    private sealed record Choice(string Id){public string Name=>BetaChannels.Label(Id);}
}