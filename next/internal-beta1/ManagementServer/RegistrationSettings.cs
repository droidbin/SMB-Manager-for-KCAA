using SmbManager.Local;
sealed partial class Store
{
    private readonly Dictionary<string,DepartmentTemplate> templates = [];
    private readonly Dictionary<string,ShareCredential> credentials = [];
    private readonly Dictionary<string,DateTimeOffset> registrationThrottle = [];
    private string SettingsFile => PersistentFile + ".registration";
    private sealed record Settings(DepartmentTemplate[] Templates, Dictionary<string,ShareCredential> Credentials);
    private void LoadRegistrationSettings() {
        if(!File.Exists(SettingsFile))return;
        var value=SecretFile.Load<Settings>(SettingsFile);
        foreach(var t in value.Templates)templates[t.Department]=t;
        foreach(var c in value.Credentials)credentials[c.Key]=c.Value;
    }
    private void SaveSettings() { try{SecretFile.Save(SettingsFile,new Settings(templates.Values.ToArray(),credentials));}catch{StorageHealthy=false;throw;} }
    private static string CredentialKey(string department,string profile) => department + "\n" + profile;
    private ShareCredential? FindCredential(string department,string profile){
        if(credentials.TryGetValue(CredentialKey(department,profile),out var exact))return exact;
        var target=profiles.FirstOrDefault(p=>p.Id==profile);if(target is null||!ProfileRules.Available(target))return null;
        var host=target.Unc.Split('\\')[2];
        return profiles.Where(p=>p.Unc.StartsWith(@"\\"+host+@"\",StringComparison.OrdinalIgnoreCase)).Select(p=>credentials.GetValueOrDefault(CredentialKey(department,p.Id))).FirstOrDefault(c=>c is not null);
    }
    private string GetAccount(DeviceAssignment a,string profile) => FindCredential(a.Department,profile)?.Account ?? a.ExpectedAccount;
    public string[] RegistrationDepartments(){lock(gate)return new[]{"운영부","사업부","바리스타학과","제과제빵학과","조리학과","임원"}.Concat(templates.Keys).Concat(assignments.Values.Select(a=>a.Department)).Distinct().Order().ToArray();}
    public DepartmentTemplate[] DepartmentTemplates(){lock(gate)return templates.Values.ToArray();}
    public IResult RequestRegistration(string ip,RegistrationRequest r){lock(gate){
        if(!RegistrationDepartments().Contains(r.Department)||string.IsNullOrWhiteSpace(r.Name)||r.Name.Length>80||r.Name.Any(char.IsControl)||string.IsNullOrWhiteSpace(r.User)||r.User.Length>160||!Guid.TryParse(r.SessionId,out _)||!HardwareIdentity.Valid(r.Hardware)||!ValidComputerName(r.ComputerName))return Results.BadRequest(new{error="INVALID_REGISTRATION"});
        foreach(var key in registrationThrottle.Where(x=>Now-x.Value>TimeSpan.FromMinutes(1)).Select(x=>x.Key).ToArray())registrationThrottle.Remove(key);
        if(registrationThrottle.TryGetValue(ip,out var at))return Results.Json(new{error="REGISTRATION_RETRY",retryAfterSeconds=Math.Max(1,(int)Math.Ceiling(60-(Now-at).TotalSeconds))},statusCode:429);
        if(registrationThrottle.Count>=10000)return Results.StatusCode(429);
        var token=Wire.Secret(); codes[token]=new(token,Now.AddSeconds(10));
        try {
            var result=Enroll(new(token,r.Name,r.User,r.SessionId,r.Hardware,r.ComputerName));
            var d=devices.Values.FirstOrDefault(d=>d.SessionId==r.SessionId && !d.Approved && HardwareIdentity.Same(d.Hardware,r.Hardware));
            if(d is not null){d.RequestedDepartment=r.Department;Persist();registrationThrottle[ip]=Now;}
            return result;
        } finally{codes.Remove(token);}
    }}
    private static bool ValidComputerName(string? value)=>value is not null && value.Length<=160 && !value.Any(char.IsControl);
    private Profile[]? TemplateProfiles(SaveDepartmentTemplate t){
        if(!AssignmentRules.ValidDepartment(t.Department)||t.Department=="공용"||!AssignmentRules.ValidAccount(t.ExpectedAccount)||t.AccessRole is not ("직원" or "임원")||t.ProfileIds is null||t.ProfileIds.Length>26)return null;
        var ids=t.AccessRole=="임원"?profiles.Where(p=>ProfileRules.Available(p)).Select(p=>p.Id).ToArray():t.ProfileIds;
        var selected=ids.Select(id=>profiles.FirstOrDefault(p=>p.Id==id)).ToArray();
        return selected.Any(p=>p is null||!ProfileRules.Available(p))||selected.Select(p=>p!.Drive).Distinct().Count()!=selected.Length?null:selected.Cast<Profile>().ToArray();
    }
    private string DeviceLabel(Device d)=>$"{d.Name} / {(d.ComputerName.Length>0?d.ComputerName:"PC명 미확인")} / ID {d.Id[^8..]}";
    private string AssignmentLabel(string id)=>DeviceLabel(devices[id])+" / 기존: "+string.Join(" · ",assignments[id].ProfileIds.Select(key=>profiles.FirstOrDefault(p=>p.Id==key)?.Name??key));
    private TemplateImpact Impact(string department)=>new(policyRevision,
        assignments.Where(x=>x.Value.Department==department&&x.Value.FollowDepartment).Select(x=>AssignmentLabel(x.Key)).ToArray(),
        assignments.Where(x=>x.Value.Department==department&&!x.Value.FollowDepartment).Select(x=>AssignmentLabel(x.Key)).ToArray(),
        devices.Values.Where(d=>d.Approved&&!d.Archived&&!assignments.ContainsKey(d.Id)&&d.RequestedDepartment==department).Select(d=>DeviceLabel(d)).ToArray());
    public IResult TemplatePreview(SaveDepartmentTemplate t){lock(gate)return TemplateProfiles(t) is null?Results.BadRequest(new{error="INVALID_TEMPLATE"}):Results.Ok(Impact(t.Department));}
    public IResult SaveDepartmentTemplate(SaveDepartmentTemplate t){lock(gate){
        var selected=TemplateProfiles(t);if(selected is null)return Results.BadRequest(new{error="DRIVE_CONFLICT_OR_INVALID_TEMPLATE"});
        if(t.ExpectedRevision is not null&&t.ExpectedRevision!=policyRevision)return Results.Conflict(new{error="SETTINGS_CHANGED"});
        if(assignments.Any(x=>x.Value.Department!=t.Department&&string.Equals(x.Value.ExpectedAccount,t.ExpectedAccount,StringComparison.OrdinalIgnoreCase)))return Results.Conflict(new{error="ACCOUNT_BELONGS_TO_OTHER_DEPARTMENT"});
        if(assignments.Any(x=>x.Value.Department==t.Department&&!x.Value.FollowDepartment&&!t.IncludeIndividual&&!string.Equals(x.Value.ExpectedAccount,t.ExpectedAccount,StringComparison.OrdinalIgnoreCase)))return Results.Conflict(new{error="INDIVIDUAL_ACCOUNT_REVIEW_REQUIRED"});
        if(ConflictingServerAccounts(t.Department,t.ExpectedAccount,selected))return Results.Conflict(new{error="SERVER_ACCOUNT_BELONGS_TO_OTHER_DEPARTMENT"});
        var next=new Dictionary<string,DeviceAssignment>(assignments);var ids=selected.Select(p=>p.Id).ToArray();
        foreach(var d in assignments.Where(x=>x.Value.Department==t.Department&&(x.Value.FollowDepartment||t.IncludeIndividual)))
            next[d.Key]=d.Value with{ProfileIds=ids,ExpectedAccount=t.ExpectedAccount,AccessRole=t.AccessRole,FollowDepartment=true,Revision=policyRevision+1};
        if(t.IncludeApprovedUnassigned)foreach(var d in devices.Values.Where(d=>d.Approved&&!d.Archived&&!assignments.ContainsKey(d.Id)&&d.RequestedDepartment==t.Department))
            next[d.Id]=new(t.Department,t.ExpectedAccount,ids,true,false,policyRevision+1,false,t.AccessRole,true);
        var saved=new DepartmentTemplate(t.Department,t.ExpectedAccount,ids,t.AccessRole);
        templates[t.Department]=saved;SaveSettings();CommitPolicy(profiles,next);RecordAudit("부서 기본 설정",t.Department,$"개별 포함: {t.IncludeIndividual} / 미배정 포함: {t.IncludeApprovedUnassigned}");Persist();return Results.Ok(saved);
    }}
    public IResult CredentialSummaries(){lock(gate){
        var values=credentials.Select(x=>{var split=x.Key.Split('\n');var p=profiles.FirstOrDefault(p=>p.Id==split[1]);return p is null?null:new CredentialSummary(split[0],p.Unc.Split('\\')[2],x.Value.Account,x.Value.Password.Length>0,profiles.Where(q=>q.Unc.StartsWith(@"\\"+p.Unc.Split('\\')[2]+@"\",StringComparison.OrdinalIgnoreCase)).Select(q=>q.Name).ToArray());}).OfType<CredentialSummary>().DistinctBy(x=>(x.Department,x.Server.ToUpperInvariant())).ToArray();
        return Results.Ok(values);
    }}
    public IResult CredentialPreview(SaveShareCredential r){lock(gate){
        var target=profiles.FirstOrDefault(p=>p.Id==r.ProfileId&&ProfileRules.Available(p));
        if(!AssignmentRules.ValidDepartment(r.Department)||target is null)return Results.BadRequest();
        var host=target.Unc.Split('\\')[2];var existing=FindCredential(r.Department,r.ProfileId);
        return Results.Ok(new CredentialImpact(policyRevision,host,profiles.Where(p=>p.Unc.StartsWith(@"\\"+host+@"\",StringComparison.OrdinalIgnoreCase)).Select(p=>p.Name).ToArray(),existing?.Account??"",existing?.Password.Length>0));
    }}
    // Bind actual per-server credentials as well as default department account labels.
    private bool OtherDepartmentUsesServerAccount(string department,string host,string account){
        bool SameHost(Profile p)=>ProfileRules.Available(p)&&string.Equals(p.Unc.Split('\\')[2],host,StringComparison.OrdinalIgnoreCase);
        bool SameAccount(string value)=>string.Equals(value,account,StringComparison.OrdinalIgnoreCase);
        foreach(var entry in credentials){
            var key=entry.Key.Split('\n');
            if(key.Length==2&&key[0]!=department&&SameAccount(entry.Value.Account)&&profiles.Any(p=>p.Id==key[1]&&SameHost(p)))return true;
        }
        foreach(var a in assignments.Values.Where(a=>a.Department!=department))
            if(profiles.Any(p=>SameHost(p)&&(a.AccessRole=="임원"||a.ProfileIds.Contains(p.Id))&&SameAccount(GetAccount(a,p.Id))))return true;
        foreach(var t in templates.Values.Where(t=>t.Department!=department))
            if(profiles.Any(p=>SameHost(p)&&(t.AccessRole=="임원"||t.ProfileIds.Contains(p.Id))&&SameAccount(FindCredential(t.Department,p.Id)?.Account??t.ExpectedAccount)))return true;
        return false;
    }
    private bool ConflictingServerAccounts(string department,string fallback,IEnumerable<Profile> selected)=>selected.Any(p=>
        OtherDepartmentUsesServerAccount(department,p.Unc.Split('\\')[2],FindCredential(department,p.Id)?.Account??fallback));
    public IResult SaveCredential(SaveShareCredential r){lock(gate){
        if(!AssignmentRules.ValidDepartment(r.Department)||!profiles.Any(p=>p.Id==r.ProfileId&&ProfileRules.Available(p))||!AssignmentRules.ValidAccount(r.Account)||r.Password is null||r.Password.Length>256)return Results.BadRequest();
        if(r.ExpectedRevision is not null&&r.ExpectedRevision!=policyRevision)return Results.Conflict(new{error="SETTINGS_CHANGED"});
        var target=profiles.Single(p=>p.Id==r.ProfileId);var host=target.Unc.Split('\\')[2];
        if(OtherDepartmentUsesServerAccount(r.Department,host,r.Account))return Results.Conflict(new{error="SERVER_ACCOUNT_BELONGS_TO_OTHER_DEPARTMENT"});
        foreach(var p in profiles.Where(p=>p.Unc.StartsWith(@"\\"+host+@"\",StringComparison.OrdinalIgnoreCase)))
            credentials[CredentialKey(r.Department,p.Id)]=new(r.Account,r.Password);
        SaveSettings();
        var next=new Dictionary<string,DeviceAssignment>(assignments);foreach(var a in assignments.Where(x=>x.Value.Department==r.Department))next[a.Key]=a.Value with{Revision=policyRevision+1,ResumeStopped=false};CommitPolicy(profiles,next);RecordAudit("서버 계정 변경",r.Department,host+" / "+r.Account);Persist();return Results.Ok(new{saved=true});
    }}
    public IResult RegistrationView(string id){lock(gate){var d=devices[id];return Results.Ok(new ClientRegistration(d.Name,d.RequestedDepartment,assignments.GetValueOrDefault(id)?.Department??"",d.Approved,d.ComputerName,d.RequestedAt,d.Correction,d.Archived));}}
    public IResult RequestCorrection(string id,CorrectionRequest r){lock(gate){
        if(string.IsNullOrWhiteSpace(r.Name)||r.Name.Length>80||r.Name.Any(char.IsControl)||!RegistrationDepartments().Contains(r.Department))return Results.BadRequest();
        var d=devices[id];if(d.Archived)return Results.Conflict(new{error="DEVICE_ARCHIVED"});
        d.Correction=new(r.Name.Trim(),r.Department,Now);Persist();return Results.Ok(new{requested=true});
    }}
    public IResult ReviewRegistration(string id,ReviewCorrection r){lock(gate){
        if(!devices.TryGetValue(id,out var d)||d.Correction is null)return Results.NotFound();
        if(r.Accept){d.Name=d.Correction.Name;d.RequestedDepartment=d.Correction.Department;}
        d.Correction=null;RecordAudit(r.Accept?"등록 정정 수락":"등록 정정 거절",id,d.Name);Persist();return Results.Ok(new{reviewed=true});
    }}
    public IResult ArchiveDevice(string id){lock(gate){
        if(!devices.TryGetValue(id,out var d))return Results.NotFound();
        d.Approved=false;d.Archived=true;d.BetaAdministrator=false;
        foreach(var w in commands.Values.Where(w=>w.Payload.DeviceId==id&&w.State is "Queued" or "Leased" or "Running")){w.State="Rejected";w.Detail="등록 보관 — 새 작업 중지";}
        var next=new Dictionary<string,DeviceAssignment>(assignments);next.Remove(id);CommitPolicy(profiles,next);RecordAudit("등록 보관",id,d.Name);Persist();
        return Results.Ok(new{archived=true});
    }}
    public IResult Credential(string id,string profile){lock(gate){
        if(!devices[id].Approved||devices[id].Quarantined||devices[id].Paused||!profiles.Any(p=>p.Id==profile&&Allows(id,p)))return Results.StatusCode(403);
        var a=assignments[id];var c=FindCredential(a.Department,profile);return c is not null?Results.Ok(c):Results.NoContent();
    }}
    public IResult RequestReconnect(string id){lock(gate){
        if(!devices.TryGetValue(id,out var d)||!d.Approved||d.Quarantined||d.Paused||Now-d.LastSeen>TimeSpan.FromSeconds(30)||!assignments.TryGetValue(id,out var a))return Results.Conflict();
        if(commands.Count>=10000)return Results.StatusCode(429);
        d.ConnectionsHeld=false;CommitPolicy(profiles,new(assignments){[id]=a with{Revision=policyRevision+1,ResumeStopped=true}});Persist();return Create(new(id,"",Operation:"ReconnectAssigned"));
    }}
    private void EnsureLegacyCatalog(){
        var names=new[]{("지점공용","Y:"),("사업부","X:"),("바리스타학과","W:"),("제과제빵학과","U:"),("조리학과","V:"),("운영부","Z:")};
        var extra=names.Where(x=>!profiles.Any(p=>p.Id=="legacy-"+x.Item2[..1]||string.Equals(p.Unc,@"\\192.168.1.65\"+x.Item1,StringComparison.OrdinalIgnoreCase)))
            .Select(x=>new Profile("legacy-"+x.Item2[..1],x.Item1,"SimulateConnect",1,@"\\192.168.1.65\"+x.Item1,x.Item2,x.Item1=="지점공용"?"공용":x.Item1)).ToArray();
        if(extra.Length>0)CommitPolicy(profiles.Concat(extra).ToArray(),new(assignments));
    }}