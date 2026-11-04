using System.Text.RegularExpressions;

namespace SmbManager.Local;

public sealed record CompanyAnnouncement(string Id,string Text,DateTimeOffset CreatedUtc,DateTimeOffset ExpiresUtc);
public sealed record PublishAnnouncement(string Text);
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record SubmitBugReport(Guid Id,string Category,string Description,string AppVersion,int WindowsBuild);
public sealed record BugReport(string Id,string DeviceAlias,DateTimeOffset ReceivedUtc,string Category,string Description,string AppVersion,int WindowsBuild);
public static class BugReportPrivacy
{
    public static readonly string[] Categories=["연결 오류","업데이트·설치","화면·사용성","기타"];
    public static string Redact(string text)
    {
        text=Regex.Replace(text,@"[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}","[이메일]",RegexOptions.IgnoreCase);
        text=Regex.Replace(text,@"\b(?:\d{1,3}\.){3}\d{1,3}\b|\b(?:[A-F0-9]{2}[:-]){5}[A-F0-9]{2}\b|\b[A-F0-9]{8}-(?:[A-F0-9]{4}-){3}[A-F0-9]{12}\b","[장치 정보]",RegexOptions.IgnoreCase);
        text=Regex.Replace(text,@"(?:[A-Z]:\\|\\\\)[^\r\n]*","[경로]",RegexOptions.IgnoreCase);
        text=Regex.Replace(text,@"\b[^\s\\]+\\[^\s\\]+","[계정]");
        text=Regex.Replace(text,@"(?i)(password|passwd|pwd|비밀번호|암호)\s*[:=]\s*\S+","[비밀값]");
        return new string(text.Where(c=>!char.IsControl(c)||c is '\r' or '\n' or '\t').ToArray()).Trim();
    }
    public static bool Valid(SubmitBugReport r)=>r.Id!=Guid.Empty&&Categories.Contains(r.Category)&&r.Description is {Length:>0 and <=2000}
        &&Regex.IsMatch(r.AppVersion??"",@"^\d{1,4}\.\d{1,4}\.\d{1,4}\.\d{1,4}$")&&r.WindowsBuild is >0 and <1000000;
}
