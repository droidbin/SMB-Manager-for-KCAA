using System.Runtime.InteropServices;
using System.Text;
using SmbManager.Local;
using SmbManager.Local.Client;

internal static class AccountUpdateQA
{
    [DllImport("advapi32.dll",EntryPoint="CredDeleteW",CharSet=CharSet.Unicode,SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]private static extern bool CredDelete(string target,uint type,uint flags);
    private static void WriteCredential(string target,string account,string password)
    {
        IntPtr t=IntPtr.Zero,u=IntPtr.Zero,b=IntPtr.Zero;var bytes=Encoding.Unicode.GetBytes(password);
        try{t=Marshal.StringToCoTaskMemUni(target);u=Marshal.StringToCoTaskMemUni(account);b=Marshal.AllocCoTaskMem(Math.Max(1,bytes.Length));Marshal.Copy(bytes,0,b,bytes.Length);
            var value=new WindowsCredential.Credential{Type=2,TargetName=t,UserName=u,CredentialBlob=b,CredentialBlobSize=(uint)bytes.Length,Persist=2};
            if(!WindowsCredential.CredWrite(ref value,0))throw new Exception("Test credential write failed: "+Marshal.GetLastWin32Error());
        }finally{Marshal.Copy(new byte[bytes.Length],0,b,bytes.Length);System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);if(t!=IntPtr.Zero)Marshal.FreeCoTaskMem(t);if(u!=IntPtr.Zero)Marshal.FreeCoTaskMem(u);if(b!=IntPtr.Zero)Marshal.FreeCoTaskMem(b);}
    }
    private static string SavedAccount(string target)
    {
        if(!WindowsCredential.CredRead(target,2,0,out var p))throw new Exception("Test credential read failed");
        try{return Marshal.PtrToStringUni(Marshal.PtrToStructure<WindowsCredential.Credential>(p).UserName)??"";}finally{WindowsCredential.CredFree(p);}
    }
    public static async Task Run(string[] args)
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException();
        if(Environment.MachineName!="DESKTOP-GMEG262")throw new InvalidOperationException("Dedicated test PC required.");
        var root=Path.GetFullPath(Wire.Arg(args,"--root"));var unc=Wire.Arg(args,"--test-share");var host=Environment.MachineName;
        if(!unc.StartsWith(@"\\"+host+@"\KCAAAccount",StringComparison.Ordinal))throw new InvalidOperationException("Dedicated isolated share required");
        var first=Environment.GetEnvironmentVariable("KCAA_QA_ACCOUNT1")??throw new Exception("Missing test account");
        var second=Environment.GetEnvironmentVariable("KCAA_QA_ACCOUNT2")??throw new Exception("Missing test account");
        var password1=Environment.GetEnvironmentVariable("KCAA_QA_PASSWORD1")??throw new Exception("Missing test password");
        var password2=Environment.GetEnvironmentVariable("KCAA_QA_PASSWORD2")??throw new Exception("Missing test password");
        if(WindowsCredential.CredRead(host,2,0,out var existing)){WindowsCredential.CredFree(existing);throw new Exception("Existing isolated target credential; preserve and abort");}
        var passed=new List<string>();string? failure=null;string drive="";RealSmbEngine? engine=null,external=null;
        void Check(bool ok,string text){if(!ok)throw new Exception(text);passed.Add(text);Console.WriteLine("PASS "+text);}
        var created=false;
        try
        {
            var update=new BetaRelease(BetaChannels.Closed,"2.0.0-beta.1","SMB-Manager-Client-test.msi",1,new string('A',64),FileVersion:"2.0.113.0");
            Check(BetaUpdate.Compare(update,"2.0.112.0","2.0.0-beta.1")==1,"Same beta label detects newer file build");
            Check(BetaUpdate.Compare(update,"2.0.113.0","2.0.0-beta.1")==0,"Current file build is up to date");
            Check(BetaUpdate.Compare(update,"2.0.114.0","2.0.0-beta.1")==-1,"Older catalog build does not offer downgrade");
            Check(BetaUpdate.Compare(update with{FileVersion=null},"2.0.112.0","2.0.0-beta.1") is null,"Legacy catalog without build remains unknown");
            Check(!BetaUpdate.ValidFileVersion("bad")&&!BetaUpdate.ValidFileVersion("2.0")&&!BetaUpdate.ValidFileVersion(""),"Malformed/incomplete file versions rejected");
            Check(WindowsCredential.Server(unc)==host,"Credential lookup scopes to exact host");
            drive=new[]{"P:","Q:","R:","S:","T:"}.First(d=>NativeMapping.Get(d).Code==2250&&!NativeMapping.DeviceAssigned(d));
            var profile=new Profile("switch-test","QA test","SimulateConnect",1,unc,drive,"QA");
            DepartmentPolicy Policy(int revision,string account)=>new("qa","qa",revision,"QA",account,[profile],true,false,false,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow.AddMinutes(2),revision,Accounts:[new(profile.Id,account)]);
            WriteCredential(host,first,password1);created=true;
            engine=new RealSmbEngine(Path.Combine(root,"engine"));engine.EnableManagedMode();engine.SetCredentials(new(){[profile.Id]=new(first,password1)});await engine.ApplyPolicy(Policy(1,first),CancellationToken.None);
            var connected=await engine.Connect("first",profile,CancellationToken.None);Check(connected.State=="Succeeded","Original department account connects: "+connected.Detail);
            Check(string.Equals(NativeMapping.Account(drive),first,StringComparison.OrdinalIgnoreCase),"Original actual Windows connection account verified");
            engine.SetCredentials(new(){[profile.Id]=new(second,password2)});await engine.ApplyPolicy(Policy(2,second),CancellationToken.None);
            connected=await engine.Connect("switch",profile,CancellationToken.None);Check(connected.State=="Succeeded","Approved department account switches: "+connected.Detail);
            Check(string.Equals(NativeMapping.Account(drive),second,StringComparison.OrdinalIgnoreCase),"New actual Windows connection account verified");
            Check(string.Equals(SavedAccount(host),second,StringComparison.OrdinalIgnoreCase),"Existing Windows credential updated to new approved account");
            Check(File.ReadAllText(drive+@"\qa-marker.txt")=="KCAA ACCOUNT TEST","Folder accessible with switched account");
            var state=File.ReadAllText(Path.Combine(root,"engine","smb-policy.json"));Check(!state.Contains(password1)&&!state.Contains(password2),"Policy state contains no Drive passwords");
            await engine.DisconnectAllOwned(CancellationToken.None);engine.Dispose();engine=null;
            // Failed credentials must never overwrite the saved account.
            WriteCredential(host,first,password1);
            engine=new RealSmbEngine(Path.Combine(root,"failure-engine"));engine.EnableManagedMode();engine.SetCredentials(new(){[profile.Id]=new(second,"deliberately-wrong-qa-password")});await engine.ApplyPolicy(Policy(1,second),CancellationToken.None);
            connected=await engine.Connect("wrong",profile,CancellationToken.None);Check(connected.State=="Failed","Invalid new password fails authentication");
            Check(SavedAccount(host)==first,"Failed authentication preserves saved credential");
            await engine.DisconnectAllOwned(CancellationToken.None);engine.Dispose();engine=null;
            // A different application's mapping is not owned and must not be replaced.
            Check(NativeMapping.Connect(drive,unc,new(first,password1),remember:true)==0,"External old-account test mapping created");
            external=new RealSmbEngine(Path.Combine(root,"external-engine"));external.EnableManagedMode();external.SetCredentials(new(){[profile.Id]=new(second,password2)});await external.ApplyPolicy(Policy(1,second),CancellationToken.None);
            connected=await external.Connect("external",profile,CancellationToken.None);
            Check(connected.State=="Failed"&&connected.Detail.StartsWith("ACCOUNT_MISMATCH"),"External wrong-account mapping is reported explicitly");
            Check(NativeMapping.Get(drive).Code==0&&string.Equals(NativeMapping.Account(drive),first,StringComparison.OrdinalIgnoreCase),"External connection remains intact");
            await external.RefreshConnections(CancellationToken.None);
            var wrongAccount=external.Connections().Single(c=>c.ProfileId==profile.Id);
            Check(wrongAccount.ConnectionState=="AccountMismatch","Native wrong-account mapping is not reported Connected");
            Check(!ConnectionRules.CanOpen(wrongAccount,Policy(1,second),DateTimeOffset.UtcNow),"Native wrong-account mapping cannot enable folder open");
            Check(SavedAccount(host)==first,"External conflict preserves Windows saved credential");
            using(var remembered=Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Network\"+drive[..1]))Check(remembered is not null,"Legacy persistent mapping exists before approved switch");
            var deniedSwitch=await external.ReconnectNow(CancellationToken.None,true);
            Check(deniedSwitch.Any(r=>r.State=="Failed")&&NativeMapping.Account(drive)==first,"Even confirmed transition refuses unrecognized external account");
            await external.ApplyPolicy(Policy(2,first),CancellationToken.None);
            await external.ApplyPolicy(Policy(3,second),CancellationToken.None);
            var switchResult=await external.ReconnectNow(CancellationToken.None,true);
            Check(switchResult.Length>0&&switchResult.All(r=>r.State=="Succeeded"),"Confirmed previous-assignment mapping transitions successfully");
            Check(NativeMapping.Account(drive)==second&&SavedAccount(host)==second,"Inherited mapping and Windows credential both switch to approved account");
            using(var remembered=Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Network\"+drive[..1]))Check(remembered is null,"Previous persistent mapping removed only for confirmed drive");
            await external.DisconnectAllOwned(CancellationToken.None);
            WriteCredential(host,first,password1);
            var detail=WindowsCredential.UpdateExisting(host,new(second,password2),["unrelated\\user"]);
            Check(SavedAccount(host)==first&&detail.Contains("보존"),"Unrelated saved account cannot be overwritten");
        }
        catch(Exception e){failure=e.ToString();Environment.ExitCode=1;}
        finally
        {
            if(engine is not null){await engine.DisconnectAllOwned(CancellationToken.None);engine.Dispose();}
            external?.Dispose();
            if(drive.Length>0&&NativeMapping.Same(NativeMapping.Get(drive).Remote,unc))NativeMapping.Disconnect(drive,false,forgetRemembered:true);
            if(created&&!CredDelete(host,2,0)){failure??="Isolated test credential cleanup failed";Environment.ExitCode=1;}
            Wire.Save(Path.Combine(root,"account-update-report.json"),new{passed,failure,drive,at=DateTimeOffset.UtcNow});
        }
    }
}