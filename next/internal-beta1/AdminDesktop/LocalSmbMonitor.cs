using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace SmbManager.Local.AdminDesktop;

// Local OS tables only: no network scan, remote queries, or connection changes.
internal static class LocalSmbSnapshot
{
    internal sealed record Peer(string Ip, int Connections, string States);
    internal sealed record Session(string Client, string User, uint Seconds, uint Idle);
    internal sealed record Snapshot(Peer[] Peers, Session[] Sessions, string? TcpError, string? SessionError, DateTimeOffset At);

    internal static bool InternalAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return false;
        var b = address.GetAddressBytes();
        return address.AddressFamily == AddressFamily.InterNetwork
            ? b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254)
            : address.IsIPv6LinkLocal || (b[0] & 0xfe) == 0xfc;
    }

    internal static Snapshot Read()
    {
        Peer[] peers = []; Session[] sessions = []; string? tcpError = null, sessionError = null;
        try
        {
            peers = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections()
                .Where(c => c.LocalEndPoint.Port == 445 && InternalAddress(c.RemoteEndPoint.Address))
                .GroupBy(c => (c.RemoteEndPoint.Address.IsIPv4MappedToIPv6 ? c.RemoteEndPoint.Address.MapToIPv4() : c.RemoteEndPoint.Address).ToString())
                .Select(g => new Peer(g.Key, g.Count(c => c.State == TcpState.Established), string.Join(", ", g.Select(c => c.State.ToString()).Distinct())))
                .OrderBy(p => p.Ip, StringComparer.Ordinal).ToArray();
        }
        catch (Exception e) when (e is NetworkInformationException or SocketException) { tcpError = e.Message; }
        try { sessions = ReadSessions(); }
        catch (Win32Exception e) { sessionError = e.NativeErrorCode == 5 ? "조회 권한 부족 (5). 필요한 경우 관리 프로그램만 관리자 권한으로 실행하세요." : $"SMB 세션 조회 실패 ({e.NativeErrorCode}): {e.Message}"; }
        return new(peers, sessions, tcpError, sessionError, DateTimeOffset.Now);
    }

    private static Session[] ReadSessions()
    {
        var result = new List<Session>(); uint resume = 0; int status;
        do
        {
            IntPtr buffer = IntPtr.Zero;
            try
            {
                // null server means this computer. Level 10 contains client/user/time only.
                status = NetSessionEnum(null, null, null, 10, out buffer, 64 * 1024, out var count, out _, ref resume);
                if (status is not (0 or 234)) throw new Win32Exception(status);
                var size = Marshal.SizeOf<SessionInfo10>();
                for (var i = 0; i < count; i++)
                {
                    var item = Marshal.PtrToStructure<SessionInfo10>(IntPtr.Add(buffer, checked((int)i * size)));
                    result.Add(new(Marshal.PtrToStringUni(item.Client)?.TrimStart('\\') ?? "알 수 없음",
                        Marshal.PtrToStringUni(item.User) ?? "", item.Seconds, item.Idle));
                }
                if (status == 234 && count == 0) throw new Win32Exception(234);
            }
            finally { if (buffer != IntPtr.Zero) NetApiBufferFree(buffer); }
        } while (status == 234);
        return result.ToArray();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SessionInfo10 { public IntPtr Client, User; public uint Seconds, Idle; }
    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetSessionEnum(string? server, string? client, string? user, uint level,
        out IntPtr buffer, uint preferredLength, out uint entries, out uint total, ref uint resume);
    [DllImport("Netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);
}

internal sealed class LocalSmbMonitor : UserControl
{
    private readonly Label summary = new() { AutoSize = true, Padding = new(0, 6, 0, 6), Text = "로컬 연결 정보 조회 중…" };
    private readonly Label status = new() { AutoSize = true, Padding = new(0, 6, 0, 6) };
    private readonly DataGridView peers = Grid();
    private readonly DataGridView sessions = Grid();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 5000 };
    private readonly Dictionary<string, Seen> history = [];
    private HashSet<string> connected = [];
    private bool busy;
    private sealed record Seen(DateTimeOffset First, DateTimeOffset Last);

    public LocalSmbMonitor()
    {
        Dock = DockStyle.Fill;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(14), ColumnCount = 1, RowCount = 8, AutoScroll = true };
        foreach (var type in new[] { SizeType.AutoSize, SizeType.AutoSize, SizeType.AutoSize, SizeType.Percent, SizeType.AutoSize, SizeType.Percent, SizeType.AutoSize, SizeType.AutoSize })
            root.RowStyles.Add(new(type, type == SizeType.Percent ? 50 : 0));
        Controls.Add(root);
        root.Controls.Add(new Label { AutoSize = true, Text = "이 PC로 들어오는 SMB 연결 · 5초마다 자동 갱신\n내부 IP 기준 집계이며, IP 수가 실제 물리 PC 대수와 항상 같지는 않습니다.", ForeColor = Color.DarkBlue }, 0, 0);
        root.Controls.Add(summary, 0, 1);
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        var refresh = new Button { Text = "지금 새로고침", AutoSize = true, MinimumSize = new(150, 38) };
        refresh.Click += async (_, _) => await RefreshSnapshot(); actions.Controls.Add(refresh);
        var clear = new Button { Text = "끊긴 IP 기록 지우기", AutoSize = true, MinimumSize = new(185, 38) };
        clear.Click += async (_, _) =>
        {
            if (busy) return;
            foreach (var ip in history.Keys.Where(ip => !connected.Contains(ip)).ToArray()) history.Remove(ip);
            await RefreshSnapshot();
        }; actions.Controls.Add(clear);
        root.Controls.Add(actions, 0, 2); root.Controls.Add(peers, 0, 3);
        root.Controls.Add(new Label { AutoSize = true, Padding = new(0, 8, 0, 6), Text = "Windows SMB 세션 · 이름과 IP를 확인할 수 없으면 임의로 합치지 않습니다." }, 0, 4);
        root.Controls.Add(sessions, 0, 5); root.Controls.Add(status, 0, 6);
        root.Controls.Add(new Label { AutoSize = true, Text = "범위: 수신 TCP 445, 사설 IPv4·링크 로컬·사설 IPv6.\nTCP 연결은 SMB 인증 성공을 보장하지 않습니다. 마지막 관측 기록은 실행 중 최대 24시간 보관합니다." }, 0, 7);
        UiTheme.Descendants(this);
        timer.Tick += async (_, _) => await RefreshSnapshot();
        Load += async (_, _) => { timer.Start(); await RefreshSnapshot(); };
    }

    private async Task RefreshSnapshot()
    {
        if (busy || IsDisposed) return;
        busy = true;
        try
        {
            var snapshot = await Task.Run(LocalSmbSnapshot.Read);
            if (IsDisposed) return;
            // Failed observations must not turn a previously connected peer into a disconnected peer.
            if (snapshot.TcpError is null)
            {
                connected = snapshot.Peers.Where(p => p.Connections > 0).Select(p => p.Ip).ToHashSet();
                foreach (var old in history.Where(p => snapshot.At - p.Value.Last > TimeSpan.FromHours(24)).Select(p => p.Key).ToArray()) history.Remove(old);
                foreach (var peer in snapshot.Peers.Where(p => p.Connections > 0))
                    history[peer.Ip] = history.TryGetValue(peer.Ip, out var seen) ? seen with { Last = snapshot.At } : new(snapshot.At, snapshot.At);
                var lookup = snapshot.Peers.ToDictionary(p => p.Ip);
                peers.DataSource = history.Keys.Union(lookup.Keys).Order(StringComparer.Ordinal).Select(ip =>
                {
                    lookup.TryGetValue(ip, out var peer); history.TryGetValue(ip, out var seen);
                    return new { 내부IP = ip, 상태 = peer?.Connections > 0 ? "연결 중" : peer is not null ? "종료/전환 중" : "연결 끊김",
                        TCP연결수 = peer?.Connections ?? 0, TCP상태 = peer?.States ?? "현재 연결 없음",
                        최초관측 = seen?.First.ToString("MM-dd HH:mm:ss") ?? "—", 마지막연결관측 = seen?.Last.ToString("MM-dd HH:mm:ss") ?? "—" };
                }).ToArray();
                summary.Text = $"연결된 내부 IP: {snapshot.Peers.Count(p => p.Connections > 0)}개 | TCP 연결: {snapshot.Peers.Sum(p => p.Connections)}개 | 갱신: {snapshot.At:HH:mm:ss}";
            }
            else summary.Text = "TCP 조회 실패 — 아래 표는 마지막 성공 관측값입니다: " + snapshot.TcpError;
            if (snapshot.SessionError is null)
                sessions.DataSource = snapshot.Sessions.Select(s => new { 접속PC또는IP = s.Client, 사용자 = s.User, 상태 = "세션 존재", 연결시간 = Duration(s.Seconds), 유휴시간 = Duration(s.Idle) }).ToArray();
            else sessions.DataSource = null;
            status.Text = snapshot.SessionError is null ? $"SMB 세션 {snapshot.Sessions.Length}개 (로컬 전체, IP 미확인 포함) · {snapshot.At:HH:mm:ss}" : snapshot.SessionError;
        }
        catch (Exception e) { if (!IsDisposed) status.Text = "조회 실패 — 현재 상태 확인 불가: " + e.Message; }
        finally { busy = false; }
    }
    private static string Duration(uint seconds) { var t = TimeSpan.FromSeconds(seconds); return $"{(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2}"; }
    private static DataGridView Grid() => new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells,
        ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
        DefaultCellStyle = new() { WrapMode = DataGridViewTriState.True, Padding = new Padding(2) }, SelectionMode = DataGridViewSelectionMode.FullRowSelect };
    protected override void Dispose(bool disposing) { if (disposing) { timer.Stop(); timer.Dispose(); } base.Dispose(disposing); }
}
