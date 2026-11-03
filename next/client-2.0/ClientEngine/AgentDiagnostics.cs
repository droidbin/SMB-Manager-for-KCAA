using System.Net.Http.Json;
using SmbManager.Local;

namespace SmbManager.Local.Client;
public sealed partial class ClientAgent
{
    private static DiagnosticPhase ParseDiagnosticPhase(string phase)=>Enum.TryParse<DiagnosticPhase>(phase,out var value)&&Enum.IsDefined(value)?value:DiagnosticPhase.Unknown;
    private static DiagnosticFailure Failure(Exception e)=>e switch {
        HttpRequestException {StatusCode:System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden}=>DiagnosticFailure.Authentication,
        HttpRequestException or OperationCanceledException=>DiagnosticFailure.Network,
        IOException or UnauthorizedAccessException=>DiagnosticFailure.Storage,
        InvalidOperationException=>DiagnosticFailure.PolicyValidation, _=>DiagnosticFailure.Unexpected };
    private void RecordDiagnostic(DiagnosticCode code,DiagnosticFailure failure=DiagnosticFailure.None,DiagnosticPhase? phase=null)=>
        diagnostics.Record(code,phase??ParseDiagnosticPhase(Current?.Phase??"Starting"),failure,PolicyUtcNow);
    private string diagnosticConnections="";
    private void RecordConnectionsDiagnostic(){
        var rows=real.Connections();var connected=rows.Count(r=>r.ConnectionState=="Connected");var other=rows.Length-connected;
        var failure=rows.Any(r=>r.ConnectionState is "CredentialsMissing" or "AuthenticationRequired")?DiagnosticFailure.Authentication
            :rows.Any(r=>r.ConnectionState=="Conflict")?DiagnosticFailure.Configuration
            :rows.Any(r=>r.ConnectionState is "Unavailable" or "Disconnected")?DiagnosticFailure.Network:DiagnosticFailure.None;
        var signature=$"{connected}:{other}:{failure}";if(signature==diagnosticConnections)return;diagnosticConnections=signature;
        diagnostics.Record(DiagnosticCode.ConnectionsChanged,ParseDiagnosticPhase(Current.Phase),failure,PolicyUtcNow,connected,other);
    }
    private async Task DiagnosticLoop(CancellationToken ct){
        while(!ct.IsCancellationRequested){
            var delay=TimeSpan.FromSeconds(30);
            try{
                if(managementApproved&&diagnostics.Enabled){
                    var pending=diagnostics.Pending();
                    if(pending.Length>0){
                        using var response=await http.PostAsJsonAsync("api/client/diagnostics",new ClientDiagnosticBatch(pending),Wire.Json,ct);
                        if(response.IsSuccessStatusCode){var ack=await response.Content.ReadFromJsonAsync<ClientDiagnosticAck>(Wire.Json,ct);if(ack?.Accepted is { } ids)diagnostics.Acknowledge(ids.Intersect(pending.Select(e=>e.EventId)));}
                        else delay=TimeSpan.FromMinutes(response.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.MethodNotAllowed?30:2);
                    }
                }
            }catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
            catch{delay=TimeSpan.FromMinutes(2);} // Collection cannot change connection/UI state or log raw transport errors.
            try{await Task.Delay(delay,ct);}catch(OperationCanceledException){break;}
        }
    }
}
