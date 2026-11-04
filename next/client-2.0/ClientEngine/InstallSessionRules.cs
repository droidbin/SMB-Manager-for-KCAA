namespace SmbManager.Local.Client;

// Pure token classification; never queries or modifies the machine or SMB state.
public static class InstallSessionRules
{
    public static bool CanComplete(bool userInteractive, int sessionId, string? sid,
        bool tokenRead, int elevationType) => userInteractive && sessionId != 0
        && sid is not (null or "" or "S-1-5-18" or "S-1-5-19" or "S-1-5-20")
        && tokenRead && elevationType is 1 or 3;
}
