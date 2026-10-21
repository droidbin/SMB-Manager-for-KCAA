using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json.Nodes;
using SmbManager.Local;
using SmbManager.Local.Client;

internal static class LegacyMigrationTests
{
    public static async Task Run(string run, Action<bool,string> check)
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException();
        LegacyLocations Locations(string name) {
            var root=Path.Combine(run,"legacy-migration",name);
            return new(Path.Combine(root,"Local"),Path.Combine(root,"Roaming"),Path.Combine(root,"Desktop"),Path.Combine(root,"StartMenu"),Path.Combine(root,"Startup"));
        }
        string Install(LegacyLocations l,string? custom=null){
            var root=custom??Path.Combine(l.Local,"Programs","SMB Manager");Directory.CreateDirectory(root);
            foreach(var item in new[]{("Migration.App","SMB Manager V1.7.11.exe"),("Migration.Uninstall","Uninstall.exe")}){
                using var input=typeof(LegacyMigrationTests).Assembly.GetManifestResourceStream(item.Item1)!;
                using var output=File.Create(Path.Combine(root,item.Item2));input.CopyTo(output);
            }
            Wire.AtomicWrite(Path.Combine(root,"install-info.ini"),System.Text.Encoding.UTF8.GetBytes("Product=SMB Manager\r\nInstallDir="+root+"\r\n"));
            return root;
        }
        string Marker(LegacyLocations l)=>Path.Combine(l.Local,"SMB Manager Internal Beta 1","migration","legacy17-removal.json");
        var l=Locations("normal");var installed=Install(l);
        Directory.CreateDirectory(l.Roaming);Directory.CreateDirectory(Path.Combine(l.Local,"SMB Manager"));
        var userSetting=Path.Combine(l.Roaming,"settings.protected");File.WriteAllBytes(userSetting,[1,3,5,7]);
        var oldCredential=Path.Combine(l.Local,"SMB Manager","password.protected");File.WriteAllBytes(oldCredential,[2,4,6,8]);
        var personal=Path.Combine(installed,"직원 개인 문서.txt");File.WriteAllText(personal,"preserve");
        var secondUser=Locations("second-user");var secondRoot=Install(secondUser);
        var result=LegacyMigration.Run(l);
        check(result.Completed&&result.Outcome=="Removed"&&!File.Exists(Path.Combine(installed,"SMB Manager V1.7.11.exe"))&&!File.Exists(Path.Combine(installed,"Uninstall.exe")),"구버전 실행 파일·제거 프로그램 자동 정리");
        check(File.ReadAllBytes(userSetting).SequenceEqual(new byte[]{1,3,5,7})&&File.ReadAllBytes(oldCredential).SequenceEqual(new byte[]{2,4,6,8}),"기존 설정·저장 비밀번호 파일 보존");
        check(File.ReadAllText(personal)=="preserve","설치 폴더의 사용자 문서는 보존");
        check(Wire.Load<JsonObject>(Marker(l))["completed"]!.GetValue<bool>(),"최초 제거 성공 기록 저장");
        Install(l);
        var again=LegacyMigration.Run(l);
        check(again.Outcome=="AlreadyCompleted"&&File.Exists(Path.Combine(installed,"SMB Manager V1.7.11.exe")),"동일 PC의 완료된 최초 정리 재실행 방지");

        var modified=Locations("modified");var modifiedRoot=Install(modified);
        var app=Path.Combine(modifiedRoot,"SMB Manager V1.7.11.exe");var bytes=File.ReadAllBytes(app);bytes[^1]^=1;File.WriteAllBytes(app,bytes);
        var rejected=LegacyMigration.Run(modified);
        check(!rejected.Completed&&File.ReadAllBytes(app).SequenceEqual(bytes)&&File.Exists(Path.Combine(modifiedRoot,"Uninstall.exe")),"알려진 이름이어도 해시가 다르면 자동 제거 거절");

        var locked=Locations("locked");var lockedRoot=Install(locked);
        using(var held=new FileStream(Path.Combine(lockedRoot,"SMB Manager V1.7.11.exe"),FileMode.Open,FileAccess.Read,FileShare.Read)){
            check(!LegacyMigration.Run(locked).Completed,"잠긴 실행 파일은 성공 처리하지 않고 재시도 대기");
        }
        check(LegacyMigration.Run(locked).Completed&&!File.Exists(Path.Combine(lockedRoot,"SMB Manager V1.7.11.exe")),"잠금 해제 후 다음 시도로 제거 완료");

