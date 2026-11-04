using SmbManager.Local;
namespace SmbManager.Local.ClientDesktop;
internal sealed class CompanyNotificationForm:Form {
 readonly TextBox content=new(){Dock=DockStyle.Fill,ReadOnly=true,Multiline=true,ScrollBars=ScrollBars.Vertical,BackColor=Color.FromArgb(17,24,39),ForeColor=Color.FromArgb(226,232,240),BorderStyle=BorderStyle.FixedSingle,Font=UiTheme.Font(12),AccessibleName="회사에서 보낸 알림"};
 readonly Button confirm=ClientForm.ActionButton("읽음으로 표시",true),pendingButton=ClientForm.ActionButton("새 알림 보기"),later=ClientForm.ActionButton("나중에 읽기");
 NotificationEntry[] displayed=[],pending=[];
 public event Action<string>? ReadConfirmed;
 protected override bool ShowWithoutActivation=>true;
 public CompanyNotificationForm(){
  Text="SMB Manager · 회사 알림";UiTheme.Form(this,new(800,600),new(620,440));StartPosition=FormStartPosition.CenterScreen;TopMost=false;ShowInTaskbar=true;
  var root=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new(18),ColumnCount=1,RowCount=4};root.RowStyles.Add(new(SizeType.AutoSize));root.RowStyles.Add(new(SizeType.AutoSize));root.RowStyles.Add(new(SizeType.Percent,100));root.RowStyles.Add(new(SizeType.AutoSize));Controls.Add(root);
  root.Controls.Add(new Label{Text="회사 알림을 확인해 주세요",AutoSize=true,Font=UiTheme.Font(14,true),Margin=new(0,0,0,10)},0,0);pendingButton.Visible=false;root.Controls.Add(pendingButton,0,1);root.Controls.Add(content,0,2);
  var actions=new FlowLayoutPanel{Dock=DockStyle.Fill,AutoSize=true,WrapContents=true,Margin=new(0,10,0,0)};actions.Controls.Add(confirm);actions.Controls.Add(later);root.Controls.Add(actions,0,3);
  later.Click+=(_,_)=>Close();later.DialogResult=DialogResult.Cancel;CancelButton=later;
  // No default Enter action: typing in another application must never acknowledge a notice.
  confirm.Click+=(_,_)=>{var id=displayed.LastOrDefault()?.Id??"";var seen=displayed.Select(e=>e.Id).ToHashSet();var remaining=pending.Where(e=>!seen.Contains(e.Id)).ToArray();if(id.Length>0)ReadConfirmed?.Invoke(id);if(remaining.Length>0)LoadEntries(remaining);else Close();};
  pendingButton.Click+=(_,_)=>LoadEntries(pending);
 }
 public void SetEntries(NotificationEntry[] entries,bool replaceExisting=false){
  if(Visible&&displayed.Length>0&&!replaceExisting){var ids=displayed.Select(e=>e.Id).ToHashSet();var count=entries.Count(e=>!ids.Contains(e.Id));if(count>0){pending=entries;pendingButton.Text=$"새 알림 {count}개가 도착했습니다 · 보기";pendingButton.Visible=true;}return;}
  LoadEntries(entries);
 }
 void LoadEntries(NotificationEntry[] entries){displayed=entries.ToArray();pending=[];pendingButton.Visible=false;content.Text=string.Join("\r\n\r\n────────────────────\r\n\r\n",entries.Select(e=>$"{e.ReceivedAt.ToLocalTime():MM/dd HH:mm}\r\n{e.Text}"));confirm.Text=entries.Length>1?$"표시된 알림 {entries.Length}개 모두 읽음으로 표시":"이 알림 읽음으로 표시";confirm.Enabled=entries.Length>0;content.Select(0,0);content.ScrollToCaret();}
}