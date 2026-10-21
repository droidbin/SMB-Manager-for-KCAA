using System.Runtime.InteropServices;
using System.Text;
using SmbManager.Local;
using SmbManager.Local.Client;

if (!OperatingSystem.IsWindows() || Environment.MachineName != "SMBALPHA-CLIENT" || Environment.UserName != "SMBAlphaLab" || !File.Exists(@"C:\Lab\single-vm.json"))
    throw new InvalidOperationException("This QA runs only in the dedicated single lab VM user session.");
const string unc = @"\\10.77.30.1\SMBAlphaLab";
var root = Path.Combine(@"C:\Lab\InternalBeta1\.local\real-qa", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
Directory.CreateDirectory(root);
var passed = new List<string>();
var cleanupErrors = new List<string>();
string? failure = null;
string observedAccount = "";
var letters = new[] { "Q:", "T:", "P:", "O:" }.Where(d => !DriveInfo.GetDrives().Any(x => x.Name.StartsWith(d, StringComparison.OrdinalIgnoreCase)) && Native.Get(d).Code == 2250).Take(2).ToArray();
if (letters.Length != 2) throw new InvalidOperationException("Two unused QA drive letters are required.");
var profile = new Profile("vm-qa-owned", "Disposable QA mapping", "Custom", 1, unc, letters[0], "시험");
var foreign = new Profile("vm-qa-foreign", "External ownership preservation QA", "Custom", 1, unc, letters[1], "시험");
var policy = Path.Combine(root, "policy");
RealSmbEngine? engine = null;
bool externalCreated = false;
string? disposableFile = null;
void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); passed.Add(message); Console.WriteLine("PASS " + message); }
bool Mapped(string drive) { var m = Native.Get(drive); return m.Code == 0 && string.Equals(m.Remote.TrimEnd('\\'), unc, StringComparison.OrdinalIgnoreCase); }
try {
    Check(Native.Connect(foreign.Drive, unc) == 0, "Disposable external QA mapping establishes the test-account session");
    externalCreated = true;
    var account = Native.Account(foreign.Drive); observedAccount = account;
    Check(AssignmentRules.ValidAccount(account), "Actual lab Drive account is readable");
    engine = new RealSmbEngine(policy);
    engine.EnableManagedMode();
    int revision = 0;
    async Task Assign(bool resume = false, bool force = false, Profile[]? allowed = null) {
        var now = DateTimeOffset.UtcNow;
        await engine.ApplyPolicy(new DepartmentPolicy("vm-qa", "vm-qa", ++revision, "시험", account,
            allowed ?? [profile, foreign], true, force, resume, now, now.AddSeconds(30), revision), CancellationToken.None);
    }
    var denied = await engine.Connect("before-approval", profile, CancellationToken.None);
    Check(denied.State == "Failed" && denied.Detail.StartsWith("MANAGED_PERMISSION_DENIED"), "Actual connection denied before administrator assignment");
    await Assign();
    var connected = await engine.Connect("connect", profile, CancellationToken.None);
    Check(connected.State == "Succeeded" && Mapped(profile.Drive), "Actual WNet mapping succeeds using the VM user's credentials");
    disposableFile = Path.Combine(unc, "qa-" + Guid.NewGuid().ToString("N") + ".txt");
    File.WriteAllText(disposableFile, "SMB Manager disposable VM QA");
    Check(File.ReadAllText(disposableFile) == "SMB Manager disposable VM QA", "Disposable share write/read succeeds");
    Check(Native.Disconnect(profile.Drive, false) == 0, "Only the QA-created mapping is removed to simulate a disconnect");
    await Task.Delay(TimeSpan.FromSeconds(32));
    await Assign();
    await engine.ReconnectDue(CancellationToken.None);
    Check(Mapped(profile.Drive), "Automatic reconnect restores the disconnected QA mapping");
    Check(Mapped(foreign.Drive), "External mapping remains available for preservation QA");
    externalCreated = true;

    var external = await engine.Connect("foreign-existing", foreign, CancellationToken.None);
    Check(external.State == "Succeeded" && external.Detail.StartsWith("ALREADY_CONNECTED"), "Existing external mapping is used without taking ownership");
    var incident = engine.LatchQuarantine("Disposable VM QA incident");
    var partial = await engine.Contain(CancellationToken.None);
    Check(partial.StartsWith("PARTIAL_BLOCK"), "Containment honestly reports incomplete network protection");
    Check(!Mapped(profile.Drive), "Quarantine removes the app-owned QA mapping");
    Check(Mapped(foreign.Drive), "Quarantine preserves the external mapping");
    var blocked = await engine.Connect("quarantine-denied", profile, CancellationToken.None);
    Check(blocked.State == "Failed" && blocked.Detail.StartsWith("QUARANTINED"), "Actual reconnect command is denied during quarantine");
    engine.Dispose(); engine = new RealSmbEngine(policy); engine.EnableManagedMode(); await Assign();
    Check(engine.Quarantined && engine.IncidentId == incident, "Quarantine persists after engine restart");
    await engine.ReconnectDue(CancellationToken.None);
    Check(!Mapped(profile.Drive), "Restart does not reconnect a quarantined mapping");
    bool staleRejected = false;
    try { engine.Release("stale-incident"); } catch (InvalidOperationException) { staleRejected = true; }
    Check(staleRejected && engine.Quarantined, "Stale incident release is rejected");
    engine.Release(incident);
    await engine.ReconnectDue(CancellationToken.None);
    Check(!Mapped(profile.Drive), "Incident release does not silently reconnect");
    var resumed = await engine.Connect("explicit-resume", profile, CancellationToken.None);
    Check(resumed.State == "Succeeded" && Mapped(profile.Drive), "Explicit post-release command can reconnect");
    await engine.DisconnectAllOwned(CancellationToken.None);
    var stopped = await engine.Connect("local-stop-denied", profile, CancellationToken.None);
    Check(stopped.State == "Failed" && stopped.Detail.StartsWith("LOCAL_STOPPED"), "Local stop blocks new remote connect commands");
    await Assign(resume:true,force:true);
    var finalConnect = await engine.Connect("force-test-connect", profile, CancellationToken.None);
    Check(finalConnect.State == "Succeeded", "Administrator reassignment explicitly resumes the stopped PC");
    using (var handle = new FileStream(Path.Combine(profile.Drive + @"\", Path.GetFileName(disposableFile)), FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) {
        engine.LatchQuarantine("Explicit force-disconnect VM QA");
        await engine.Contain(CancellationToken.None);
        Check(!Mapped(profile.Drive), "Explicitly approved force disconnect removes mapping with a disposable file open");
    }
    Check(File.Exists(disposableFile), "Direct UNC remains reachable: full Guard isolation is not claimed");
} catch (Exception ex) { failure = ex.ToString(); Console.Error.WriteLine(ex.Message); }
finally {
    if (engine is not null) {
        try { await engine.DisconnectAllOwned(CancellationToken.None); } catch (Exception ex) { cleanupErrors.Add(ex.Message); }
        engine.Dispose();
    }
    if (externalCreated) {
        try {
            if (Mapped(foreign.Drive)) { var result = Native.Disconnect(foreign.Drive, true); if (result != 0) cleanupErrors.Add("External QA mapping cleanup: " + result); }
            else if (Native.Get(foreign.Drive).Code != 2250) cleanupErrors.Add("Foreign drive changed; preserved for manual review");
        } catch (Exception ex) { cleanupErrors.Add(ex.Message); }
    }
    if (disposableFile is not null) { try { File.Delete(disposableFile); } catch (Exception ex) { cleanupErrors.Add(ex.Message); } }
    Wire.Save(Path.Combine(root, "report.json"), new { version = "2.0.0-beta.1", machine = Environment.MachineName, user = Environment.UserName, observedAccount, passed, failure, cleanupErrors, actualNetworkGuard = false, at = DateTimeOffset.Now });
}
Console.WriteLine("REPORT " + Path.Combine(root, "report.json"));
Environment.ExitCode = failure is null && cleanupErrors.Count == 0 ? 0 : 1;

static class Native {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct Resource { public int Scope, Type, DisplayType, Usage; public string? LocalName, RemoteName, Comment, Provider; }
    public static (int Code, string Remote) Get(string drive) { int size = 32768; var b = new StringBuilder(size); return (WNetGetConnection(drive, b, ref size), b.ToString()); }
    public static string Account(string drive) { int size=512;var b=new StringBuilder(size);int result=WNetGetUser(drive,b,ref size);if(result!=0)throw new InvalidOperationException("WNetGetUser failed: "+result);return b.ToString(); }
    public static int Connect(string drive, string unc) { var r = new Resource { Type = 1, LocalName = drive, RemoteName = unc }; return WNetAddConnection2(ref r, null, null, 0); }
    public static int Disconnect(string drive, bool force) => WNetCancelConnection2(drive, 0, force);
    [DllImport("mpr.dll", CharSet = CharSet.Unicode)] static extern int WNetGetUser(string name, StringBuilder user, ref int length);
    [DllImport("mpr.dll", CharSet = CharSet.Unicode)] static extern int WNetGetConnection(string name, StringBuilder remote, ref int length);
    [DllImport("mpr.dll", CharSet = CharSet.Unicode)] static extern int WNetAddConnection2(ref Resource resource, string? password, string? user, int flags);
    [DllImport("mpr.dll", CharSet = CharSet.Unicode)] static extern int WNetCancelConnection2(string name, int flags, [MarshalAs(UnmanagedType.Bool)] bool force);
}

