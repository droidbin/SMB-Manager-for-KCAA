using SmbManager.Local;
namespace SmbManager.Local.Client;
public sealed partial class RealSmbEngine
{
    // The reconnect dialog obtains explicit permission for an inherited mapping.
    // Only a mapping matching a previous signed assignment on this exact UNC may change.
    private async Task<Receipt[]> SwitchPreviousMappings(CancellationToken ct)
    {
        await operationGate.WaitAsync(ct);
        try{return await Task.Run(()=>{
            var results=new List<Receipt>();
            lock(policyGate){
                if(!state.Managed || state.Quarantined || state.ManagementPaused || state.ConnectionsStopped || state.PolicyUntil<=DateTimeOffset.UtcNow)throw new InvalidOperationException("새 부서의 연결 권한을 확인할 수 없습니다.");
                foreach(var a in state.Approvals.Values.Where(a=>a.Authorized&&!a.Pending&&!a.NeedsCommand).ToArray()){
                    ct.ThrowIfCancellationRequested();var map=NativeMapping.Get(a.Profile.Drive);
                    if(map.Code is not (0 or 1201) || !NativeMapping.Same(map.Remote,a.Profile.Unc))continue;
                    var account=NativeMapping.Account(a.Profile.Drive);
                    if(string.Equals(account,a.ExpectedAccount,StringComparison.OrdinalIgnoreCase))continue;
                    if(!state.PreviousServerAccounts.GetValueOrDefault(WindowsCredential.Server(a.Profile.Unc),[]).Contains(account,StringComparer.OrdinalIgnoreCase) || !sessionCredentials.TryGetValue(a.Profile.Id,out var credential) || !string.Equals(credential.Account,a.ExpectedAccount,StringComparison.OrdinalIgnoreCase)){
                        results.Add(new("account-switch","Failed","이전 배정 계정 또는 새 계정·암호를 확인할 수 없어 기존 연결을 보존했습니다."));continue;
                    }
                    var code=NativeMapping.Disconnect(a.Profile.Drive,false,forgetRemembered:true);
                    if(code is not (0 or 2250)){results.Add(new("account-switch","Failed",$"기존 부서 연결 해제 미완료 ({code}): 열린 파일을 닫고 다시 시도하세요."));continue;}
                    a.Owned=false;a.AccountMismatch=false;a.Enabled=a.Reconnect;a.NextAttempt=DateTimeOffset.UtcNow;
                    a.Status="사용자 확인으로 이전 부서 매핑 해제 — 새 배정 계정 연결 대기";
                }
                Save();
            }
            return results.ToArray();
        },CancellationToken.None);}finally{operationGate.Release();}
    }
}