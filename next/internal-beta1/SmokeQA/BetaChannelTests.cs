using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using SmbManager.Local;
using SmbManager.Local.Client;

internal static class BetaChannelTests
{
    public static async Task<EnrollReply> Run(HttpClient admin, string url, string run, Action<bool,string> check)
    {
        using var anonymous = Wire.Http(url);
        using (var r = await anonymous.GetAsync("api/client/beta/releases/closed-beta")) check(r.StatusCode == HttpStatusCode.Unauthorized, "베타 배포 익명 조회 거절");
        var code = await Wire.Read<EnrollmentCode>(await admin.PostAsync("api/admin/enrollment-code", null));
        var enrolled = await Wire.Read<EnrollReply>(await anonymous.PostAsJsonAsync("api/enroll", new EnrollRequest(code.Code, "운영부 관리자", "Administrator", Guid.NewGuid().ToString(), HardwareIdentity.Capture()), Wire.Json));
        using var client = Wire.Http(url, device: enrolled.DeviceKey);
        async Task<BetaAccess> Access() => (await client.GetFromJsonAsync<BetaAccess>("api/client/beta/channels", Wire.Json))!;
        async Task<HttpStatusCode> Select(string channel) { using var r = await client.PostAsJsonAsync("api/client/beta/selection", new BetaSelection(channel), Wire.Json); return r.StatusCode; }
        check((await Access()).Allowed.Length == 0, "미승인 PC에는 베타 채널 권한 없음");
        check(await Select(BetaChannels.Open) == HttpStatusCode.Forbidden, "미승인 PC 오픈베타 선택 거절");
        using (var r = await client.GetAsync("api/client/beta/download/closed-beta")) check(r.StatusCode == HttpStatusCode.Forbidden, "미승인 PC 직접 다운로드 거절");
        using (var r = await admin.PostAsync($"api/admin/devices/{enrolled.DeviceId}/approve", null)) r.EnsureSuccessStatusCode();
        check((await Access()).Allowed.SequenceEqual([BetaChannels.Open]), "기존·미배정 PC는 기본 오픈베타만 허용");
        check(await Select(BetaChannels.Closed) == HttpStatusCode.Forbidden, "PC명·사용자명 관리자 사칭으로 클로즈베타 진입 불가");
        using (var r = await client.PostAsJsonAsync($"api/admin/devices/{enrolled.DeviceId}/beta-administrator", new BetaAdministrator(true), Wire.Json)) check(r.StatusCode == HttpStatusCode.Unauthorized, "장치 키로 관리자 자격 자체 부여 불가");
        using (var r = await client.PostAsJsonAsync("api/client/beta/selection", new { channel = BetaChannels.Closed, administrator = true, department = "운영부" }, Wire.Json)) check(r.StatusCode == HttpStatusCode.Forbidden, "클라이언트 JSON 부서·관리자 위조 거절");
        check(await Select("beta") == HttpStatusCode.BadRequest, "불명확한 구 베타 채널 이름 거절");
        using (var r = await client.GetAsync("api/client/beta/releases/open-beta")) check(r.StatusCode == HttpStatusCode.NotFound, "미게시 오픈베타는 배포 없음으로 응답");
        var assignment = await Wire.Read<DeviceAssignment>(await admin.PostAsJsonAsync($"api/admin/devices/{enrolled.DeviceId}/assignment", new SaveAssignment("운영부", @"LABDRIVE\operations", []), Wire.Json));
        check((await Access()).Allowed.Contains(BetaChannels.Closed), "서버 배정 운영부에 클로즈베타 허용");
        check(await Select(BetaChannels.Closed) == HttpStatusCode.OK, "운영부 클로즈베타 선택 성공");
        var bytes = System.Text.Encoding.UTF8.GetBytes("QA fixture only - not an installer");
        var releaseDir = Path.Combine(run, "beta-releases", BetaChannels.Closed); Directory.CreateDirectory(releaseDir);
        var release = new BetaRelease(BetaChannels.Closed, "2.0.0-beta.1-channel-qa", "SMB-Manager-channel-qa.msi", bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)));
        File.WriteAllBytes(Path.Combine(releaseDir, release.FileName), bytes); Wire.Save(Path.Combine(releaseDir, "release.json"), release);
        var metadata = await client.GetFromJsonAsync<BetaRelease>("api/client/beta/releases/closed-beta", Wire.Json);
        check(metadata == release, "클로즈베타 인증된 배포 정보 조회");
        check((await client.GetByteArrayAsync("api/client/beta/download/closed-beta")).SequenceEqual(bytes), "운영부 클로즈베타 인증 다운로드 성공");
        var identityDir = Path.Combine(run, "beta-download-client"); Directory.CreateDirectory(identityDir);
        SecretFile.Save(Path.Combine(identityDir, "identity.json.protected"), new Identity(url, enrolled.DeviceId, enrolled.DeviceKey, enrolled.PublicKey, HardwareIdentity.Capture()));
        using var agent = await ClientAgent.Create(new(url, "", "channel-qa"), identityDir);
        var savedFile = Path.Combine(identityDir, "download.msi");
        await agent.DownloadBeta(release, savedFile, CancellationToken.None);
        check(File.ReadAllBytes(savedFile).SequenceEqual(bytes), "클라이언트 저장 전 크기·SHA256 검증 성공");
        var altered = false;
        try { await agent.DownloadBeta(release with { Sha256 = new string('0', 64) }, Path.Combine(identityDir, "invalid.msi"), CancellationToken.None); } catch (InvalidDataException) { altered = true; }
        check(altered && !File.Exists(Path.Combine(identityDir, "invalid.msi")) && Directory.GetFiles(identityDir, "*.partial").Length == 0, "클라이언트 해시 불일치 파일과 임시 파일 제거");
        File.WriteAllBytes(Path.Combine(releaseDir, release.FileName), new byte[bytes.Length]);
        using (var r = await client.GetAsync("api/client/beta/download/closed-beta")) check(r.StatusCode == HttpStatusCode.Conflict, "서버 배포 파일 변조 다운로드 거절");
        File.WriteAllBytes(Path.Combine(releaseDir, release.FileName), bytes);
        Wire.Save(Path.Combine(releaseDir, "release.json"), release with { FileName = "../secret.msi" });
        using (var r = await client.GetAsync("api/client/beta/releases/closed-beta")) check(r.StatusCode == HttpStatusCode.NotFound, "배포 메타데이터 경로 탈출 거절");
        Wire.Save(Path.Combine(releaseDir, "release.json"), release);
        assignment = await Wire.Read<DeviceAssignment>(await admin.PostAsJsonAsync($"api/admin/devices/{enrolled.DeviceId}/assignment", new SaveAssignment("사업부 시험", @"LABDRIVE\sales-test", [], ExpectedRevision: assignment.Revision), Wire.Json));
        check((await Access()).Selected == BetaChannels.Open, "운영부에서 다른 부서로 변경 시 선택 채널 오픈으로 제한");
        using (var r = await client.GetAsync("api/client/beta/releases/closed-beta")) check(r.StatusCode == HttpStatusCode.Forbidden, "부서 변경 후 캐시된 클로즈베타 조회 차단");
        using (var r = await client.GetAsync("api/client/beta/download/closed-beta")) check(r.StatusCode == HttpStatusCode.Forbidden, "부서 변경 후 직접 다운로드 차단");
        using (var r = await admin.PostAsJsonAsync($"api/admin/devices/{enrolled.DeviceId}/beta-administrator", new BetaAdministrator(true), Wire.Json)) r.EnsureSuccessStatusCode();
        check(await Select(BetaChannels.Closed) == HttpStatusCode.OK, "서버가 자격 부여한 관리자 클로즈베타 허용");
        using (var r = await admin.PostAsJsonAsync($"api/admin/devices/{enrolled.DeviceId}/beta-administrator", new BetaAdministrator(false), Wire.Json)) r.EnsureSuccessStatusCode();
        check(await Select(BetaChannels.Closed) == HttpStatusCode.Forbidden, "관리자 자격 회수 즉시 클로즈베타 선택 차단");
        using (var r = await client.GetAsync("api/client/beta/download/closed-beta")) check(r.StatusCode == HttpStatusCode.Forbidden, "관리자 자격 회수 즉시 다운로드 차단");
        using (var r = await admin.PostAsJsonAsync($"api/admin/devices/{enrolled.DeviceId}/beta-administrator", new BetaAdministrator(true), Wire.Json)) r.EnsureSuccessStatusCode();
        await Select(BetaChannels.Closed);
        return enrolled;
    }
}
