using System.Security.Cryptography;

namespace SmbManager.Local.Client;

// Alpha sensor observes only its own disposable local canary, never business files or a shared server.
public sealed class CanarySensor : IDisposable
{
    private readonly FileSystemWatcher watcher;
    private readonly string file;
    private readonly byte[] baseline;
    private readonly Action<string> onThreat;
    private readonly System.Threading.Timer debounce;
    private int readFailures;
    private volatile bool tampered;
    private volatile bool degraded;
    private volatile bool disposed;
    public bool Healthy => !disposed && !tampered && !degraded;
    public string Status => degraded ? "감시 오류 — 보호 미확인" : tampered ? "탐지 파일 변조 감지 — 모의 격리" : "앱 전용 로컬 탐지 파일 감시 (랜섬웨어 종합 감지 아님)";
    public CanarySensor(string directory, Action<string> threat)
    {
        Directory.CreateDirectory(directory); file = Path.Combine(directory, "alpha-canary.txt");
        File.WriteAllText(file, "SMB Manager alpha disposable canary " + Guid.NewGuid());
        baseline = SHA256.HashData(File.ReadAllBytes(file)); onThreat = threat;
        debounce = new System.Threading.Timer(_ => Observe(), null, Timeout.Infinite, Timeout.Infinite);
        watcher = new FileSystemWatcher(directory) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
        watcher.Changed += (_, _) => Schedule(); watcher.Deleted += (_, _) => Schedule(); watcher.Renamed += (_, _) => Schedule();
        watcher.Error += (_, _) => degraded = true;
        watcher.EnableRaisingEvents = true;
    }
    private void Observe()
    {
        if (disposed || tampered) return;
        try
        {
            if (File.Exists(file) && CryptographicOperations.FixedTimeEquals(baseline, SHA256.HashData(File.ReadAllBytes(file)))) return;
            tampered = true; onThreat("앱 전용 탐지 파일 변조 감지");
        }
        catch (IOException) { if (Interlocked.Increment(ref readFailures) < 5) Schedule(); else degraded = true; }
        catch (UnauthorizedAccessException) { degraded = true; }
        catch (Exception) { degraded = true; }
    }
    private void Schedule() { if (!disposed) try { debounce.Change(100, Timeout.Infinite); } catch (ObjectDisposedException) { } }
    public void Demonstrate() { if (disposed) throw new ObjectDisposedException(nameof(CanarySensor)); File.AppendAllText(file, "\nExplicit alpha demonstration"); Schedule(); }
    public void Dispose() { disposed = true; watcher.EnableRaisingEvents = false; watcher.Dispose(); debounce.Dispose(); }
}
