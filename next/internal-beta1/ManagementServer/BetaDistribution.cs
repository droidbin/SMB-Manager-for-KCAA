using System.Security.Cryptography;
using SmbManager.Local;

sealed partial class Store
{
    // Only local authenticated administration can grant Administrator; department comes from
    // the existing server assignment, never from enrollment name/user or client JSON.
    private bool CanBeta(string id, string channel) => BetaChannels.Valid(channel) && devices[id].Approved &&
        (channel == BetaChannels.Open || devices[id].BetaAdministrator || assignments.GetValueOrDefault(id)?.Department == "운영부");
    private BetaAccess BetaView(string id)
    {
        var d = devices[id];
        var allowed = new[] { BetaChannels.Open, BetaChannels.Closed }.Where(c => CanBeta(id, c)).ToArray();
        return new(CanBeta(id, d.BetaChannel) ? d.BetaChannel : BetaChannels.Open, allowed,
            assignments.GetValueOrDefault(id)?.Department ?? "미배정", d.BetaAdministrator);
    }
    public IResult BetaChannelsFor(string id) { lock (gate) return Results.Ok(BetaView(id)); }
    public IResult SelectBeta(string id, BetaSelection request)
    {
        lock (gate)
        {
            if (!BetaChannels.Valid(request.Channel)) return Results.BadRequest();
            if (!CanBeta(id, request.Channel)) return Results.StatusCode(403);
            devices[id].BetaChannel = request.Channel; Persist(); return Results.Ok(BetaView(id));
        }
    }
    public IResult GrantBetaAdministrator(string id, BetaAdministrator request)
    {
        lock (gate)
        {
            if (!devices.TryGetValue(id, out var d)) return Results.NotFound();
            d.BetaAdministrator = request.Administrator;
            if (!CanBeta(id, d.BetaChannel)) d.BetaChannel = BetaChannels.Open;
            Persist(); return Results.Ok(BetaView(id));
        }
    }
    public IResult AdminBetaAccess(string id) { lock (gate) return devices.ContainsKey(id) ? Results.Ok(BetaView(id)) : Results.NotFound(); }

    private BetaRelease? ReadBetaRelease(string channel, out string path)
    {
        path = "";
        if (!BetaChannels.Valid(channel)) return null;
        var root = Path.Combine(Path.GetDirectoryName(profilesFile)!, "beta-releases");
        var dir = Path.Combine(root, channel);
        var manifest = Path.Combine(dir, "release.json");
        if (!File.Exists(manifest)) return null;
        // Administrator-managed local artifacts only. Never follow a client-provided path/URL.
        foreach (var p in new[] { root, dir, manifest })
            if ((File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) return null;
        var release = Wire.Load<BetaRelease>(manifest);
        if (!BetaUpdate.ValidFileVersion(release.FileVersion) || release.Channel != channel || string.IsNullOrWhiteSpace(release.Version) || release.Version.Length > 80 ||
            release.FileName is null || release.FileName.Length > 120 ||
            !System.Text.RegularExpressions.Regex.IsMatch(release.FileName, @"\ASMB-Manager-[a-zA-Z0-9._-]+\.msi\z") ||
            release.Size is <= 0 or > 1_073_741_824 || release.Sha256 is null ||
            !System.Text.RegularExpressions.Regex.IsMatch(release.Sha256, @"\A[0-9a-fA-F]{64}\z") || release.Notes is null || release.Notes.Length > 4000) return null;
        path = Path.Combine(dir, release.FileName);
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).Length != release.Size) return null;
        return release;
    }
    public IResult BetaArtifact(string id, string channel, bool download)
    {
        lock (gate)
        {
            // Re-evaluate approval and department on EVERY metadata/download request, including
            // direct URLs for an old selection. Revocation does not tear down business SMB maps.
            if (!BetaChannels.Valid(channel)) return Results.BadRequest();
            if (!CanBeta(id, channel)) return Results.StatusCode(403);
            FileStream? stream = null;
            try
            {
                var release = ReadBetaRelease(channel, out var path);
                if (release is null) return Results.NotFound(new { error = "NO_BETA_RELEASE" });
                if (!download) return Results.Ok(release);
                stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length != release.Size || !Convert.ToHexString(SHA256.HashData(stream)).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                { stream.Dispose(); return Results.Conflict(new { error = "ARTIFACT_INTEGRITY_FAILED" }); }
                stream.Position = 0;
                return Results.File(stream, "application/octet-stream", release.FileName, enableRangeProcessing: false);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            { stream?.Dispose(); return Results.StatusCode(503); }
        }
    }
}