        var partial=Locations("partial");var partialRoot=Install(partial);File.Delete(Path.Combine(partialRoot,"SMB Manager V1.7.11.exe"));
        check(LegacyMigration.Run(partial).Completed&&!File.Exists(Path.Combine(partialRoot,"Uninstall.exe")),"이전 정리가 중단된 남은 제거 프로그램·설치 정보 정리");

        var custom=Locations("custom");var customRoot=Install(custom,Path.Combine(run,"legacy-migration","custom","Custom Path","SMB Manager"));
        Directory.CreateDirectory(custom.Desktop);var customLink=Path.Combine(custom.Desktop,"SMB Manager.lnk");
        CreateShortcut(customLink,Path.Combine(customRoot,"SMB Manager V1.7.11.exe"));
        var customResult=LegacyMigration.Run(custom);
        check(customResult.Completed&&!File.Exists(Path.Combine(customRoot,"SMB Manager V1.7.11.exe"))&&!File.Exists(customLink),"설치 정보와 바로가기로 확인한 사용자 지정 설치도 정리");

        var foreign=Locations("foreign");Directory.CreateDirectory(foreign.Desktop);
        var unrelated=Path.Combine(run,"legacy-migration","foreign","ClientDesktop.exe");File.WriteAllText(unrelated,"different product");
        var unrelatedLink=Path.Combine(foreign.Desktop,"SMB Manager.lnk");CreateShortcut(unrelatedLink,unrelated);
        check(LegacyMigration.Run(foreign).Completed&&File.Exists(unrelatedLink)&&File.ReadAllText(unrelated)=="different product","동일 이름 바로가기가 새 앱·다른 프로그램을 가리키면 보존");

        var updater=Locations("updater");var updaterRoot=Install(updater,Path.Combine(updater.Local,"SMB Manager","Installed","1.7.11"));
        var pointer=Path.Combine(updater.Local,"SMB Manager","Installed","current.txt");File.WriteAllText(pointer,Path.Combine(updaterRoot,"SMB Manager V1.7.11.exe"));
        check(LegacyMigration.Run(updater).Completed&&!File.Exists(pointer)&&!Directory.Exists(updaterRoot),"구버전 업데이트 설치 폴더와 해당 포인터만 정리");

        var corrupt=Locations("corrupt");var corruptRoot=Install(corrupt);Directory.CreateDirectory(Path.GetDirectoryName(Marker(corrupt))!);File.WriteAllText(Marker(corrupt),"broken journal");
        check(!LegacyMigration.Run(corrupt).Completed&&File.Exists(Path.Combine(corruptRoot,"SMB Manager V1.7.11.exe"))&&File.ReadAllText(Marker(corrupt))=="broken journal","손상된 전환 기록은 원본을 유지하고 제거를 보류");

        var ghost=Wire.Load<JsonObject>(Marker(l));ghost["hardware"]!["macAddresses"]=new JsonArray("001122334455");Wire.Save(Marker(l),ghost);
        check(LegacyMigration.Run(l).Outcome=="Removed"&&!File.Exists(Path.Combine(installed,"SMB Manager V1.7.11.exe")),"고스트로 복사한 다른 장비의 완료 기록은 재사용하지 않음");

        var busy=Locations("busy");var busyRoot=Install(busy);using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
        using var identity=WindowsIdentity.GetCurrent();var sid=identity.User!.Value;
        var owner=Task.Run(()=>{using var mutex=new Mutex(false,@"Local\SMBManager.Legacy17."+sid);mutex.WaitOne();try{entered.Set();release.Wait();}finally{mutex.ReleaseMutex();}});
        entered.Wait();
        try{var pending=LegacyMigration.Run(busy);check(pending.Outcome=="Busy"&&!pending.Completed&&File.Exists(Path.Combine(busyRoot,"SMB Manager V1.7.11.exe")),"동시 구버전 정리는 중복 실행하지 않음");}finally{release.Set();await owner;}

        check(Directory.Exists(secondRoot)&&File.Exists(Path.Combine(secondRoot,"SMB Manager V1.7.11.exe")),"다른 사용자 설치 경로를 전역 검색·삭제하지 않음");
    }
    private static void CreateShortcut(string file,string target)
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException();
        object? shell=null;object? link=null;
        try{shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);link=shell!.GetType().InvokeMember("CreateShortcut",System.Reflection.BindingFlags.InvokeMethod,null,shell,[file]);link!.GetType().InvokeMember("TargetPath",System.Reflection.BindingFlags.SetProperty,null,link,[target]);link.GetType().InvokeMember("Save",System.Reflection.BindingFlags.InvokeMethod,null,link,null);}
        finally{if(link is not null)Marshal.FinalReleaseComObject(link);if(shell is not null)Marshal.FinalReleaseComObject(shell);}
    }
}

