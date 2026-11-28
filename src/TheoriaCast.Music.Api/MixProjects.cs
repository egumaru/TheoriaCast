using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace TheoriaCast.Music.Api;

public sealed partial class MusicStore
{
    private sealed record MixRevision(string project, string id, string? parent, string action, DateTimeOffset created,
        string? label, string? restored_from, string[] takes, JsonObject changes, MixManifest manifest);
    private static readonly JsonSerializerOptions ProjectJson = new(JsonSerializerDefaults.Web) { WriteIndented=true };
    private string ProjectRoot => Path.Combine(Root,"mix-projects");
    private string RevisionFile(string project,string revision) =>
        Path.Combine(ProjectRoot,ProjectId(project),"revisions",ProjectId(revision)+".json");
    private static string ProjectId(string? id) => Guid.TryParseExact(id,"N",out var value)
        ? value.ToString("N") : throw new ArgumentException("Invalid project or revision ID.");
    private static void AtomicJson(string file,object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        string temp=file+"."+Id()+".tmp";
        try {
            using(var output=File.Create(temp)) { JsonSerializer.Serialize(output,value,ProjectJson); output.Flush(true); }
            File.Move(temp,file,true);
        } finally { File.Delete(temp); }
    }
    private static MixRevision ReadVersion(string path) => File.Exists(path)
        ? JsonSerializer.Deserialize<MixRevision>(File.ReadAllText(path),ProjectJson)??throw new InvalidDataException(path)
        : throw new KeyNotFoundException("Project or revision was not found.");
    private MixRevision Version(ProjectRenderRef target)=>ReadVersion(RevisionFile(target.project,target.revision));
    private IEnumerable<MixRevision> Ancestors(MixRevision current)
    {
        var seen=new HashSet<string>();
        while(seen.Add(current.id)) {
            yield return current;
            if(current.parent==null) yield break;
            current=Version(new(current.project,current.parent));
        }
        throw new InvalidDataException("Cycle in project history.");
    }
    private MixManifest Pin(MixManifest? manifest)
    {
        if(manifest==null || string.IsNullOrWhiteSpace(manifest.title) || manifest.tracks==null ||
           manifest.schema is not ("theoriacast.static-mix/v1" or ClipSchema or DspSchema) || manifest.time_unit!="samples" || manifest.sample_rate!=StaticMixRenderer.Rate ||
           !ValidGain(manifest.master_gain_db) || manifest.bpm is {} bpm && (!double.IsFinite(bpm)||bpm<=0))
            throw new ArgumentException("Check the project title, sample clock, BPM and master gain.");
        var names=new HashSet<string>(StringComparer.Ordinal);
        var pinned=new List<ProjectLane>();
        foreach(var lane in manifest.tracks) {
            if(lane is null || string.IsNullOrWhiteSpace(lane.id) || !names.Add(lane.id) ||
                lane.start_sample<0 || lane.start_sample>(uint.MaxValue-36L)/6 || !ValidGain(lane.gain_db) ||
                !double.IsFinite(lane.pan) || Math.Abs(lane.pan)>1)
                throw new ArgumentException("Check lane IDs, sample positions, pan and gain.");
            var track=GetTrack(lane.take);
            string sha=(string?)track.provenance["sha256"]??throw new InvalidDataException("Take has no WAV hash.");
            if(!IsHash(sha) || lane.sha256!=null && lane.sha256!=sha) throw new InvalidDataException("Take hash mismatch: "+lane.take);
            if(lane.clips!=null && manifest.schema is not (ClipSchema or DspSchema)) throw new ArgumentException("Clip lanes require the timeline schema.");
            pinned.Add(lane with {sha256=sha,clips=PinClips(lane),dsp=MixDsp.Pin(lane.dsp)});
        }
        return manifest with {tracks=pinned.ToArray(), schema=pinned.Any(lane=>lane.dsp is {Length:>0})?DspSchema:manifest.schema};
    }
    private static bool ValidGain(double value)=>double.IsFinite(value) && value is >= -120 and <= 36;
    private static JsonObject Changes(MixManifest? old,MixManifest current)
    {
        var before=JsonSerializer.SerializeToNode(old)?.AsObject();
        var next=JsonSerializer.SerializeToNode(current)!.AsObject();
        var changes=new JsonObject();
        foreach(var (key,value) in next.Where(k=>k.Key!="tracks"))
            if(before==null || !JsonNode.DeepEquals(before[key],value)) changes[key]=value?.DeepClone();
        var previous=(old?.tracks??[]).ToDictionary(l=>l.id);
        changes["updated_tracks"]=JsonSerializer.SerializeToNode(current.tracks.Where(l=>!previous.TryGetValue(l.id,out var p)||
            !JsonNode.DeepEquals(JsonSerializer.SerializeToNode(p),JsonSerializer.SerializeToNode(l))));
        changes["removed_tracks"]=JsonSerializer.SerializeToNode(previous.Keys.Except(current.tracks.Select(l=>l.id)));
        if(!(old?.tracks??[]).Select(l=>l.id).SequenceEqual(current.tracks.Select(l=>l.id)))
            changes["order"]=JsonSerializer.SerializeToNode(current.tracks.Select(l=>l.id));
        return changes;
    }

