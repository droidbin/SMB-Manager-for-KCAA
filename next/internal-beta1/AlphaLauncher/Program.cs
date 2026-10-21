using System.Diagnostics;
using SmbManager.Local;

ApplicationConfiguration.Initialize();
var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,".."));
var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"SMB Manager Internal Beta 1","server");
using var single = new Mutex(true, @"Local\SMBManagerInternalBeta1Admin", out var first);
if (!first) { MessageBox.Show("관리 프로그램이 이미 실행 중입니다. 알림 영역의 SMB Manager 서버관리자 아이콘에서 관리 화면 열기를 선택하세요.","SMB Manager"); return; }
Process? server = null;
try
{
    SecretFile.PrivateDirectory(data);
    var readyFile = Path.Combine(data,"ready.json");
    if (File.Exists(readyFile)) File.Delete(readyFile);
    Process Start(string project, params string[] args) {
        var info = new ProcessStartInfo(Path.Combine(root,project,project+".exe")) {UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=Path.Combine(root,project)};
        foreach(var arg in args)info.ArgumentList.Add(arg);
        return Process.Start(info) ?? throw new IOException("프로그램 시작 실패: "+project);
    }
    var config = Path.Combine(data,"https.json");
    var serverArgs = new List<string> {"--run-dir",data,"--port","48751"};
    if(File.Exists(config)) { serverArgs.Add("--https-config"); serverArgs.Add(config); }
    server=Start("ManagementServer",serverArgs.ToArray());
    var until=DateTime.UtcNow.AddSeconds(20);
    while(!File.Exists(readyFile)) { if(server.HasExited || DateTime.UtcNow>until)throw new IOException("관리 서버를 시작하지 못했습니다. 포트 48751 사용 여부와 인증서·설정을 확인하세요."); Thread.Sleep(100); }
    using var admin=Start("AdminDesktop","--ready",readyFile);
    admin.WaitForExit();
}
catch(Exception e) { MessageBox.Show(e.Message,"SMB Manager Internal Beta 1 시작 실패"); Environment.ExitCode=1; }
finally
{
    if(server is not null) { try { if(!server.HasExited){server.Kill();server.WaitForExit(5000);} } catch(InvalidOperationException){} server.Dispose(); }
    single.ReleaseMutex();
}
