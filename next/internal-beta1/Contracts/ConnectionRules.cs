namespace SmbManager.Local;

public static class ConnectionRules
{
    public static string AccountState(string state,string account,string expectedAccount) =>
        state=="Connected" && !string.Equals(account,expectedAccount,StringComparison.OrdinalIgnoreCase) ? "AccountMismatch" : state;

    public static bool CanOpen(ConnectionReport row,DepartmentPolicy? policy,DateTimeOffset now)
    {
        if(policy is null || policy.ExpiresAt<=now || row.ConnectionState!="Connected")return false;
        var profile=policy.Profiles.FirstOrDefault(p=>p.Id==row.ProfileId);
        return ProfileRules.Available(profile)
            && string.Equals(profile!.Drive,row.Drive,StringComparison.OrdinalIgnoreCase)
            && string.Equals(profile.Unc.TrimEnd('\\'),row.Unc.TrimEnd('\\'),StringComparison.OrdinalIgnoreCase)
            && string.Equals(policy.Accounts?.FirstOrDefault(a=>a.ProfileId==profile.Id)?.Account??policy.ExpectedAccount,row.Account,StringComparison.OrdinalIgnoreCase);
    }
}
