using System.Net.Http.Json;
using System.Security.Cryptography;
using SmbManager.Local;

namespace SmbManager.Local.Client;

public sealed partial class ClientAgent
{
    public async Task<BetaAccess> GetBetaAccess(CancellationToken ct) =>
        await http.GetFromJsonAsync<BetaAccess>("api/client/beta/channels", Wire.Json, ct) ?? throw new InvalidDataException("채널 응답이 없습니다.");
    public async Task<BetaAccess> SelectBetaChannel(string channel, CancellationToken ct) =>
        await Wire.Read<BetaAccess>(await http.PostAsJsonAsync("api/client/beta/selection", new BetaSelection(channel), Wire.Json, ct), ct);
    public async Task<BetaRelease?> GetBetaRelease(string channel, CancellationToken ct)
    {
        if (!BetaChannels.Valid(channel)) throw new ArgumentException("채널이 올바르지 않습니다.");
        using var response = await http.GetAsync("api/client/beta/releases/" + channel, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var release = await response.Content.ReadFromJsonAsync<BetaRelease>(Wire.Json, ct) ?? throw new InvalidDataException("배포 응답이 없습니다.");
        if (!BetaUpdate.ValidFileVersion(release.FileVersion) || release.Channel != channel || release.Size is <= 0 or > 1_073_741_824 || release.Sha256 is null ||
            !System.Text.RegularExpressions.Regex.IsMatch(release.Sha256, @"\A[0-9a-fA-F]{64}\z") ||
            release.FileName is null || !System.Text.RegularExpressions.Regex.IsMatch(release.FileName, @"\ASMB-Manager-[a-zA-Z0-9._-]+\.msi\z"))
            throw new InvalidDataException("배포 정보 검증 실패");
        return release;
    }
    public async Task DownloadBeta(BetaRelease release, string destination, CancellationToken ct)
    {
        // Re-authorize on download; never embed credentials in URLs or run an installer silently.
        if (!BetaChannels.Valid(release.Channel) || release.Size is <= 0 or > 1_073_741_824) throw new InvalidDataException();
        var temp = Path.GetFullPath(destination) + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            using var client = Wire.Http(Identity.Url, device: Identity.DeviceKey);
            client.Timeout = TimeSpan.FromMinutes(15);
            using var response = await client.GetAsync("api/client/beta/download/" + release.Channel, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[81920]; long size = 0; int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    size += read; if (size > release.Size) throw new InvalidDataException("배포 파일 크기 불일치");
                    hash.AppendData(buffer, 0, read); await output.WriteAsync(buffer.AsMemory(0, read), ct);
                }
                if (size != release.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("배포 파일 무결성 검증 실패");
                await output.FlushAsync(ct);
            }
            File.Move(temp, destination, false);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
