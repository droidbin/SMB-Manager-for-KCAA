using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using SmbManager.Local;
using SmbManager.Local.Client;

internal static class InstallerResetQA
{
    public static void Run(string[] args)
    {
        if(!OperatingSystem.IsWindows() || Environment.MachineName!="DESKTOP-GMEG262" || !Environment.UserName.StartsWith("KCAASetup",StringComparison.Ordinal))throw new InvalidOperationException("Disposable native QA account required");
        var root=Path.GetFullPath(Wire.Arg(args,"--root"));var msi=Path.GetFullPath(Wire.Arg(args,"--msi"));var share=Wire.Arg(args,"--test-share");var fixture=Wire.Arg(args,"--legacy-fixture");var role=Wire.Arg(args,"--role","Client");
        if(!root.StartsWith(@"C:\TEST\KCAASetup-",StringComparison.OrdinalIgnoreCase)||!msi.StartsWith(root+"\\",StringComparison.OrdinalIgnoreCase)||!share.StartsWith(@"\\DESKTOP-GMEG262\KCAASetup",StringComparison.OrdinalIgnoreCase))throw new Exception("Isolated fixture paths required");
        var passed=new List<string>();string? failure=null;var drives=new List<string>();int? installerExit=null;
        void Check(bool ok,string text){if(!ok)throw new Exception(text);passed.Add(text);Console.WriteLine("PASS "+text);}
        try
        {
            var local=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);var roaming=Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            Check(local.Contains(Environment.UserName,StringComparison.OrdinalIgnoreCase),"Disposable user profile loaded");
            var locations=new LegacyLocations(local,roaming,Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),Environment.GetFolderPath(Environment.SpecialFolder.Startup));
            Check(LegacyMigration.Run(locations).Completed,"Prior NoLegacy completion journal established");
            var legacy=Path.Combine(local,"Programs","SMB Manager");Directory.CreateDirectory(legacy);
            foreach(var file in Directory.GetFiles(fixture))File.Copy(file,Path.Combine(legacy,Path.GetFileName(file)),false);
            var executable=Directory.GetFiles(legacy,"SMB Manager V1.7*.exe").Single();
            File.WriteAllLines(Path.Combine(legacy,"install-info.ini"),["Product=SMB Manager","InstallDir="+legacy]);
            var note=Path.Combine(legacy,"employee-note.txt");File.WriteAllText(note,"PRESERVE UNKNOWN USER FILE");
            var data=Path.Combine(local,"SMB Manager");Directory.CreateDirectory(data);var settings=Path.Combine(data,"settings.ini");var saved=Path.Combine(data,"saved-password.protected");File.WriteAllText(settings,"PRESERVE SETTINGS");File.WriteAllText(saved,"PRESERVE ENCRYPTED PASSWORD FIXTURE");
            var hashes=new[]{note,settings,saved}.ToDictionary(p=>p,p=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p))));
            foreach(var drive in new[]{"P:","Q:","R:","S:","T:"}.Where(d=>NativeMapping.Get(d).Code==2250&&!NativeMapping.DeviceAssigned(d)).Take(2))drives.Add(drive);
            Check(drives.Count==2,"Two unused test drive letters reserved");
            foreach(var d in drives)Check(NativeMapping.Connect(d,share,remember:true)==0,"Persistent SMB fixture mapped "+d);
            Check(NativeMapping.Connect(null,@"\\DESKTOP-GMEG262\IPC$")==0,"Deviceless IPC connection established");
            var denied=InstallCompletion.Run("S-1-0-0");Check(!denied.Completed&&drives.All(d=>NativeMapping.Get(d).Code==0)&&File.Exists(executable),"Wrong installer SID cannot remove files or connections");
            using(var handle=new FileStream(drives[0]+@"\qa-marker.txt",FileMode.Open,FileAccess.Read,FileShare.None)){
                var blocked=CurrentUserSmbReset.DisconnectAll();Check(!blocked.Completed,"Open SMB file prevents successful all-disconnect result");
                Check(handle.ReadByte()>=0&&NativeMapping.Get(drives[0]).Code==0,"Open file handle and its mapping are not forcibly closed");
            }
            foreach(var d in drives){if(NativeMapping.Get(d).Code!=0)Check(NativeMapping.Connect(d,share,remember:true)==0,"Reconnect isolated fixture before installer "+d);}
            Check(NativeMapping.Connect(null,@"\\DESKTOP-GMEG262\IPC$")==0,"IPC fixture restored before installer");
            var info=new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"msiexec.exe")){UseShellExecute=false,CreateNoWindow=true};foreach(var value in new[]{"/i",msi,"/qn","/norestart","/L*v",Path.Combine(root,"install-"+role+".log")})info.ArgumentList.Add(value);
            using(var process=Process.Start(info)!){if(!process.WaitForExit(180000)){process.Kill();throw new Exception("Installer timed out");}installerExit=process.ExitCode;}
            Check(installerExit is 0 or 3010,"MSI and automatic completion action succeeded: "+installerExit);
            var completion=Wire.Load<InstallCompletionResult>(Path.Combine(local,"SMB Manager Internal Beta 1","client","install-completion.json"));
            Check(completion.Completed&&completion.Sid==WindowsIdentity.GetCurrent().User!.Value,"Installer helper ran for the original Windows user");
            Check(completion.Legacy?.Removed.Length>0&&!File.Exists(executable)&&!File.Exists(Path.Combine(legacy,"Uninstall.exe")),"Installer removed legacy executable and uninstaller despite completed old journal");
            Check(hashes.All(p=>File.Exists(p.Key)&&Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p.Key)))==p.Value),"Settings, saved password fixture and unknown user file preserved");
            Check(completion.Connections is {Completed:true,Remaining.Length:0},"Installer reported all current-user SMB connections removed");
            Check(drives.All(d=>NativeMapping.Get(d).Code==2250&&!NativeMapping.DeviceAssigned(d)),"Both test drives actually disconnected");
            foreach(var d in drives){using var key=Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Network\"+d[..1]);Check(key is null,"Persistent mapping removed "+d);}
            Check(CurrentUserSmbReset.DisconnectAll().Completed,"Already disconnected state is idempotent");
            if(role=="Client"){
                int Repair(string tag){var repair=new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"msiexec.exe")){UseShellExecute=false,CreateNoWindow=true};foreach(var value in new[]{"/i",msi,"/qn","/norestart","REINSTALL=ALL","REINSTALLMODE=vomus","/L*v",Path.Combine(root,"repair-"+tag+".log")})repair.ArgumentList.Add(value);using var process=Process.Start(repair)!;if(!process.WaitForExit(180000))throw new Exception("Repair timed out");return process.ExitCode;}
                Check(NativeMapping.Connect(drives[0],share,remember:true)==0,"Open-file MSI failure fixture connected");
                using(var handle=new FileStream(drives[0]+@"\qa-marker.txt",FileMode.Open,FileAccess.Read,FileShare.None)){
                    Check(Repair("blocked")==1603,"MSI returns failure when open SMB file prevents completion");
                    var blocked=Wire.Load<InstallCompletionResult>(Path.Combine(local,"SMB Manager Internal Beta 1","client","install-completion.json"));
                    Check(!blocked.Completed&&handle.ReadByte()>=0&&NativeMapping.Get(drives[0]).Code==0,"Failed MSI completion preserves open file and records incomplete state");
                }
                Check(Repair("retry")==0,"Installer retry succeeds after closing file");
                Check(Wire.Load<InstallCompletionResult>(Path.Combine(local,"SMB Manager Internal Beta 1","client","install-completion.json")).Completed&&NativeMapping.Get(drives[0]).Code==2250,"Retry records completed cleanup and removes remaining mapping");
            }
        }
        catch(Exception e){failure=e.ToString();Environment.ExitCode=1;}
        finally
        {
            try{CurrentUserSmbReset.DisconnectAll();}catch{}
            Wire.Save(Path.Combine(root,"installer-reset-"+role+".json"),new{passed,failure,installerExit,sid=WindowsIdentity.GetCurrent().User?.Value,user=Environment.UserName,at=DateTimeOffset.UtcNow});
        }
    }
}