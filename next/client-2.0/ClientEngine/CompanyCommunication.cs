using System.Net.Http.Json;
using System.Reflection;
using SmbManager.Local;

namespace SmbManager.Local.Client;
public sealed partial class ClientAgent
{
    public async Task SendBugReport(Guid id,string category,string description,CancellationToken ct)
    {
        if(!managementApproved)throw new InvalidOperationException("회사 등록 승인 후 신고할 수 있습니다.");
        var version=typeof(ClientAgent).Assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version??"0.0.0.0";
        var report=new SubmitBugReport(id,category,BugReportPrivacy.Redact(description),version,Environment.OSVersion.Version.Build);
        if(!BugReportPrivacy.Valid(report))throw new InvalidOperationException("분류와 신고 내용을 확인하세요.");
        using var response=await http.PostAsJsonAsync("api/client/bug-reports",report,Wire.Json,ct);
        if(!response.IsSuccessStatusCode)throw new InvalidOperationException(response.StatusCode==System.Net.HttpStatusCode.TooManyRequests?"신고 횟수가 많습니다. 잠시 후 다시 시도하세요.":"신고를 전송하지 못했습니다. 서버 연결과 기능 버전을 확인한 뒤 다시 시도하세요.");
    }
    private async Task CommunicationLoop(CancellationToken ct)
    {
        while(!ct.IsCancellationRequested){
            var delay=TimeSpan.FromSeconds(managementApproved?30:2);
            try{
                if(managementApproved){
                    using var response=await http.GetAsync("api/client/announcements",ct);
                    if(response.IsSuccessStatusCode){
                        var rows=await response.Content.ReadFromJsonAsync<CompanyAnnouncement[]>(Wire.Json,ct)??[];
                        foreach(var row in rows.TakeLast(20).Where(r=>Guid.TryParse(r.Id,out _)&&r.Text is {Length:>0 and <=1000}&&r.ExpiresUtc>PolicyUtcNow))executor.ReceiveAnnouncement(row);
                    }else delay=TimeSpan.FromMinutes(2);
                }
            }catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
            catch{delay=TimeSpan.FromMinutes(2);} // Notice failures do not change SMB connection state.
            try{await Task.Delay(delay,ct);}catch(OperationCanceledException){break;}
        }
    }
}
