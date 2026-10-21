using SmbManager.Local;
sealed partial class Store {
    private readonly List<AuditEntry> audit=[];
    private void RecordAudit(string action,string target,string summary){audit.Add(new(Now,action,target,summary));if(audit.Count>500)audit.RemoveRange(0,audit.Count-500);}
    public IResult Audit(){lock(gate)return Results.Ok(audit.AsEnumerable().Reverse().ToArray());}
    public IResult ProfileImpact(string id){lock(gate){if(!profiles.Any(p=>p.Id==id))return Results.NotFound();return Results.Ok(new ProfileImpact(policyRevision,assignments.Where(x=>x.Value.AccessRole=="임원"||x.Value.ProfileIds.Contains(id)).Select(x=>DeviceLabel(devices[x.Key])).ToArray(),templates.Values.Where(t=>t.AccessRole=="임원"||t.ProfileIds.Contains(id)).Select(t=>t.Department).ToArray()));}}
    public IResult ProfileStatus(string id,ProfileStatusChange r){lock(gate){
        var old=profiles.FirstOrDefault(p=>p.Id==id);if(old is null)return Results.NotFound();
        if(old.Id.StartsWith("demo-")||old.Revision!=r.ExpectedRevision||policyRevision!=r.ExpectedPolicyRevision)return Results.Conflict(new{error="SETTINGS_CHANGED"});
        var updated=old with{Mode=r.Enabled?"SimulateConnect":"Disabled",Revision=old.Revision+1};var nextProfiles=profiles.Select(p=>p.Id==id?updated:p).ToArray();
        if(r.Enabled&&assignments.Values.Any(a=>a.AccessRole=="임원")&&nextProfiles.Where(ProfileRules.Available).GroupBy(p=>p.Drive).Any(g=>g.Count()>1))return Results.Conflict(new{error="EXECUTIVE_DRIVE_CONFLICT"});
        var next=new Dictionary<string,DeviceAssignment>(assignments);
        if(!r.Enabled){foreach(var a in assignments.Where(x=>x.Value.ProfileIds.Contains(id)))next[a.Key]=a.Value with{ProfileIds=a.Value.ProfileIds.Where(key=>key!=id).ToArray(),Revision=policyRevision+1};foreach(var t in templates.Values.ToArray())templates[t.Department]=t with{ProfileIds=t.ProfileIds.Where(key=>key!=id).ToArray()};SaveSettings();}
        CommitPolicy(nextProfiles,next);RecordAudit(r.Enabled?"공유 다시 사용":"공유 사용 중지",id,old.Name);Persist();return Results.Ok(updated);
    }}
}
