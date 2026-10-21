using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
namespace SmbManager.Local;

public record AdminReleaseVersion(int Major,int Minor,int Patch,int Stage,int Number,int Build):IComparable<AdminReleaseVersion>{
 public int CompareTo(AdminReleaseVersion? other){if(other is null)return 1;var a=new[]{Major,Minor,Patch,Stage,Number,Build};var b=new[]{other.Major,other.Minor,other.Patch,other.Stage,other.Number,other.Build};for(int i=0;i<a.Length;i++){var n=a[i].CompareTo(b[i]);if(n!=0)return n;}return 0;}
 public static AdminReleaseVersion? Parse(string version,int build=0){var m=Regex.Match(version,@"\Av?(\d+)\.(\d+)\.(\d+)(?:-(alpha|beta|rc)\.(\d+))?\z",RegexOptions.IgnoreCase);if(!m.Success)return null;try{return new(int.Parse(m.Groups[1].Value),int.Parse(m.Groups[2].Value),int.Parse(m.Groups[3].Value),m.Groups[4].Value.ToLowerInvariant() switch{"alpha"=>0,"beta"=>1,"rc"=>2,_=>3},m.Groups[5].Success?int.Parse(m.Groups[5].Value):0,build);}catch(OverflowException){return null;}}
}
public record AdminGithubRelease(string Tag,string FileName,long Size,string Sha256,Uri DownloadUrl,string Notes,AdminReleaseVersion Version);
public sealed class AdminGithubUpdate:IDisposable {
 public const string Repository="droidbin/SMB-Manager-for-KCAA";
 public const string ReleasesPage="https://github.com/"+Repository+"/releases";
 const long MaxSize=512L*1024*1024;
 readonly HttpClient http;
 public AdminGithubUpdate(HttpMessageHandler? handler=null){http=new(handler??new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(90)};http.DefaultRequestHeaders.UserAgent.ParseAdd("SMB-Manager-Admin-Updater/1.0");}
 public async Task<AdminGithubRelease?> Latest(bool includeBeta,CancellationToken ct){
  using var response=await http.GetAsync("https://api.github.com/repos/"+Repository+"/releases?per_page=100",HttpCompletionOption.ResponseHeadersRead,ct);
  if(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)throw new InvalidOperationException("GitHub 요청 제한에 도달했습니다. 잠시 후 다시 확인하세요.");
  response.EnsureSuccessStatusCode();
  await using var stream=await response.Content.ReadAsStreamAsync(ct);
  var releases=await System.Text.Json.JsonSerializer.DeserializeAsync<GithubRelease[]>(stream,cancellationToken:ct)??[];
  return Select(releases,includeBeta);
 }
 public static AdminGithubRelease? Select(IEnumerable<GithubRelease> releases,bool includeBeta){
  var found=new List<AdminGithubRelease>();
  foreach(var r in releases){
   if(r.Draft||r.Tag is null)continue;var version=AdminReleaseVersion.Parse(r.Tag);if(version is null||(!includeBeta&&(r.Prerelease||version.Stage!=3))||r.Prerelease!=(version.Stage!=3))continue;
   foreach(var a in r.Assets??[]){
    if(a.Name is null||a.Size<=0||a.Size>MaxSize||a.State!="uploaded"||a.Digest is null||!Regex.IsMatch(a.Digest,@"\Asha256:[0-9a-fA-F]{64}\z"))continue;
    var m=Regex.Match(a.Name,@"\ASMB-Manager-Admin-"+Regex.Escape(r.Tag.TrimStart('v','V'))+@"-build([0-9]+)\.zip\z");
    if(!m.Success||!int.TryParse(m.Groups[1].Value,out var build)||build<1||!Uri.TryCreate(a.DownloadUrl,UriKind.Absolute,out var uri)||!IsReleaseUrl(uri,r.Tag,a.Name))continue;
    found.Add(new(r.Tag,a.Name,a.Size,a.Digest[7..].ToUpperInvariant(),uri,(r.Body??"")[..Math.Min(12000,(r.Body??"").Length)],version with{Build=build}));
   }
  }
  return found.OrderByDescending(r=>r.Version).FirstOrDefault();
 }
 static bool IsReleaseUrl(Uri uri,string tag,string file)=>uri.Scheme=="https"&&uri.Host=="github.com"&&uri.IsDefaultPort&&uri.UserInfo.Length==0&&uri.Query.Length==0&&uri.Fragment.Length==0&&Uri.UnescapeDataString(uri.AbsolutePath).Equals("/"+Repository+"/releases/download/"+tag+"/"+file,StringComparison.Ordinal);
 static bool RedirectAllowed(Uri uri)=>uri.Scheme=="https"&&uri.IsDefaultPort&&uri.UserInfo.Length==0&&uri.Fragment.Length==0&&uri.Host is "github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com";
 public async Task Download(AdminGithubRelease release,string destination,IProgress<int>? progress,CancellationToken ct){
  if(!IsReleaseUrl(release.DownloadUrl,release.Tag,release.FileName)||release.Size<=0||release.Size>MaxSize||!Regex.IsMatch(release.Sha256,@"\A[0-9A-Fa-f]{64}\z"))throw new InvalidOperationException("유효하지 않은 관리자 배포 정보입니다.");
  var target=Path.GetFullPath(destination);if(File.Exists(target))throw new IOException("기존 파일을 보존하려면 다른 이름으로 저장하세요.");
  var temp=target+"."+Guid.NewGuid().ToString("N")+".download";
  try{
   var url=release.DownloadUrl;HttpResponseMessage? response=null;
   try{
    for(int hop=0;hop<5;hop++){
     response=await http.GetAsync(url,HttpCompletionOption.ResponseHeadersRead,ct);
     if((int)response.StatusCode is >=300 and <400){var location=response.Headers.Location;response.Dispose();response=null;if(location is null)throw new IOException("다운로드 이동 주소가 없습니다.");url=new Uri(url,location);if(!RedirectAllowed(url))throw new IOException("허용되지 않은 다운로드 주소입니다.");continue;}
     break;
    }
    if(response is null)throw new IOException("다운로드 이동 횟수 초과");response.EnsureSuccessStatusCode();
    if(response.Content.Headers.ContentLength is long length&&length!=release.Size)throw new IOException("배포 파일 크기가 변경됐습니다. 다시 확인하세요.");
    await using(var input=await response.Content.ReadAsStreamAsync(ct))
    await using(var output=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None,81920,true))
    using(var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256)){
     var buffer=new byte[81920];long total=0;int read;
     while((read=await input.ReadAsync(buffer,ct))>0){total+=read;if(total>release.Size)throw new IOException("배포 파일 크기가 일치하지 않습니다.");hash.AppendData(buffer,0,read);await output.WriteAsync(buffer.AsMemory(0,read),ct);progress?.Report((int)(total*100/release.Size));}
     if(total!=release.Size||!Convert.ToHexString(hash.GetHashAndReset()).Equals(release.Sha256,StringComparison.OrdinalIgnoreCase))throw new IOException("다운로드 SHA-256 검증 실패. 파일을 설치하지 마세요.");
     await output.FlushAsync(ct);
    }
    ct.ThrowIfCancellationRequested();File.Move(temp,target);
   }finally{response?.Dispose();}
  }finally{if(File.Exists(temp))File.Delete(temp);}
 }
 public void Dispose()=>http.Dispose();
 public record GithubRelease([property:JsonPropertyName("tag_name")]string? Tag,[property:JsonPropertyName("draft")]bool Draft,[property:JsonPropertyName("prerelease")]bool Prerelease,[property:JsonPropertyName("body")]string? Body,[property:JsonPropertyName("assets")]GithubAsset[]? Assets);
 public record GithubAsset([property:JsonPropertyName("name")]string? Name,[property:JsonPropertyName("size")]long Size,[property:JsonPropertyName("digest")]string? Digest,[property:JsonPropertyName("browser_download_url")]string? DownloadUrl,[property:JsonPropertyName("state")]string? State="uploaded");
}
