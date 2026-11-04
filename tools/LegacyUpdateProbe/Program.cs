using System.Reflection;
using System.Text.Json;

// Run in the designated guests only. Loads the immutable shipped Contracts.dll,
// never ClientAgent, never changes registration, channels or SMB mappings.
if (args.Length != 3) throw new ArgumentException("old Contracts.dll, metadata.json, output.json required");
var assembly = Assembly.LoadFrom(Path.GetFullPath(args[0]));
using var json = JsonDocument.Parse(File.ReadAllText(args[1]));
var r = json.RootElement;
var releaseType = assembly.GetType("SmbManager.Local.BetaRelease", true)!;
var release = Activator.CreateInstance(releaseType, r.GetProperty("channel").GetString(), r.GetProperty("version").GetString(),
    r.GetProperty("fileName").GetString(), r.GetProperty("size").GetInt64(), r.GetProperty("sha256").GetString(),
    r.GetProperty("notes").GetString(), r.GetProperty("fileVersion").GetString());
var compare = assembly.GetType("SmbManager.Local.BetaUpdate", true)!.GetMethod("Compare")!;
var before = (int?)compare.Invoke(null, [release, "2.0.145.0", "2.0.5"]);
var after = (int?)compare.Invoke(null, [release, "2.0.145.1", "2.0.5"]);
var newer = (int?)compare.Invoke(null, [release, "2.0.146.0", "2.0.5"]);
var passed = before == 1 && after == 0 && newer == -1;
File.WriteAllText(args[2], JsonSerializer.Serialize(new {
    passed, beforePatchComparison=before, installedPatchComparison=after, newerBuildComparison=newer,
    originalAssemblySha256=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(args[0]))),
    originalAssemblyVersion=assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
    candidateFileVersion=r.GetProperty("fileVersion").GetString(), candidateSha256=r.GetProperty("sha256").GetString()
}, new JsonSerializerOptions { WriteIndented=true }));
Environment.ExitCode = passed ? 0 : 1;
