using SmbManager.Local;
using SmbManager.Local.Client;

namespace SmbManager.Local.ClientDesktop;
internal sealed class BugReportDialog:Form
{
    public BugReportDialog(ClientAgent? agent=null)
    {
        Text="버그 신고";UiTheme.Form(this,new(700,680),new(580,560));
        var root=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new(16),ColumnCount=1,RowCount=7};Controls.Add(root);
        root.RowStyles.Add(new(SizeType.AutoSize));root.RowStyles.Add(new(SizeType.AutoSize));root.RowStyles.Add(new(SizeType.Percent,50));root.RowStyles.Add(new(SizeType.AutoSize));root.RowStyles.Add(new(SizeType.Percent,50));root.RowStyles.Add(new(SizeType.AutoSize));root.RowStyles.Add(new(SizeType.AutoSize));
        var hint=UiTheme.Hint("문제가 발생한 상황과 기대한 동작을 적어 주세요. 이름·연락처·계정·암호·업무 내용을 넣지 마세요. 화면·파일·로그는 자동 첨부하지 않습니다.");UiTheme.Wrap(hint,root);root.Controls.Add(hint,0,0);
        var category=new ComboBox{Dock=DockStyle.Fill,DropDownStyle=ComboBoxStyle.DropDownList,AccessibleName="오류 분류"};category.Items.AddRange(BugReportPrivacy.Categories);category.SelectedIndex=0;root.Controls.Add(category,0,1);
        var input=new TextBox{Dock=DockStyle.Fill,Multiline=true,MaxLength=2000,ScrollBars=ScrollBars.Vertical,AccessibleName="신고 내용"};root.Controls.Add(input,0,2);
        root.Controls.Add(UiTheme.Hint("전송할 내용 미리보기 · 흔한 주소·경로는 가립니다. 이름 등 남은 개인정보는 직접 지워 주세요."),0,3);
        var preview=new TextBox{Dock=DockStyle.Fill,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,AccessibleName="신고 전송 미리보기"};root.Controls.Add(preview,0,4);input.TextChanged+=(_,_)=>preview.Text=BugReportPrivacy.Redact(input.Text);
        var consent=new CheckBox{AutoSize=true,Text="내용과 앱 버전·Windows 빌드 전송에 동의합니다. 관리자에게 가명으로 표시되며 최대 30일 보관됩니다."};root.Controls.Add(consent,0,5);
        var buttons=new FlowLayoutPanel{Dock=DockStyle.Fill,AutoSize=true};root.Controls.Add(buttons,0,6);var send=ClientForm.ActionButton("신고 전송",true);var close=ClientForm.ActionButton("닫기");buttons.Controls.Add(send);buttons.Controls.Add(close);
        var stop=new CancellationTokenSource();FormClosed+=(_,_)=>stop.Cancel();close.Click+=(_,_)=>Close();send.Enabled=agent is not null;
        var id=Guid.NewGuid();input.TextChanged+=(_,_)=>id=Guid.NewGuid();category.SelectedIndexChanged+=(_,_)=>id=Guid.NewGuid();
        send.Click+=async(_,_)=>{
            if(agent is null)return;
            if(!consent.Checked||string.IsNullOrWhiteSpace(preview.Text)){MessageBox.Show(this,"전송할 내용과 동의를 확인하세요.",Text);return;}
            send.Enabled=false;input.ReadOnly=true;category.Enabled=false;
            try{await agent.SendBugReport(id,category.Text,preview.Text,stop.Token);if(!IsDisposed){MessageBox.Show(this,"신고가 접수됐습니다.",Text);Close();}}
            catch(OperationCanceledException)when(stop.IsCancellationRequested){}
            catch(Exception e){if(!IsDisposed)MessageBox.Show(this,e.Message+"\n내용은 이 창에 남아 있습니다. 다시 시도할 수 있습니다.",Text);}
            finally{if(!IsDisposed){send.Enabled=true;input.ReadOnly=false;category.Enabled=true;}}
        };
    }
}
