using System.Reflection;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using SmbManager.Local;
using SmbManager.Local.Client;

internal static class ReviewRegression
{
    public static async Task Run(string[] args)
    {
        var root=Path.GetFullPath(Wire.Arg(args,"--root"));
        var run=Path.Combine(root,".local","review8",DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(run);
        var passed=new List<string>();var failed=new List<string>();
        void Check(bool ok,string name){(ok?passed:failed).Add(name);Console.WriteLine((ok?"PASS ":"FAIL ")+name);}
        async Task Case(string name,Func<Task> test){try{await test();}catch(Exception e){failed.Add(name+": "+e.GetType().Name+" "+e.Message);}}
        var drive=Enumerable.Range('D',23).Select(c=>((char)c)+":").First(d=>NativeMapping.Get(d).Code==2250&&!NativeMapping.DeviceAssigned(d));
        var p=new Profile("review-share","QA","Real",1,@"\\192.0.2.250\review",drive,"운영부");
        DepartmentPolicy Policy(int revision=1,int assignment=1,bool reconnect=true,string account=@"QA\old")=>new("qa","qa",revision,"운영부",account,[p],reconnect,false,false,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow.AddMinutes(5),assignment);
        RealSmbEngine.State State(RealSmbEngine engine)=>(RealSmbEngine.State)typeof(RealSmbEngine).GetField("state",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(engine)!;
        await Case("application startup restoration",async()=>{
            var directory=Path.Combine(run,"startup-restore");
            using(var first=new RealSmbEngine(directory)){first.EnableManagedMode();await first.ApplyPolicy(Policy(reconnect:false),default);var saved=State(first);saved.ConnectionsStopped=true;saved.Approvals[p.Id].Enabled=false;saved.Approvals[p.Id].Failures=4;saved.Approvals[p.Id].NextAttempt=DateTimeOffset.UtcNow.AddHours(1);Wire.Save(Path.Combine(directory,"smb-policy.json"),saved);}
            using var second=new RealSmbEngine(directory);second.EnableManagedMode();await second.ApplyPolicy(Policy(reconnect:false),default);var restored=State(second);var approval=restored.Approvals[p.Id];
            Check(approval.Enabled&&!restored.ConnectionsStopped,"Fresh app start restores assigned connection even when continuous reconnect is disabled");
            Check(approval.Failures==0&&approval.NextAttempt<=DateTimeOffset.UtcNow,"Startup restoration does not wait for stale network backoff");
            approval.Enabled=false;await second.ApplyPolicy(Policy(reconnect:false),default);Check(!approval.Enabled,"Startup restoration is performed once per app session");
        });
        foreach(var guard in new[]{"authentication","security-command","quarantine","pause"})await Case("startup preserves "+guard,async()=>{
            var directory=Path.Combine(run,"startup-"+guard);
            using(var first=new RealSmbEngine(directory)){first.EnableManagedMode();await first.ApplyPolicy(Policy(),default);var saved=State(first);saved.Approvals[p.Id].Enabled=false;saved.Approvals[p.Id].AuthRejected=guard=="authentication";saved.Approvals[p.Id].NeedsCommand=guard=="security-command";saved.Quarantined=guard=="quarantine";saved.ManagementPaused=guard=="pause";Wire.Save(Path.Combine(directory,"smb-policy.json"),saved);}
            using var second=new RealSmbEngine(directory);second.EnableManagedMode();await second.ApplyPolicy(Policy(),default);Check(!State(second).Approvals[p.Id].Enabled,"App restart preserves "+guard+" guard");
        });
        await Case("startup held policy",async()=>{
            using var engine=new RealSmbEngine(Path.Combine(run,"startup-held"));engine.EnableManagedMode();await engine.ApplyPolicy(Policy() with{ConnectionsHeld=true},default);
            Check(State(engine).Approvals.Values.All(a=>!a.Enabled&&!a.Authorized),"Administrator hold cannot be bypassed by app startup even with populated profiles");
            Check((await engine.Connect("held-startup",p,default)).State=="Failed","Held policy refuses explicit connect before native access");
            var ordinary=System.Text.Json.JsonSerializer.Serialize(Policy(),Wire.Json);Check(!ordinary.Contains("connectionsHeld"),"Ordinary policy signature format remains compatible with existing clients");
        });
        await Case("authentication rejection guard",async()=>{
            using var engine=new RealSmbEngine(Path.Combine(run,"auth-rejection"));engine.EnableManagedMode();
            var free=Enumerable.Range('D',23).Select(c=>((char)c)+":").Where(d=>NativeMapping.Get(d).Code==2250&&!NativeMapping.DeviceAssigned(d)).Take(3).ToArray();
            var q=p with{Id="same-host",Drive=free[1],Unc=@"\\192.0.2.250\second"};var r=p with{Id="other-host",Drive=free[2],Unc=@"\\192.0.2.249\other"};
            var policy=Policy() with{Profiles=[p,q,r]};await engine.ApplyPolicy(policy,default);
            var old=new ShareCredential(@"QA\old","test-only");var values=new Dictionary<string,ShareCredential>{{p.Id,old},{q.Id,old},{r.Id,old}};engine.SetCredentials(values);
            var state=State(engine);var a=state.Approvals[p.Id];var b=state.Approvals[q.Id];var other=state.Approvals[r.Id];
            var reject=typeof(RealSmbEngine).GetMethod("RejectAuthenticationForServer",BindingFlags.NonPublic|BindingFlags.Instance)!;
            reject.Invoke(engine,[a,53,old]);Check(!a.AuthRejected&&!b.AuthRejected,"Network outage does not require password reentry");
            reject.Invoke(engine,[a,1326,old]);Check(a.AuthRejected&&b.AuthRejected&&!a.Enabled&&!b.Enabled,"One rejected credential stops automatic authentication for the entire server");
            Check(!other.AuthRejected&&other.Enabled,"Authentication rejection does not stop other servers");
            var receipt=await engine.Connect("blocked",q,default,automatic:true);Check(receipt.Detail.StartsWith("AUTHENTICATION_REQUIRED"),"Already queued shares do not perform another native authentication");
            await engine.RefreshConnections(default);Check(engine.Connections().Single(c=>c.ProfileId==p.Id).ConnectionState=="AuthenticationRequired","Client reports credential-required state instead of endless reconnect wait");
            engine.SetCredentials(values);Check(a.AuthRejected&&b.AuthRejected,"Repeated fetch of identical credentials cannot restart rejected authentication");
            await engine.ApplyPolicy(policy with{Revision=2},default);Check(a.AuthRejected&&!a.Enabled,"Unrelated signed policy revision preserves authentication stop");
            var changed=old with{Password="replacement-test-only"};var updated=new Dictionary<string,ShareCredential>{{p.Id,changed},{q.Id,changed},{r.Id,old}};
            engine.SetCredentials(updated);Check(!a.AuthRejected&&!b.AuthRejected&&a.Enabled&&b.Enabled,"Changed server credentials resume eligible profiles");
            Check(a.Failures==0&&a.NextAttempt<=DateTimeOffset.UtcNow,"Changed credentials reset stale retry backoff");
            reject.Invoke(engine,[a,1326,old]);Check(!a.AuthRejected&&!b.AuthRejected,"Late rejection of an old credential cannot block its replacement");
            reject.Invoke(engine,[a,86,changed]);Check(a.AuthRejected&&b.AuthRejected,"Invalid-password error also stops server authentication");
            var finish=typeof(RealSmbEngine).GetMethod("Finish",BindingFlags.NonPublic|BindingFlags.Instance)!;finish.Invoke(engine,[a,new Receipt("qa","Failed","Windows SMB 오류 86"),true]);Check(!a.Enabled,"Failed receipt cannot reactivate authentication-rejected profiles");
            await engine.ApplyPolicy(policy with{Revision=3,AssignmentRevision=2,ResumeStopped=true},default);Check(!a.AuthRejected&&a.Enabled,"Explicit administrator reconnect authorizes one fresh attempt");
            reject.Invoke(engine,[a,1909,changed]);Check(a.AuthRejected&&b.AuthRejected,"Account lockout error stops all same-server retries");
            state.Quarantined=true;a.NeedsCommand=true;engine.SetCredentials(new(){{p.Id,changed with{Password="third-test-only"}},{q.Id,changed},{r.Id,old}});
            Check(!a.AuthRejected&&!a.Enabled&&a.NeedsCommand,"Credential correction cannot bypass quarantine or explicit security-resume requirement");
            Check(NativeMapping.Get(p.Drive).Code==2250&&!NativeMapping.DeviceAssigned(p.Drive),"Authentication guard regression does not create or delete real SMB mappings");
        });
        await Case("credential transition",async()=>{
            using var engine=new RealSmbEngine(Path.Combine(run,"credential"));engine.EnableManagedMode();
            await engine.ApplyPolicy(Policy(),default);engine.SetCredentials(new(){[p.Id]=new(@"QA\new","test-only")});
            var receipt=await engine.Connect("qa",p,default);
            Check(receipt.Detail.StartsWith("CREDENTIAL_POLICY_CHANGED"),"Credential mismatch returns before native connection");
            Check(!State(engine).Approvals[p.Id].Pending,"Credential mismatch does not leave Pending latched");
            await engine.ApplyPolicy(Policy(2,2,account:@"QA\new"),default);
            Check(State(engine).Approvals[p.Id] is {Authorized:true,Pending:false,ExpectedAccount:@"QA\new"},"Fresh credential policy can replace old approval");
        });
        await Case("active native attempt",async()=>{
            using var engine=new RealSmbEngine(Path.Combine(run,"active-native"));engine.EnableManagedMode();await engine.ApplyPolicy(Policy(),default);
            var state=State(engine);var a=state.Approvals[p.Id];a.Pending=true;var epoch=state.Epoch;
            var gate=(SemaphoreSlim)typeof(RealSmbEngine).GetField("operationGate",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(engine)!;
            await gate.WaitAsync();try{
                var apply=engine.ApplyPolicy(Policy(),default);await apply.WaitAsync(TimeSpan.FromSeconds(3));
                Check(a.Pending&&a.Authorized&&state.Epoch==epoch,"Same-policy refresh never revokes an active native attempt or changes its epoch");
            }finally{gate.Release();}
        });
        await Case("interrupted absent mapping",async()=>{
            using var engine=new RealSmbEngine(Path.Combine(run,"interrupted-absent"));engine.EnableManagedMode();
            await engine.ApplyPolicy(Policy(),default);var a=State(engine).Approvals[p.Id];
            a.Pending=true;a.Authorized=true;a.Failures=5;a.NextAttempt=DateTimeOffset.UtcNow.AddHours(1);
            await engine.ApplyPolicy(Policy(),default);
            Check(a is {Pending:false,Authorized:true,Enabled:true,Owned:false,Failures:0},"Unchanged signed policy recovers an absent interrupted mapping without adopting ownership");
            Check(a.NextAttempt<=DateTimeOffset.UtcNow,"Recovered absent mapping can retry immediately");
            Check(NativeMapping.Get(drive).Code==2250&&!NativeMapping.DeviceAssigned(drive),"Recovery does not create or disconnect any mapping");
            await engine.RefreshConnections(default);
            Check(engine.Connections().Single(c=>c.ProfileId==p.Id).ConnectionState=="Disconnected","Recovered assigned folder remains visible as disconnected");
        });
        foreach(var guard in new[]{"stopped","quarantined","needs-command"})await Case("interrupted "+guard,async()=>{
            using var engine=new RealSmbEngine(Path.Combine(run,"interrupted-"+guard));engine.EnableManagedMode();await engine.ApplyPolicy(Policy(),default);
            var state=State(engine);var a=state.Approvals[p.Id];a.Pending=true;a.Authorized=false;
            state.ConnectionsStopped=guard=="stopped";state.Quarantined=guard=="quarantined";a.NeedsCommand=guard=="needs-command";
            await engine.ApplyPolicy(Policy(),default);
            Check(!a.Pending&&!a.Enabled,"Interrupted recovery respects "+guard+" reconnect restriction");
        });
        await Case("interrupted occupied drive",async()=>{
            var occupied=p with{Drive=Path.GetPathRoot(Environment.SystemDirectory)![..2]};
            using var engine=new RealSmbEngine(Path.Combine(run,"interrupted-occupied"));engine.EnableManagedMode();
            var policy=Policy() with{Profiles=[occupied]};await engine.ApplyPolicy(policy,default);
            var a=State(engine).Approvals[p.Id];a.Pending=true;
            await engine.ApplyPolicy(policy,default);
            Check(a.Pending&&!a.Authorized&&!a.Enabled&&!a.Owned,"Occupied local drive never clears ambiguous ownership or receives authorization");
            await engine.RefreshConnections(default);
            Check(engine.Connections().Single(c=>c.ProfileId==p.Id).ConnectionState=="ReconciliationRequired","Blocked assigned folder stays visible for client and administrator");
            await engine.ApplyPolicy(policy with{Revision=2,Profiles=[]},default);await engine.RefreshConnections(default);
            Check(a.Pending&&!engine.Connections().Any(c=>c.ProfileId==p.Id),"Revoked ambiguous folder is preserved internally and excluded from assigned rows");
        });
        await Case("interrupted revoked absent mapping",async()=>{
            using var engine=new RealSmbEngine(Path.Combine(run,"interrupted-revoked"));engine.EnableManagedMode();await engine.ApplyPolicy(Policy(),default);
            State(engine).Approvals[p.Id].Pending=true;
            await engine.ApplyPolicy(Policy(2) with{Profiles=[]},default);
            Check(!State(engine).Approvals.ContainsKey(p.Id),"Revoked absent interrupted mapping is removed without being reauthorized");
        });
        await Case("reconnect preference",async()=>{
            using var engine=new RealSmbEngine(Path.Combine(run,"reconnect"));engine.EnableManagedMode();
            await engine.ApplyPolicy(Policy(reconnect:false),default);
            Check(State(engine).Approvals[p.Id].Enabled,"Initial assignment may connect once without auto reconnect");
            State(engine).Approvals[p.Id].Enabled=false;
            await engine.ApplyPolicy(Policy(2,reconnect:false),default);
            Check(!State(engine).Approvals[p.Id].Enabled,"Unrelated global policy change respects disabled auto reconnect");
            await engine.ApplyPolicy(Policy(3,2,reconnect:false),default);
            Check(State(engine).Approvals[p.Id].Enabled,"Explicit assignment change may connect once");
        });
        foreach(var redirected in new[]{"Desktop","StartMenu","Startup"})await Case("redirected "+redirected,()=>{
            var dir=Path.Combine(run,"redirected-"+redirected);Directory.CreateDirectory(dir);
            string Location(string name)=>redirected==name?@"\\invalid-review-server\users\"+name:Path.Combine(dir,name);
            var result=LegacyMigration.Run(new(Path.Combine(dir,"Local"),Path.Combine(dir,"Roaming"),Location("Desktop"),Location("StartMenu"),Location("Startup")));
            Check(result.Completed,"Redirected "+redirected+" does not block local migration");
            Check(result.Retained.Any(x=>x.StartsWith(@"\\invalid-review-server")),"Redirected "+redirected+" is retained without network access");return Task.CompletedTask;
        });
        await Case("preflight and persistence",async()=>{
            var dir=Path.Combine(run,"preflight");using var engine=new RealSmbEngine(dir);engine.EnableManagedMode();await engine.ApplyPolicy(Policy(),default);
            var current=State(engine);var approval=current.Approvals[p.Id];
            var duplicate=false;try{using var second=new RealSmbEngine(dir);}catch(IOException){duplicate=true;}Check(duplicate,"Duplicate engine cannot modify same state directory");
            using(var cancel=new CancellationTokenSource()){cancel.Cancel();var cancelled=false;try{await engine.Connect("cancel",p,cancel.Token);}catch(OperationCanceledException){cancelled=true;}Check(cancelled&&!approval.Pending,"Cancelled operation does not latch Pending");}
            foreach(var reason in new[]{"paused","stopped","quarantined","expired","revoked"}){
                current.ManagementPaused=reason=="paused";current.ConnectionsStopped=reason=="stopped";current.Quarantined=reason=="quarantined";current.PolicyUntil=reason=="expired"?DateTimeOffset.MinValue:DateTimeOffset.UtcNow.AddMinutes(5);approval.Authorized=reason!="revoked";
                var receipt=await engine.Connect("denied",p,default);Check(receipt.State=="Failed"&&!approval.Pending,"Preflight denies native attempt when "+reason);
            }
            current.ManagementPaused=false;current.ConnectionsStopped=false;current.Quarantined=false;current.PolicyUntil=DateTimeOffset.UtcNow.AddMinutes(5);approval.Authorized=true;
            approval.Pending=true;var unknown=await engine.Connect("ambiguous",p,default);Check(unknown.Detail.StartsWith("UNKNOWN")&&approval.Pending,"Genuinely ambiguous native attempt stays blocked for reconciliation");approval.Pending=false;
            var incident=engine.LatchQuarantine("QA");var wrong=false;try{engine.Release("wrong-incident");}catch(InvalidOperationException){wrong=true;}Check(wrong&&engine.Quarantined,"Wrong incident cannot release quarantine");
            engine.Release(incident);Check(!approval.Enabled&&approval.NeedsCommand,"Quarantine release requires explicit reconnect authorization");
            await engine.ApplyPolicy(Policy(2,1),default);Check(!approval.Enabled&&approval.NeedsCommand,"Unrelated policy cannot bypass explicit-resume requirement");
        });
        await Case("one-time reconnect intent",async()=>{
            using var engine=new RealSmbEngine(Path.Combine(run,"once"));engine.EnableManagedMode();await engine.ApplyPolicy(Policy(reconnect:false),default);
            await engine.ApplyPolicy(Policy(2,reconnect:false),default);Check(State(engine).Approvals[p.Id].Enabled,"Unrelated policy preserves initial not-yet-attempted one-time connect");
            State(engine).Approvals[p.Id].Enabled=false;await engine.ApplyPolicy(Policy(3,reconnect:true),default);Check(State(engine).Approvals[p.Id].Enabled,"Enabling auto reconnect reactivates eligible assignment");
        });
        foreach(var reason in new[]{"expired","revoked","paused","stopped","quarantined","account-mismatch"})await Case("late success "+reason,async()=>{
            using var engine=new RealSmbEngine(Path.Combine(run,"late-"+reason));engine.EnableManagedMode();await engine.ApplyPolicy(Policy(),default);
            var state=State(engine);var approval=state.Approvals[p.Id];
            switch(reason){case "expired":state.PolicyUntil=DateTimeOffset.MinValue;break;case "revoked":approval.Authorized=false;break;case "paused":state.ManagementPaused=true;break;case "stopped":state.ConnectionsStopped=true;break;case "quarantined":state.Quarantined=true;break;case "account-mismatch":approval.AccountMismatch=true;break;}
            var finish=typeof(RealSmbEngine).GetMethod("Finish",BindingFlags.NonPublic|BindingFlags.Instance)!;
            var receipt=(Receipt)finish.Invoke(engine,[approval,new Receipt("late","Succeeded","ALREADY_CONNECTED"),false])!;
            Check(receipt.State=="Failed","Connection completion cannot report success after "+reason);
            Check(!approval.Enabled,"No automatic retry after "+reason);
        });
        await Case("malformed optional shortcut",()=>{
            var dir=Path.Combine(run,"malformed-link");var desktop=Path.Combine(dir,"Desktop");Directory.CreateDirectory(desktop);var link=Path.Combine(desktop,"SMB Manager.lnk");File.WriteAllText(link,"malformed QA shortcut");
            var result=LegacyMigration.Run(new(Path.Combine(dir,"Local"),Path.Combine(dir,"Roaming"),desktop,Path.Combine(dir,"StartMenu"),Path.Combine(dir,"Startup")));
            Check(result.Completed,"Malformed optional shortcut does not block local migration");
            Check(File.Exists(link),"Malformed shortcut is preserved for manual review");return Task.CompletedTask;
        });
        await Case("folder open policy guards",()=>{
            var policy=Policy();var row=new ConnectionReport(p.Id,p.Drive,p.Unc,@"QA\old","",false,ConnectionState:"Connected");
            Check(ConnectionRules.CanOpen(row,policy,DateTimeOffset.UtcNow),"Matching external mapping can open without taking ownership");
            Check(!ConnectionRules.CanOpen(row with{Account=@"QA\other"},policy,DateTimeOffset.UtcNow),"Wrong-account mapping cannot open");
            Check(ConnectionRules.AccountState("Connected",@"QA\other",@"QA\old")=="AccountMismatch","Wrong-account mapping is not classified Connected");
            Check(ConnectionRules.AccountState("Connected","",@"QA\old")=="AccountMismatch","Unknown account is not classified Connected");
            Check(ConnectionRules.AccountState("Disconnected","",@"QA\old")=="Disconnected","Disconnected mapping retains its state");
            Check(!ConnectionRules.CanOpen(row,policy with{ExpiresAt=DateTimeOffset.UtcNow.AddSeconds(-1)},DateTimeOffset.UtcNow),"Expired policy cannot open");
            Check(!ConnectionRules.CanOpen(row,policy with{Profiles=[]},DateTimeOffset.UtcNow),"Revoked profile cannot open");
            Check(!ConnectionRules.CanOpen(row with{Unc=@"\\other\share"},policy,DateTimeOffset.UtcNow),"Replaced UNC cannot open");
            Check(!ConnectionRules.CanOpen(row with{Drive="C:"},policy,DateTimeOffset.UtcNow),"Wrong drive cannot open");
            foreach(var state in new[]{"AccountMismatch","Disconnected","Unavailable","Conflict","Unknown"})
                Check(!ConnectionRules.CanOpen(row with{ConnectionState=state},policy,DateTimeOffset.UtcNow),state+" cannot open");
            Check(ConnectionRules.CanOpen(row with{Account=@"QA\server"},policy with{Accounts=[new(p.Id,@"QA\server")]},DateTimeOffset.UtcNow),"Per-server account overrides default");
            Check(!ConnectionRules.CanOpen(row,null,DateTimeOffset.UtcNow),"Missing policy cannot open");
            return Task.CompletedTask;
        });
        await Case("department server credentials",async()=>{
            var serverDir=Path.Combine(run,"server");
            var exe=Wire.Arg(args,"--server-exe",Path.Combine(root,".local","beta1-build8-install","bin","ManagementServer","Release","net10.0","ManagementServer.exe"));
            var start=new ProcessStartInfo(exe){UseShellExecute=false,CreateNoWindow=true};start.ArgumentList.Add("--run-dir");start.ArgumentList.Add(serverDir);start.ArgumentList.Add("--qa-diagnostics");
            using var server=Process.Start(start)!;
            try{
                var readyPath=Path.Combine(serverDir,"ready.json");var until=DateTime.UtcNow.AddSeconds(15);
                while(!File.Exists(readyPath)&&!server.HasExited&&DateTime.UtcNow<until)await Task.Delay(100);
                var ready=Wire.Load<ServerReady>(readyPath);using var admin=Wire.Http(ready.Url,admin:ready.AdminKey);
                async Task<Profile> Add(string name,string host,string drive)=>await Wire.Read<Profile>(await admin.PostAsJsonAsync("api/admin/profiles",new SaveProfile(name,@"\\"+host+@"\review",drive),Wire.Json));
                var a=await Add("Review A","192.0.2.10","M:");var b=await Add("Review B","192.0.2.11","N:");
                async Task<HttpStatusCode> Save(string department,Profile profile){using var response=await admin.PostAsJsonAsync("api/admin/share-credential",new SaveShareCredential(department,profile.Id,@"QA\ops","test-only"),Wire.Json);return response.StatusCode;}
                Check(await Save("운영부",a)==HttpStatusCode.OK,"First department server credential is accepted");
                Check(await Save("사업부",a)==HttpStatusCode.Conflict,"Same server account cannot be assigned to another department");
                Check(await Save("사업부",b)==HttpStatusCode.OK,"Same account name on a different server is allowed");
                var summaries=await admin.GetFromJsonAsync<CredentialSummary[]>("api/admin/share-credentials",Wire.Json)??[];
                Check(!summaries.Any(c=>c.Department=="사업부"&&c.Server=="192.0.2.10"),"Rejected credential write leaves saved settings unchanged");
                using(var template=await admin.PostAsJsonAsync("api/admin/department-templates",new SaveDepartmentTemplate("조리학과",@"QA\ops",[a.Id]),Wire.Json))
                    Check(template.StatusCode==HttpStatusCode.Conflict,"Template default cannot reuse another department actual account");
                var c=await Add("Review C","192.0.2.12","O:");
                using(var template=await admin.PostAsJsonAsync("api/admin/department-templates",new SaveDepartmentTemplate("테스트부",@"QA\default",[c.Id]),Wire.Json))template.EnsureSuccessStatusCode();
                using(var blocked=await admin.PostAsJsonAsync("api/admin/share-credential",new SaveShareCredential("사업부",c.Id,@"QA\default","test-only"),Wire.Json))
                    Check(blocked.StatusCode==HttpStatusCode.Conflict,"Saved credential cannot reuse another department template default");
                var code=await Wire.Read<EnrollmentCode>(await admin.PostAsync("api/admin/enrollment-code",null));
                using var enrollment=Wire.Http(ready.Url);var enrolled=await Wire.Read<EnrollReply>(await enrollment.PostAsJsonAsync("api/enroll",new EnrollRequest(code.Code,"review-only","qa",Guid.NewGuid().ToString(),HardwareIdentity.Capture()),Wire.Json));
                using(var approve=await admin.PostAsync($"api/admin/devices/{enrolled.DeviceId}/approve",null))approve.EnsureSuccessStatusCode();
                using(var assignment=await admin.PostAsJsonAsync($"api/admin/devices/{enrolled.DeviceId}/assignment",new SaveAssignment("사업부",@"QA\ops",[a.Id]),Wire.Json))
                    Check(assignment.StatusCode==HttpStatusCode.Conflict,"Individual assignment cannot bypass actual-account department binding");
                var assigned=await Wire.Read<DeviceAssignment>(await admin.PostAsJsonAsync($"api/admin/devices/{enrolled.DeviceId}/assignment",new SaveAssignment("사업부",@"QA\other",[a.Id]),Wire.Json));
                Check(assigned.ExpectedAccount==@"QA\other","Unrelated department-specific account remains assignable");
                using(var change=await admin.PostAsJsonAsync("api/admin/department-account",new DepartmentAccountChange("사업부",@"QA\other",@"QA\ops"),Wire.Json))
                    Check(change.StatusCode==HttpStatusCode.Conflict,"Bulk account change cannot bypass actual-account department binding");
                var impact=await Wire.Read<CredentialImpact>(await admin.PostAsJsonAsync("api/admin/share-credential-preview",new SaveShareCredential("운영부",a.Id,@"QA\new","test-only"),Wire.Json));
                using(var change=await admin.PostAsJsonAsync("api/admin/share-credential",new SaveShareCredential("운영부",a.Id,@"QA\new","test-only",impact.Revision),Wire.Json))change.EnsureSuccessStatusCode();
                using(var stale=await admin.PostAsJsonAsync("api/admin/share-credential",new SaveShareCredential("운영부",a.Id,@"QA\stale","test-only",impact.Revision),Wire.Json))
                    Check(stale.StatusCode==HttpStatusCode.Conflict,"Stale credential save is rejected after concurrent settings change");

            }finally{if(!server.HasExited){server.Kill(true);await server.WaitForExitAsync();}}
        });
        Wire.Save(Path.Combine(run,"report.json"),new{passed,failed,at=DateTimeOffset.Now});
        Console.WriteLine("REPORT "+Path.Combine(run,"report.json"));Environment.ExitCode=failed.Count==0?0:1;
    }
}