    public object MixProjects(MixProjectCommand input)
    {
        if(input.action=="list")
            return Directory.Exists(ProjectRoot)?Directory.EnumerateDirectories(ProjectRoot)
                .Select(dir=>Path.Combine(dir,"project.json")).Where(File.Exists).Select(ReadVersion)
                .Select(v=>new {v.project,head=v.id,v.manifest.title}).ToArray():[];
        bool create=input.action=="create";
        string project=create?Id():ProjectId(input.project);
        string head=Path.Combine(ProjectRoot,project,"project.json");
        using var db=Open();
        using var tx=db.BeginTransaction(deferred:false);
        MixRevision? parent=create?null:ReadVersion(head);
        object? job=null;
        if(input.action=="get" || input.action=="history") {
            var history=Ancestors(parent!).ToArray();
            RepairQueue(db,history);
            var selected=input.revision is null?parent!:history.FirstOrDefault(v=>v.id==input.revision)??throw new KeyNotFoundException();
            var states=input.action=="history"?history.Select(v=>new {revision=v,output=ProjectOutput(db,v)}).ToArray():null;
            tx.Commit();
            return new {project,head=parent!.id,revision=selected,history=states,keep_latest=MixKeepLatest};
        }
        if(input.action=="regenerate") {
            var target=Version(new(project,ProjectId(input.revision)));
            if(target.action!="render" || !Ancestors(parent!).Any(v=>v.id==target.id)) throw new ArgumentException("Select a rendered revision from this project.");
            job=QueueMix(db,target,Id()); tx.Commit(); return new {project,head=parent!.id,job};
        }
        if(!create && (parent==null || parent.id!=input.expected))
            throw new InvalidOperationException("Project changed. Read the head and reapply the intended change.");
        var manifest=input.action switch {
            "create" or "save"=>Pin(input.manifest),
            "add_dsp" or "set_dsp" or "move_dsp" or "delete_dsp"=>EditDsp(parent!.manifest,input),
            "place_clip" or "move_clip" or "trim_clip" or "fade_clip" or "loop_clip" or "delete_clip"=>EditClip(parent!.manifest,input),
            "snapshot" when !string.IsNullOrWhiteSpace(input.label)=>parent!.manifest,
            "render"=>input.manifest is null?parent!.manifest:Pin(input.manifest),
            "rollback"=>Ancestors(parent!).FirstOrDefault(v=>v.id==input.revision)?.manifest??throw new KeyNotFoundException("Unknown rollback revision."),
            _=>throw new ArgumentException("Unknown project action or missing snapshot label.")
        };
        if(input.action=="render" && !Sources(manifest).Any()) throw new ArgumentException("Add a take or a clip to render.");
        var version=new MixRevision(project,Id(),parent?.id,input.action,DateTimeOffset.UtcNow,input.label,
            input.action=="rollback"?input.revision:null,Sources(manifest).Select(s=>s.take).ToArray(),Changes(parent?.manifest,manifest),manifest);
        AtomicJson(RevisionFile(project,version.id),version);
        if(input.action=="render") job=QueueMix(db,version,version.id);
        AtomicJson(head,version);
        tx.Commit();
        return new {project,head=version.id,revision=version,job,keep_latest=MixKeepLatest};
    }
    private object? ProjectOutput(SqliteConnection db,MixRevision v)
    {
        if(v.action!="render")return null;
        var take=Read(db,"SELECT * FROM tracks WHERE id=$p0",ToTrack,v.id).SingleOrDefault();
        return new {job=v.id,state=Value(db,"SELECT state FROM creator_exports WHERE id=$p0",v.id),
            track=take?.id,status=take?.status,retained=take!=null && File.Exists(take.file_path),sha256=(string?)take?.provenance["sha256"]};
    }
    private static string QueueMix(SqliteConnection db,MixRevision v,string id)
    {
        var source=Sources(v.manifest).First();
        var request=new CreatorExport(source.take,"project_mix",project_mix:new(v.project,v.id));
        Run(db,"INSERT INTO creator_exports(id,track,sha,request,state,created) VALUES($p0,$p1,$p2,$p3,'queued',$p4)",
            id,source.take,source.sha256,JsonSerializer.Serialize(request,ExportJson),DateTimeOffset.UtcNow.ToString("O"));
        return id;
    }
    // A crash between the atomic manifest replacement and the SQLite commit leaves a replayable intent.
    private static void RepairQueue(SqliteConnection db,IEnumerable<MixRevision> history)
    {
        foreach(var v in history.Reverse().Where(v=>v.action=="render"))
            if(Value(db,"SELECT 1 FROM creator_exports WHERE id=$p0",v.id)==null) QueueMix(db,v,v.id);
    }
    private void RecoverMixProjects(SqliteConnection db)
    {
        if(!Directory.Exists(ProjectRoot))return;
        using var tx=db.BeginTransaction(deferred:false);
        foreach(var dir in Directory.EnumerateDirectories(ProjectRoot)) {
            string head=Path.Combine(dir,"project.json");
            if(!File.Exists(head))continue;
            var version=ReadVersion(head);
            RepairQueue(db,Ancestors(version));
            TrimMixAudio(db,version.project);
        }
        tx.Commit();
    }
}
