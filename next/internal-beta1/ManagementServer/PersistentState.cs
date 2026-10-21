using SmbManager.Local;

sealed partial class Store
{
    public bool StorageHealthy { get; private set; } = true;
    private string PersistentFile => Path.Combine(Path.GetDirectoryName(profilesFile)!, ".server-secrets", Path.GetFileName(profilesFile) + ".protected");
    private sealed record SavedDevice(string Id, string Name, string User, string Key, string SessionId, bool Approved, bool BetaAdministrator = false, string BetaChannel = BetaChannels.Open, HardwareIdentity? Hardware = null, string RequestedDepartment = "", string ComputerName = "", DateTimeOffset RequestedAt = default, RegistrationCorrection? Correction = null, bool Archived = false, bool ConnectionsHeld = false);
    private sealed record SavedWork(CommandPayload Payload, string Signature, string DeviceName, string State, string Detail);
    private sealed record ServerState(int Schema, string PrivateKey, string AdminKey, SavedDevice[] Devices, SavedWork[] Commands, AuditEntry[]? Audit = null);
    private void LoadPersistentState()
    {
        if (!File.Exists(PersistentFile)) { Persist(); return; }
        // A corrupt, moved or unreadable state is fatal. Never silently replace device or signing keys.
        var state = SecretFile.Load<ServerState>(PersistentFile);
        if (state.Schema != 1) throw new InvalidDataException("지원하지 않는 서버 상태 형식입니다.");
        signing.ImportFromPem(state.PrivateKey);
        AdminKey = state.AdminKey;
        foreach (var d in state.Devices)
            devices.Add(d.Id, new Device(d.Id, d.Name, d.User, d.Key, d.SessionId) { RequestedDepartment = d.RequestedDepartment, ComputerName = d.ComputerName, RequestedAt = d.RequestedAt, Correction = d.Correction, Archived = d.Archived, ConnectionsHeld = d.ConnectionsHeld, Approved = d.Approved && HardwareIdentity.Valid(d.Hardware), Hardware = d.Hardware, LastSeen = DateTimeOffset.MinValue, BetaAdministrator = d.BetaAdministrator && HardwareIdentity.Valid(d.Hardware), BetaChannel = BetaChannels.Valid(d.BetaChannel) ? d.BetaChannel : BetaChannels.Open });
        audit.AddRange(state.Audit??[]);
        foreach (var w in state.Commands)
        {
            // Outcome may already have happened on the client. Do not replay an interrupted lease.
            var interrupted = w.State is "Leased" or "Running";
            commands.Add(w.Payload.Id, new Work(w.Payload, w.Signature, w.DeviceName) {
                State = interrupted ? "Unknown" : w.State,
                Detail = interrupted ? "SERVER_RESTART: 실행 결과 확인 필요; 자동 재실행 금지" : w.Detail
            });
        }
        Expire();
        Persist();
    }
    private void Persist()
    {
        try { SecretFile.Save(PersistentFile, new ServerState(1, signing.ExportPkcs8PrivateKeyPem(), AdminKey,
            devices.Values.Select(d => new SavedDevice(d.Id, d.Name, d.User, d.Key, d.SessionId, d.Approved, d.BetaAdministrator, d.BetaChannel, d.Hardware, d.RequestedDepartment, d.ComputerName, d.RequestedAt, d.Correction, d.Archived, d.ConnectionsHeld)).ToArray(),
            commands.Values.Select(w => new SavedWork(w.Payload, w.Signature, w.DeviceName, w.State, w.Detail)).ToArray(),audit.ToArray())); }
        catch { StorageHealthy=false; throw; } // Refuse further management actions until storage is repaired and the server restarted.
    }
}
