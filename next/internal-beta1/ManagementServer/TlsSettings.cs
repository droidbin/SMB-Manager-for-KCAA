using System.Net;
using System.Security.Cryptography.X509Certificates;
using SmbManager.Local;

sealed class TlsSettings
{
    public string BindAddress { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 48653;
    public string CertificateThumbprint { get; set; } = "";
    public X509Certificate2 Certificate { get; private set; } = null!;
    public static TlsSettings Load(string file)
    {
        var result = Wire.Load<TlsSettings>(file);
        if (!IPAddress.TryParse(result.BindAddress, out var ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)
            || result.Port < 1024 || result.Port > 65535 || result.CertificateThumbprint.Length < 40)
            throw new InvalidDataException("HTTPS는 명시적 인터페이스 IP·포트·서버 인증서 지문이 필요합니다.");
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        var certs = store.Certificates.Find(X509FindType.FindByThumbprint, result.CertificateThumbprint, false);
        result.Certificate = certs.Cast<X509Certificate2>().SingleOrDefault(c => c.HasPrivateKey && c.NotBefore.ToUniversalTime() <= DateTime.UtcNow && c.NotAfter.ToUniversalTime() > DateTime.UtcNow)
            ?? throw new InvalidDataException("현재 사용자 인증서 저장소에서 유효한 서버 인증서를 찾을 수 없습니다.");
        if (!result.Certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().Any(e => e.EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>().Any(o=>o.Value=="1.3.6.1.5.5.7.3.1")))
            throw new InvalidDataException("서버 인증 EKU가 필요합니다.");
        return result;
    }
}
