using System.Diagnostics;
using System.Net;
using System.Text.Json;

namespace SmbManager.Local;

public static class ServerStartupReadiness
{
    public static async Task<bool> ProbeAsync(string file, int processId, int port, CancellationToken ct)
    {
        try
        {
            var ready = Wire.Load<ServerReady>(file);
            if (ready is null || ready.ProcessId != processId || string.IsNullOrWhiteSpace(ready.AdminKey)
                || !Uri.TryCreate(ready.Url, UriKind.Absolute, out var url)
                || url.Scheme != "http" || url.Host != "127.0.0.1" || url.Port != port
                || url.AbsolutePath != "/" || url.UserInfo.Length != 0 || url.Query.Length != 0 || url.Fragment.Length != 0)
                return false;
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false });
            client.DefaultRequestHeaders.Add("X-Admin-Key", ready.AdminKey);
            using var response = await client.GetAsync(new Uri(url, "api/admin/devices"), HttpCompletionOption.ResponseHeadersRead, ct);
            return response.StatusCode == HttpStatusCode.OK;
        }
        catch (Exception e) when (e is IOException or JsonException or HttpRequestException or UnauthorizedAccessException or ArgumentException or FormatException)
        { return false; }
    }

    public static async Task WaitAsync(string file, Process server, int port, TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        try
        {
            while (!server.HasExited)
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                attempt.CancelAfter(TimeSpan.FromSeconds(2));
                try { if (await ProbeAsync(file, server.Id, port, attempt.Token) && !server.HasExited) return; }
                catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { }
                await Task.Delay(100, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
        throw new IOException("관리 서버 연결을 확인하지 못했습니다. 포트 48751 사용 여부와 인증서·설정을 확인하세요.");
    }
}
