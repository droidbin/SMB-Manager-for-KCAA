using SmbManager.Local;
namespace SmbManager.Local.ClientDesktop;
internal sealed class CompanyNotificationForm:Form {
 readonly TextBox content=new(){Dock=DockStyle.Fill,ReadOnly=true,Multiline=true,ScrollBars=ScrollBars.Vertical,BackColor=Color.FromArgb(17,24,39),ForeColor=Color.FromArgb(209,213,219),BorderStyle=BorderStyle.FixedSingle,Font=UiTheme.Font(12)};
 string lastId="";
 public event Action<string>? ReadConfirmed;
 public CompanyNotificationForm(){
  Text="SMB Manager — 회사 알림";UiTheme.Form(this,new(860,680),new(640,460));StartPosition=FormStartPosition.CenterScreen;TopMost=true;ShowInTaskbar=true;
  var root=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new(16),ColumnCount=1,RowCount=3};root.RowStyles.Add(new(SizeType.AutoSize));root.RowStyles.Add(new(SizeType.Percent,100));root.RowStyles.Add(new(SizeType.AutoSize));Controls.Add(root);
  root.Controls.Add(new Label{Text="회사에서 보낸 알림입니다.",AutoSize=true,Margin=new(0,0,0,10)},0,0);root.Controls.Add(content,0,1);
  var confirm=new Button{Text="확인했어요",AutoSize=true,Margin=new(0,12,0,0)};UiTheme.Button(confirm,true);confirm.Click+=(_,_)=>{if(lastId.Length>0)ReadConfirmed?.Invoke(lastId);Close();};root.Controls.Add(confirm,0,2);AcceptButton=confirm;
 }
 public void SetEntries(NotificationEntry[] entries){content.Text=string.Join("\r\n\r\n",entries.Select(e=>$"{e.ReceivedAt.ToLocalTime():MM/dd HH:mm}\r\n{e.Text}"));lastId=entries.LastOrDefault()?.Id??"";content.SelectionStart=0;content.ScrollToCaret();}
}
