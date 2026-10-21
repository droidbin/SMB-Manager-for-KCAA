using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using SmbManager.Local;

namespace SmbManager.Local.Client;

public sealed record LegacyMigrationResult(bool Completed, string Outcome, string Message, string[] Removed, string[] Retained, string[] Problems);
internal sealed record LegacyLocations(string Local, string Roaming, string Desktop, string StartMenu, string Startup);

/// <summary>Current-user, hash-identified 1.7 removal. No shells, recursive deletion,
/// credential APIs, SMB operations, services, forced process termination or elevation.</summary>
public static class LegacyMigration
{
    private sealed record CatalogEntry(string Path, string Sha256, bool App);
    private sealed record Journal(int Schema, bool Completed, string Sid, HardwareIdentity Hardware, LegacyMigrationResult Result);
    private static readonly CatalogEntry[] Catalog = LoadCatalog();
    private static CatalogEntry[] LoadCatalog()
    {
        using var stream = typeof(LegacyMigration).Assembly.GetManifestResourceStream("SmbManager.LegacyCatalog")!;
        return JsonSerializer.Deserialize<CatalogEntry[]>(stream, Wire.Json) ?? throw new InvalidDataException("구버전 참조 목록 확인 실패");
    }
    public static LegacyMigrationResult RunForCurrentUser(bool rescan = false)
    {
        var executableDirectory = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var allowed = new[] { "SMB Manager Client Internal Beta 1", "SMB Manager Internal Beta 1" }
            .Select(p => Path.Combine(programFiles, p, "ClientDesktop"));
        if (!allowed.Any(p => string.Equals(p, executableDirectory, StringComparison.OrdinalIgnoreCase)))
            return new(true, "NotInstalled", "개발·미설치 실행: 구버전 정리를 실행하지 않음", [], [], []);
        return Run(new(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.Startup)),rescan);
    }
    internal static LegacyMigrationResult Run(LegacyLocations locations, bool rescan = false)
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException();
        var removed = new List<string>(); var retained = new List<string>(); var problems = new List<string>();
        try
        {
            var sid = WindowsIdentity.GetCurrent().User!.Value;
            using var mutex = new Mutex(false, @"Local\SMBManager.Legacy17." + sid);
            var acquired = false;
            try
            {
                try { acquired = mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) return new(false, "Busy", "다른 실행에서 구버전을 정리 중입니다. 잠시 후 다시 시도하세요.", [], [], []);
                var journalDirectory = Path.Combine(locations.Local, "SMB Manager Internal Beta 1", "migration");
                Plain(journalDirectory);
                SecretFile.PrivateDirectory(journalDirectory);
                var journalFile = Path.Combine(journalDirectory, "legacy17-removal.json");
                Plain(journalFile);
                var hardware = HardwareIdentity.Capture(); Journal? previous = null;
                if (File.Exists(journalFile))
                {
                    previous = Wire.Load<Journal>(journalFile);
                    if (previous.Schema != 1) throw new InvalidDataException("구버전 정리 기록 형식 확인 필요");
                    if (!rescan && previous.Completed && previous.Sid == sid && HardwareIdentity.Same(previous.Hardware, hardware))
                        return previous.Result with { Outcome = "AlreadyCompleted", Message = "구버전 최초 정리 완료" };
                }

                var defaultRoot = Path.Combine(locations.Local, "Programs", "SMB Manager");
                var updateRoot = Path.Combine(locations.Local, "SMB Manager", "Installed");
                var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Full(defaultRoot), Full(updateRoot) };
                var priorRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if(previous is {Completed:false} && previous.Sid==sid && HardwareIdentity.Same(previous.Hardware,hardware)){
                    removed.AddRange(previous.Result.Removed);
                    foreach(var file in previous.Result.Removed.Where(p=>Catalog.Any(e=>e.App&&e.Path==Path.GetFileName(p)))){
                        var prior=Path.GetDirectoryName(Full(file))!;roots.Add(prior);priorRoots.Add(prior);
                    }
                }
                Plain(updateRoot);
                if (Directory.Exists(updateRoot))
                {
                    var children = Directory.GetDirectories(updateRoot);
                    if (children.Length > 100) throw new IOException("구버전 업데이트 폴더가 너무 많아 확인이 필요합니다.");
                    foreach (var child in children) roots.Add(Full(child));
                }
                var links = new[]
                {
                    Path.Combine(locations.Desktop, "SMB Manager.lnk"),
                    Path.Combine(locations.Startup, "SMB Manager.lnk"),
                    Path.Combine(locations.StartMenu, "Programs", "SMB Manager", "SMB Manager.lnk"),
                    Path.Combine(locations.StartMenu, "Programs", "SMB Manager", "SMB Manager Uninstall.lnk")
                };
                var linkTargets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var link in links)
                {
                    try { Plain(link); }
                    catch(Exception e) when(e is IOException or UnauthorizedAccessException) { retained.Add(link); continue; }
                    if (!File.Exists(link)) continue;
                    string target;
                    try { target=ReadShortcut(link); }
                    catch(Exception e) when(e is IOException or UnauthorizedAccessException or COMException) { retained.Add(link); continue; }
                    if (target.Length == 0 || !Path.IsPathFullyQualified(target) || target.StartsWith(@"\\", StringComparison.Ordinal)) continue;
                    target = Full(target);
                    if (!Catalog.Any(e => e.App && string.Equals(System.IO.Path.GetFileName(target), e.Path, StringComparison.OrdinalIgnoreCase))
                        && !string.Equals(System.IO.Path.GetFileName(target), "Uninstall.exe", StringComparison.OrdinalIgnoreCase)) continue;
                    var root = System.IO.Path.GetDirectoryName(target)!;
                    if (!roots.Contains(root) && !ValidMarker(root)) continue;
                    roots.Add(root); linkTargets[link] = target;
                }
                var completedRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var root in roots)
                {
                    try
                    {
                        Plain(root);
                        if (!Directory.Exists(root)) { if(priorRoots.Contains(root))completedRoots.Add(root);continue; }
                        var apps = Directory.GetFiles(root, "SMB Manager V1.7*.exe");
                        if (apps.Length == 0 && !(File.Exists(Path.Combine(root, "Uninstall.exe")) && Matches(Path.Combine(root, "Uninstall.exe"), "Uninstall.exe")) && !priorRoots.Contains(root)) continue;
                        if (apps.Any(p => !Matches(p, System.IO.Path.GetFileName(p))))
                            throw new IOException("확인되지 않은 구버전 파일이 있어 자동 제거를 보류했습니다.");
                        var controlled = Same(root, defaultRoot) || Same(root, updateRoot) ||
                            Same(System.IO.Path.GetDirectoryName(root)!, updateRoot);
                        if (!controlled && !priorRoots.Contains(root) && !ValidMarker(root)) throw new IOException("설치 경로 확인 필요");
                        StopKnownApplications(apps);
                        foreach (var relative in Catalog.Select(e => e.Path).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p == "Uninstall.exe" ? 2 : p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? 0 : 1))
                        {
                            var file = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
                            Plain(file);
                            if (!File.Exists(file)) continue;
                            if (!Matches(file, relative)) { retained.Add(file); continue; }
                            File.Delete(file); removed.Add(file);
                        }
                        if (Directory.GetFiles(root, "SMB Manager V1.7*.exe").Length != 0) throw new IOException("구버전 실행 파일 제거 확인 필요");
                        var marker = Path.Combine(root, "install-info.ini");
                        if (ValidMarker(root)) { File.Delete(marker); removed.Add(marker); }
                        completedRoots.Add(root);
                        EmptyDirectory(Path.Combine(root, "Fonts"));
                        EmptyDirectory(root);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                    { problems.Add(root + ": " + e.Message); }
                }
                foreach (var link in linkTargets)
                {
                    var root = System.IO.Path.GetDirectoryName(link.Value)!;
                    if (!completedRoots.Contains(root)) continue;
                    Plain(link.Key);
                    if (File.Exists(link.Key) && Same(ReadShortcut(link.Key), link.Value))
                    { File.Delete(link.Key); removed.Add(link.Key); }
                }
                var pointer = Path.Combine(updateRoot, "current.txt");
                Plain(pointer);
                if (File.Exists(pointer) && new FileInfo(pointer).Length < 16384)
                {
                    var target = File.ReadAllText(pointer).Trim();
                    if (Path.IsPathFullyQualified(target) && !target.StartsWith(@"\\", StringComparison.Ordinal))
                    {
                        var parent = System.IO.Path.GetDirectoryName(Full(target))!;
                        if (completedRoots.Contains(parent) && (Same(parent, updateRoot) || Same(System.IO.Path.GetDirectoryName(parent)!, updateRoot))
                            && Catalog.Any(e => e.App && e.Path == System.IO.Path.GetFileName(target)) && !File.Exists(target))
                        { File.Delete(pointer); removed.Add(pointer); }
                    }
                }
                var menuDirectory=Path.Combine(locations.StartMenu,"Programs","SMB Manager");
                try { EmptyDirectory(menuDirectory); }
                catch(Exception e) when(e is IOException or UnauthorizedAccessException) { retained.Add(menuDirectory); }
                EmptyDirectory(updateRoot);
                var completed = problems.Count == 0;
                var result = new LegacyMigrationResult(completed, completed ? removed.Count > 0 ? "Removed" : "NoLegacy" : "Pending",
                    completed ? removed.Count > 0 ? "구버전 제거 완료 · 설정과 저장 비밀번호, 기존 폴더 연결은 보존했습니다." : "제거할 구버전이 없습니다."
                        : "구버전을 정리하지 못했습니다. 구버전을 종료한 뒤 다시 시도하거나 서버관리자에게 문의하세요.",
                    removed.ToArray(), retained.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), problems.ToArray());
                if(completed && retained.Any(p=>p.EndsWith(".lnk",StringComparison.OrdinalIgnoreCase)||Same(p,menuDirectory)))
                    result=result with{Message=result.Message+" 일부 바로가기 경로는 자동 정리할 수 없어 보존했습니다. 남은 바로가기는 직접 삭제하세요."};
                Wire.Save(journalFile, new Journal(1, completed, sid, hardware, result));
                return result;
            }
            finally { if (acquired) mutex.ReleaseMutex(); }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or JsonException or System.ComponentModel.Win32Exception or COMException)
        {
            problems.Add(e.Message);
            return new(false, "Pending", "구버전 정리 확인이 필요합니다. 다시 시도하거나 서버관리자에게 문의하세요.", removed.ToArray(), retained.ToArray(), problems.ToArray());
        }
    }
    private static string Full(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
    private static bool Same(string left, string right) => string.Equals(Full(left), Full(right), StringComparison.OrdinalIgnoreCase);
    private static void Plain(string path)
    {
        var current = Path.GetFullPath(path);
        if (!Path.IsPathFullyQualified(current) || current.StartsWith(@"\\", StringComparison.Ordinal)) throw new IOException("로컬 설치 경로만 정리할 수 있습니다.");
        while (!string.IsNullOrEmpty(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("링크 경로는 자동 정리하지 않습니다.");
            current = Path.GetDirectoryName(current)!;
        }
    }
    private static bool Matches(string file, string relative)
    {
        Plain(file);
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = Convert.ToHexString(SHA256.HashData(stream));
        return Catalog.Any(e => string.Equals(e.Path, relative, StringComparison.OrdinalIgnoreCase) && e.Sha256 == hash);
    }
    private static bool ValidMarker(string root)
    {
        var marker = Path.Combine(root, "install-info.ini");
        Plain(marker);
        if (!File.Exists(marker) || new FileInfo(marker).Length > 16384) return false;
        var lines = File.ReadAllLines(marker);
        return lines.Contains("Product=SMB Manager") &&
            lines.Any(l => l.StartsWith("InstallDir=", StringComparison.Ordinal) && Same(l["InstallDir=".Length..], root));
    }
    private static void EmptyDirectory(string path)
    {
        Plain(path);
        if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path, false);
    }
    private static void StopKnownApplications(string[] apps)
    {
        using var self = Process.GetCurrentProcess();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                string? path;
                try
                {
                    if (process.Id == self.Id || process.SessionId != self.SessionId || process.HasExited) continue;
                    path = process.MainModule?.FileName;
                }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { continue; }
                if (path is null || !apps.Any(p => Same(p, path))) continue;
                if (!Matches(path, System.IO.Path.GetFileName(path))) throw new IOException("실행 중인 구버전 파일 확인 필요");
                var message = RegisterWindowMessage("SmbManager.Setup.RequestExit");
                EnumWindows((window, _) => { GetWindowThreadProcessId(window, out var pid); if (pid == process.Id) PostMessage(window, message, IntPtr.Zero, IntPtr.Zero); return true; }, IntPtr.Zero);
                process.CloseMainWindow();
                if (!process.WaitForExit(5000)) throw new IOException("실행 중인 구버전 종료 대기 — 강제 종료하지 않았습니다.");
            }
        }
    }
    private delegate bool WindowCallback(IntPtr handle, IntPtr state);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, IntPtr state);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint id);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr handle, uint message, IntPtr a, IntPtr b);

    private static string ReadShortcut(string path)
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException();
        Plain(path);
        object? instance = null;
        try
        {
            instance = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"))!);
            ((IPersistFile)instance!).Load(path, 0);
            var target = new StringBuilder(32768);
            ((IShellLinkW)instance).GetPath(target, target.Capacity, IntPtr.Zero, 4);
            return Environment.ExpandEnvironmentVariables(target.ToString());
        }
        finally { if (instance is not null && Marshal.IsComObject(instance)) Marshal.FinalReleaseComObject(instance); }
    }
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int count, IntPtr data, uint flags);
    }
}

