using System.Runtime.InteropServices;
namespace SmbManager.Local.ClientDesktop;
internal static class NoticeContent {
 [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern IntPtr SendMessage(IntPtr handle,uint message,IntPtr wParam,IntPtr lParam);
 public static void Update(TextBox box,string text){
  if(box.Text==text)return;var selection=box.SelectionStart;var selected=box.SelectionLength;var first=box.IsHandleCreated?(int)SendMessage(box.Handle,0xCE,IntPtr.Zero,IntPtr.Zero):0;
  var last=box.GetLineFromCharIndex(box.TextLength);var follow=selected==0&&first+Math.Max(1,box.ClientSize.Height/Math.Max(1,box.Font.Height))>=last;
  box.Text=text;
  if(follow){box.Select(box.TextLength,0);box.ScrollToCaret();}
  else{box.Select(Math.Min(selection,box.TextLength),Math.Min(selected,Math.Max(0,box.TextLength-selection)));if(box.IsHandleCreated){var now=(int)SendMessage(box.Handle,0xCE,IntPtr.Zero,IntPtr.Zero);SendMessage(box.Handle,0xB6,IntPtr.Zero,(IntPtr)(first-now));}}
 }
}