using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SmbManager.Local;
using SmbManager.Local.Client;

internal static class HardwareTests
{
    public static async Task Run(HttpClient admin, string url, string run, Action<bool,string> check)
    {
        var actual = HardwareIdentity.Capture();
        check(HardwareIdentity.Valid(actual), "Hardware inventory has valid MAC addresses");
        check(HardwareIdentity.Same(actual, actual with { IpAddresses = ["192.0.2.1"] }), "IP address change does not change device identity");
        check(HardwareIdentity.Same(actual, actual with { MacAddresses = actual.MacAddresses.Reverse().ToArray() }), "Adapter ordering does not change device identity");
        check(HardwareIdentity.Same(actual, actual with { MacAddresses = actual.MacAddresses.Take(15).Append("02AABBCCDDEE").ToArray() }), "Adding an adapter preserves matching hardware");
        var foreign = actual with { SystemUuid = Guid.NewGuid().ToString(), MacAddresses = ["02AABBCCDDEE"] };
        check(!HardwareIdentity.Same(actual, foreign), "Different ghost target hardware does not match");
        check(!HardwareIdentity.Same(actual, actual with { SystemUuid = Guid.NewGuid().ToString() }), "Shared MAC with a different UUID does not match");
        check(!HardwareIdentity.Same(actual, actual with { MacAddresses = ["000000000000"] }), "Invalid MAC is rejected");
        check(!HardwareIdentity.Same(null, actual), "Legacy unbound identity requires fresh enrollment");
        using var anonymous = Wire.Http(url);
        async Task<EnrollmentCode> Code() => await Wire.Read<EnrollmentCode>(await admin.PostAsync("api/admin/enrollment-code", null));
        async Task<EnrollReply> Enroll(HardwareIdentity hardware) {var c=await Code();return await Wire.Read<EnrollReply>(await anonymous.PostAsJsonAsync("api/enroll",new EnrollRequest(c.Code,"GHOST-SAME-PC-NAME","qa",Guid.NewGuid().ToString(),hardware),Wire.Json));}
        var original = await Enroll(actual);var clone = await Enroll(foreign);
        check(original.DeviceId != clone.DeviceId, "Identical PC names on different hardware get separate device IDs");
        using(var r=await admin.PostAsync($"api/admin/devices/{original.DeviceId}/approve",null))r.EnsureSuccessStatusCode();
        using(var r=await admin.PostAsJsonAsync($"api/admin/devices/{original.DeviceId}/assignment",new SaveAssignment("운영부",@"LABDRIVE\operations",[]),Wire.Json))r.EnsureSuccessStatusCode();
        using var copiedKey = Wire.Http(url,device:original.DeviceKey);
        copiedKey.DefaultRequestHeaders.Remove("X-Device-Hardware");
        copiedKey.DefaultRequestHeaders.Add("X-Device-Hardware",Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(foreign,Wire.Json)));
        using(var r=await copiedKey.GetAsync("api/client/policy"))check(r.StatusCode==HttpStatusCode.Unauthorized,"Copied approved key on different hardware cannot fetch policy");
        using(var r=await copiedKey.GetAsync("api/client/beta/channels"))check(r.StatusCode==HttpStatusCode.Unauthorized,"Copied key on different hardware cannot access beta permissions");
        copiedKey.DefaultRequestHeaders.Remove("X-Device-Hardware");
        using(var r=await copiedKey.GetAsync("api/client/profiles"))check(r.StatusCode==HttpStatusCode.Unauthorized,"Missing hardware header fails closed");
        copiedKey.DefaultRequestHeaders.Add("X-Device-Hardware","invalid-base64");
        using(var r=await copiedKey.GetAsync("api/client/profiles"))check(r.StatusCode==HttpStatusCode.Unauthorized,"Malformed hardware header fails closed");
        var list=await admin.GetFromJsonAsync<DeviceView[]>("api/admin/devices",Wire.Json)??[];
        check(!list.Single(d=>d.Id==clone.DeviceId).Approved && list.Single(d=>d.Id==clone.DeviceId).Assignment is null,"Ghost target does not inherit approval or department");
        check(list.Single(d=>d.Id==original.DeviceId).Approved,"Rejected foreign claim does not revoke original PC approval");
        var dir=Path.Combine(run,"ghost-clone-binding");Directory.CreateDirectory(dir);
        var identityPath=Path.Combine(dir,"identity.json.protected");
        SecretFile.Save(identityPath,new Identity(url,original.DeviceId,original.DeviceKey,original.PublicKey,foreign));
        var saved=File.ReadAllBytes(identityPath);bool denied=false;
        try{using var ignored=await ClientAgent.Create(new(url,"expired-code","GHOST-SAME-PC-NAME"),dir);}catch(InvalidOperationException){denied=true;}
        check(denied && saved.SequenceEqual(File.ReadAllBytes(identityPath)),"Invalid reenrollment code preserves original saved identity");
        var fresh=await Code();var rebound=await ClientAgent.Create(new(url,fresh.Code,"GHOST-SAME-PC-NAME"),dir);
        check(rebound.Identity.DeviceId!=original.DeviceId && HardwareIdentity.Same(rebound.Identity.Hardware,actual),"Copied identity creates a fresh hardware-bound device after valid enrollment");
        rebound.Dispose();
        var restored=await Code();using var same=await ClientAgent.Create(new(url,restored.Code,"GHOST-SAME-PC-NAME"),dir);
        check(same.Identity.DeviceId==rebound.Identity.DeviceId,"Same hardware restart retains the existing device ID");
        list=await admin.GetFromJsonAsync<DeviceView[]>("api/admin/devices",Wire.Json)??[];
        check(!list.Single(d=>d.Id==rebound.Identity.DeviceId).Approved && list.Single(d=>d.Id==rebound.Identity.DeviceId).Assignment is null,"Reenrolled device stays unapproved and unassigned");
    }
}
