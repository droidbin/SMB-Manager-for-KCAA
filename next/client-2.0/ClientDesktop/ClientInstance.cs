using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace SmbManager.Local.ClientDesktop;

// Coordinate before opening client.lock; duplicates only request the existing UI.
internal sealed class ClientInstance : IDisposable
{
    readonly Mutex mutex;
    readonly EventWaitHandle activate;
    RegisteredWaitHandle? listener;
    public bool IsOwner { get; }

    public ClientInstance(string stateDirectory)
    {
        var identity = WindowsIdentity.GetCurrent().User!.Value;
        var scope = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            identity + "|" + Path.TrimEndingDirectorySeparator(Path.GetFullPath(stateDirectory)).ToUpperInvariant())));
        mutex = new Mutex(false, @"Local\KCAA.SmbClient." + scope);
        try { IsOwner = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { IsOwner = true; }
        activate = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\KCAA.SmbClient.Activate." + scope);
    }

    public void RequestActivation() => activate.Set();

    public void Listen(Form form)
    {
        void Start(object? sender, EventArgs args)
        {
            listener ??= ThreadPool.RegisterWaitForSingleObject(activate, (_, _) =>
            {
                try
                {
                    form.BeginInvoke((Action)(() =>
                    {
                        if (form.IsDisposed) return;
                        form.Show();
                        if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal;
                        form.Activate();
                    }));
                }
                catch (InvalidOperationException) { /* Window is shutting down. */ }
            }, null, Timeout.Infinite, false);
        }
        if (form.IsHandleCreated) Start(null, EventArgs.Empty);
        else form.HandleCreated += Start;
    }

    public void Dispose()
    {
        listener?.Unregister(null);
        activate.Dispose();
        if (IsOwner) mutex.ReleaseMutex();
        mutex.Dispose();
    }
}
