using SmbManager.Local;
sealed partial class Store {
 public IResult BulkConnections(BulkConnectionRequest request){lock(gate){
  if(request.Operation is not ("Reconnect" or "Disconnect") || request.DeviceIds is null || request.DeviceIds.Length is <1 or >500 || request.DeviceIds.Any(string.IsNullOrWhiteSpace) || request.DeviceIds.Distinct().Count()!=request.DeviceIds.Length)return Results.BadRequest();
  var results=new List<BulkConnectionResult>();
  foreach(var id in request.DeviceIds){
   if(!devices.TryGetValue(id,out var d)||d.Archived||!d.Approved||Now-d.LastSeen>=TimeSpan.FromSeconds(30)||!assignments.TryGetValue(id,out var a)){results.Add(new(id,false,"현재 접속·승인·배정 상태가 달라 제외됨"));continue;}
   if(request.Operation=="Reconnect"){
    if(d.Quarantined||d.Paused){results.Add(new(id,false,"보호 잠금 또는 사용자 일시중지 중"));continue;}
    if(commands.Count>=10000){results.Add(new(id,false,"서버 작업 한도 초과"));continue;}
    var response=RequestReconnect(id);
    results.Add(new(id,response is IStatusCodeHttpResult {StatusCode:200},"재연결 요청 접수 · 실제 결과는 작업 내역 확인"));
   }else{
    d.ConnectionsHeld=true;
    foreach(var w in commands.Values.Where(w=>w.Payload.DeviceId==id&&w.Payload.Operation is "ReconnectAssigned" or "ConnectApprovedSmb" or "ConnectProfile" && w.State is "Queued" or "Leased")){w.State="Rejected";w.Detail="관리자 연결 해제 요청으로 취소";}
    CommitPolicy(profiles,new(assignments){[id]=a with{Revision=policyRevision+1,ResumeStopped=false}});
    Persist();d.Signal.Writer.TryWrite(1);results.Add(new(id,true,"연결 해제 정책 접수 · PC 보고로 실제 해제 확인 필요"));
   }
   RecordAudit(request.Operation=="Reconnect"?"일괄 재연결":"일괄 연결 해제",id,d.Name);
  }
  Persist();return Results.Ok(results.ToArray());
 }}
}
