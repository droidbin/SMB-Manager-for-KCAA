using System.Reflection;
using SmbManager.Local;

namespace SmbManager.Local.Client;

internal sealed class DiagnosticJournal
{
    private sealed record Saved(bool Enabled, ClientDiagnosticEvent[] Entries);
    private readonly string file;
    private readonly object gate=new();
    private Saved state=new(true,[]);
    private bool available=true;
    private string last="";
    public DiagnosticJournal(string directory) {
        file=Path.Combine(directory,"events.protected");
        try { if(File.Exists(file)) { state=SecretFile.Load<Saved>(file);state=state with {Entries=state.Entries.Where(DiagnosticPrivacy.Valid).TakeLast(DiagnosticPrivacy.DeviceLimit).ToArray()}; } }
        catch { available=false; } // Leave unreadable evidence intact; never stop SMB work for diagnostics.
    }
    public bool Enabled { get { lock(gate)return available&&state.Enabled; } }
    public bool SetEnabled(bool enabled) { lock(gate){if(!available)return false;state=new(enabled,enabled?state.Entries:[]);last="";Save();return available;} }
    private void Save() { try { SecretFile.Save(file,state); } catch { available=false; } }
    public void Record(DiagnosticCode code, DiagnosticPhase phase, DiagnosticFailure failure, DateTimeOffset at, int connected=0, int other=0) {
        lock(gate){
            if(!available||!state.Enabled)return;
            var signature=$"{code}:{phase}:{failure}:{connected}:{other}";
            if(signature==last)return;
            var version=typeof(ClientAgent).Assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version??"0.0.0.0";
            var entry=new ClientDiagnosticEvent(Guid.NewGuid(),DiagnosticPrivacy.Minute(at),code,phase,failure,version,Environment.OSVersion.Version.Build,connected,other);
            if(!DiagnosticPrivacy.Valid(entry))return;
            last=signature;state=state with{Entries=state.Entries.Append(entry).TakeLast(DiagnosticPrivacy.DeviceLimit).ToArray()};Save();
        }
    }
    public ClientDiagnosticEvent[] Pending(){lock(gate)return available&&state.Enabled?state.Entries.Take(DiagnosticPrivacy.BatchLimit).ToArray():[];}
    public void Acknowledge(IEnumerable<Guid> ids){lock(gate){if(!available||!state.Enabled)return;var accepted=ids.ToHashSet();state=state with{Entries=state.Entries.Where(e=>!accepted.Contains(e.EventId)).ToArray()};Save();}}
}
