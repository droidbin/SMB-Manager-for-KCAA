using SmbManager.Local;
using SmbManager.Local.Client;

// Pure rules only. Does not load ClientEngine/ClientAgent, install, reset SMB,
// touch user registration, or execute employee-client runtime QA on Admin.
var count = 0;
void Check(bool result, string name) { if (!result) throw new Exception(name); count++; }
const string sid = "S-1-5-21-123-456-789-1001";
Check(InstallSessionRules.CanComplete(true, 1, sid, true, 1), "UAC-disabled administrator desktop accepted");
Check(InstallSessionRules.CanComplete(true, 1, sid, true, 3), "UAC limited desktop accepted");
Check(!InstallSessionRules.CanComplete(true, 1, sid, true, 2), "UAC elevated linked session rejected");
Check(!InstallSessionRules.CanComplete(false, 1, sid, true, 1), "Noninteractive session rejected");
Check(!InstallSessionRules.CanComplete(true, 0, sid, true, 1), "Session zero rejected");
foreach (var service in new[] { "", "S-1-5-18", "S-1-5-19", "S-1-5-20" })
    Check(!InstallSessionRules.CanComplete(true, 1, service, true, 1), "Service identity rejected");
Check(!InstallSessionRules.CanComplete(true, 1, sid, false, 1), "Token query failure fails closed");
Check(!InstallSessionRules.CanComplete(true, 1, sid, true, 99), "Unknown token type fails closed");
Check(BetaChannels.Label(BetaChannels.Open) == "베타", "Legacy open catalog never labeled stable");
Check(BetaChannels.Label(BetaChannels.Closed) == "베타", "Legacy closed catalog never labeled stable");
Check(BetaChannels.Label(BetaChannels.Stable) == "정식", "Only explicit stable labeled stable");
Check(BetaChannels.DisplayChoices(new("beta", ["beta", "stable", "beta"], "", false)).SequenceEqual(["stable", "beta"]), "Current two choices ordered and deduplicated");
Check(BetaChannels.DisplayChoices(new("closed-beta", ["open-beta", "closed-beta"], "", true)).SequenceEqual(["closed-beta"]), "Saved restricted selection retained");
Check(BetaChannels.DisplayChoices(new("closed-beta", ["open-beta"], "", false)).SequenceEqual(["open-beta"]), "Revoked selection cannot broaden access");
Check(BetaChannels.DisplayChoices(new("open-beta", [], "", false)).Length == 0, "Unapproved device gets no choices");
Console.WriteLine($"PASS: {count} pure regression checks; no client runtime or SMB actions.");

var patch = new BetaRelease("open-beta", "2.0.5", "SMB-Manager-Client-2.0.5-Patch-r4-x64.msi", 1, new string('A', 64), "", "2.0.145.1");
Check(BetaUpdate.Compare(patch, "2.0.145.0", "2.0.5") == 1, "Existing 2.0.5 stable discovers file revision .1");
Check(BetaUpdate.Compare(patch, "2.0.145.1", "2.0.5") == 0, "Installed patch is not repeatedly offered");
Check(BetaUpdate.Compare(patch, "2.0.146.0", "2.0.5") == -1, "Higher internal build never downgraded");
Check(patch.Version == "2.0.5", "Marketing version unchanged");
Console.WriteLine($"PASS: {count} total pure regression checks.");
