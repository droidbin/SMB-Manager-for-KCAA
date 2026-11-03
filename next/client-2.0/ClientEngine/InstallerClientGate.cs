using System.ComponentModel;
using System.Security.Principal;
namespace SmbManager.Local.Client;
public static class InstallerClientGate {
 public static InstallCompletionResult Complete(string directory,string expectedSid){
  if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException();
  var sid=WindowsIdentity.GetCurrent().User?.Value??"";
  if(sid is "" or "S-1-5-18" or "S-1-5-19" or "S-1-5-20"||sid!=expectedSid)return Fail(sid,"설치를 시작한 Windows 사용자와 다릅니다. 다른 사용자의 앱·연결은 변경하지 않았습니다.");
  try{
   Directory.CreateDirectory(directory);
   using var installer=new FileStream(Path.Combine(directory,"install.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
   FileStream client;
   try{client=new FileStream(Path.Combine(directory,"client.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}
   catch(IOException e)when((e.HResult&65535)is 32 or 33){return Fail(sid,"직원 앱이 실행 중이어서 설치 마무리를 보류했습니다. 알림 영역의 SMB Manager에서 ‘앱 완전히 종료’를 선택한 뒤 설치를 다시 실행하세요. 기존 연결과 설정은 유지했습니다.");}
   using(client)return InstallCompletion.Run(expectedSid);
  }catch(Exception e)when(e is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException){return Fail(sid,"설치 마무리 잠금을 확보하지 못했습니다. 직원 앱과 다른 설치 작업을 종료하고 다시 실행하세요. Windows 오류: "+(e.HResult&65535));}
 }
 static InstallCompletionResult Fail(string sid,string message)=>new(false,sid,null,null,message);
}