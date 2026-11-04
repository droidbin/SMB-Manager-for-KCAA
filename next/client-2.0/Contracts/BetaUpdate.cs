namespace SmbManager.Local;
public static class BetaUpdate
{
    public static bool ValidFileVersion(string? text)=>text is null || (System.Version.TryParse(text,out var value) && value.Build>=0 && value.Revision>=0);
    // Older catalogs without fileVersion cannot distinguish two beta builds with the same label.
    public static int? Compare(BetaRelease release,string installedFileVersion,string installedVersion)
    {
        if(release.FileVersion is not null && ValidFileVersion(release.FileVersion) && System.Version.TryParse(installedFileVersion,out var installed))
            return Math.Sign(System.Version.Parse(release.FileVersion).CompareTo(installed));
        if(release.Version==installedVersion)return null;
        var left=release.Version.Split('-')[0];var right=installedVersion.Split('-')[0];
        if(!System.Version.TryParse(left,out var candidate)||!System.Version.TryParse(right,out var current))return null;
        var result=candidate.CompareTo(current);return result==0?null:Math.Sign(result);
    }
}