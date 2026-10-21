using System.Diagnostics;
using System.Security.Principal;
using SmbManager.Local;
using SmbManager.Local.Client;
internal static class AdminInstallerIsolationQA {
 public static void Run(string[] args){
  if(Environment.MachineName!="DESKTOP-GMEG262"||!Environment.UserName.StartsWith("KCAASetup"))throw new Exception("Disposable native QA account required");
  var root=Path.GetFullPath(Wire.Arg(args,"--root"));var msi=Path.GetFullPath(Wire.Arg(args,"--msi"));var share=Wire.Arg(args,"--test-share");
  if(!root.StartsWith(@"C:\TEST\KCAASetup-")||!msi.StartsWith(root+"\\")||!share.StartsWith(@"\\DESKTOP-GMEG262\KCAASetup"))throw new Exception("Isolated test paths required");
  var passed=new List<string>();string? failure=null;var drives=new List<string>();Process? employee=null;int? installerExit=null;
  void Check(bool ok,string name){if(!ok)throw new Exception(name);passed.Add(name);Console.WriteLine("PASS "+name);}
  try{
   var local=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);Check(local.Contains(Environment.UserName),"Disposable user profile loaded");
   var state=Path.Combine(local,"SMB Manager Internal Beta 1","client");Directory.CreateDirectory(state);var marker=Path.Combine(state,"qa-settings.protected");File.WriteAllText(marker,"PRESERVE REGISTRATION FIXTURE");
   var client=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"SMB Manager Client Internal Beta 1","ClientDesktop","ClientDesktop.exe");Check(File.Exists(client),"Independent employee installation available");var version=FileVersionInfo.GetVersionInfo(client).FileVersion;
   foreach(var d in new[]{"P:","Q:","R:","S:","T:"}.Where(d=>NativeMapping.Get(d).Code==2250&&!NativeMapping.DeviceAssigned(d)).Take(2)){Check(NativeMapping.Connect(d,share,remember:true)==0,"Test mapping established "+d);drives.Add(d);}Check(drives.Count==2,"Two isolated mappings available");
   var bootstrap=Path.Combine(root,"bootstrap.json");Wire.Save(bootstrap,new ClientBootstrap("http://127.0.0.1:9/","","QA"));var start=new ProcessStartInfo(client){UseShellExecute=false,CreateNoWindow=true};foreach(var a in new[]{"--bootstrap",bootstrap,"--snapshot",Path.Combine(root,"employee.png"),"--snapshot-delay-ms","30000"})start.ArgumentList.Add(a);employee=Process.Start(start)!;_=employee.Handle;
   var deadline=DateTime.UtcNow.AddSeconds(12);var held=false;while(!employee.HasExited&&DateTime.UtcNow<deadline){try{using var f=new FileStream(Path.Combine(state,"client.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}catch(IOException){held=true;break;}Thread.Sleep(100);}Check(held,"Employee app actively running during administrator installation");
   using(var open=new FileStream(drives[0]+@"\qa-marker.txt",FileMode.Open,FileAccess.Read,FileShare.None)){
    var info=new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"msiexec.exe")){UseShellExecute=false,CreateNoWindow=true};foreach(var a in new[]{"/i",msi,"/qn","/norestart","/L*v",Path.Combine(root,"install-Full.log")})info.ArgumentList.Add(a);using var process=Process.Start(info)!;if(!process.WaitForExit(180000))throw new Exception("MSI timeout");installerExit=process.ExitCode;
    Check(installerExit is 0 or 3010,"Administrator MSI succeeds with employee app and open SMB file");Check(open.ReadByte()>=0&&drives.All(d=>NativeMapping.Get(d).Code==0),"Administrator installation preserves open SMB file and mappings");
   }
   Check(File.ReadAllText(marker)=="PRESERVE REGISTRATION FIXTURE","Employee settings preserved");Check(!File.Exists(Path.Combine(state,"install-completion.json")),"Administrator installation never invokes employee cleanup");
   var adminRoot=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"SMB Manager Internal Beta 1");Check(!File.Exists(Path.Combine(adminRoot,"ClientDesktop","ClientDesktop.exe")),"Upgrade removes bundled duplicate employee executable");
   Check(File.Exists(Path.Combine(adminRoot,"AdminDesktop","AdminDesktop.exe"))&&File.Exists(Path.Combine(adminRoot,"ManagementServer","ManagementServer.exe")),"Administrator and management server installed");Check(FileVersionInfo.GetVersionInfo(client).FileVersion==version,"Independent employee installation preserved");
   Check(employee.WaitForExit(45000)&&employee.ExitCode==0,"Employee app finishes its own preview normally");
  }catch(Exception e){failure=e.ToString();Environment.ExitCode=1;}
  finally{if(employee is not null){if(!employee.HasExited)employee.WaitForExit(45000);employee.Dispose();}foreach(var d in drives){try{CurrentUserSmbReset.DisconnectAll();}catch{}}Wire.Save(Path.Combine(root,"installer-reset-Full.json"),new{passed,failure,installerExit,at=DateTimeOffset.UtcNow});}
 }
}