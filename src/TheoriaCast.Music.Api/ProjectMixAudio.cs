using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace TheoriaCast.Music.Api;

public sealed partial class MusicStore
{
    internal int MixKeepLatest { get; init; } = 3;
    private static string HashAudio(Stream stream)
    {
        string sha=Convert.ToHexStringLower(SHA256.HashData(stream));
        stream.Position=0;
        return sha;
    }
    private Stream ScratchStream() {
        string dir=Directory.CreateDirectory(Path.Combine(Root,"partial")).FullName;
        return new FileStream(Path.Combine(dir,"mix-"+Id()),FileMode.CreateNew,FileAccess.ReadWrite,FileShare.Read|FileShare.Delete,
            65536,FileOptions.DeleteOnClose|FileOptions.SequentialScan);
    }
    private StaticMixRenderer.Analysis MakeMix(MixRevision revision,Stream output,CancellationToken ct)
        => StaticMixRenderer.Render(revision.manifest,media=>{
            var source=OpenClipMedia(media,ct);
            try {
                if(HashAudio(source)!=(media.loop_sha256??media.sha256))throw new InvalidDataException("Source audio changed: "+media.take);
                return source;
            } catch { source.Dispose(); throw; }
        },output,ct);

    internal Stream OpenTrackAudio(string id,CancellationToken ct)
    {
        var take=GetTrack(id);
        try { return new FileStream(take.file_path,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete); }
        catch(FileNotFoundException) when(take.provenance["mix_project"]!=null) { }
        var target=take.provenance["mix_project"]!.Deserialize<ProjectRenderRef>()!;
        var scratch=ScratchStream();
        try {
            MakeMix(Version(target),scratch,ct);
            scratch.Position=0;
            if(HashAudio(scratch)!=(string?)take.provenance["sha256"]) throw new InvalidDataException("Regenerated WAV differs from the recorded mix.");
            return scratch;
        } catch { scratch.Dispose(); throw; }
    }
    private Stream MixZip(ExportJob job,CancellationToken ct)
    {
        var scratch=ScratchStream();
        try {
            using(var archive=new ZipArchive(scratch,ZipArchiveMode.Create,true)) {
                using(var wav=OpenTrackAudio(job.request.project_mix!.revision,ct))
                using(var output=archive.CreateEntry("master.wav",CompressionLevel.Fastest).Open()) wav.CopyTo(output);
                using var recipe=archive.CreateEntry("recipe.json").Open();
                JsonSerializer.Serialize(recipe,job.recipe,ProjectJson);
            }
            scratch.Position=0;return scratch;
        } catch { scratch.Dispose();throw; }
    }
    private void RunProjectMix(ExportJob job,string stage,CancellationToken ct)
    {
        var target=job.request.project_mix!;
        var version=Version(target);
        string wav=Path.Combine(stage,"master.wav");
        StaticMixRenderer.Analysis analysis;
        using(var output=File.Create(wav)) analysis=MakeMix(version,output,ct);
        string sha;
        using(var audio=File.OpenRead(wav)) sha=HashAudio(audio);
        ct.ThrowIfCancellationRequested();
        var recipe=new {renderer=version.manifest.schema switch {DspSchema=>"managed-dsp-pcm24/v3",ClipSchema=>"managed-timeline-pcm24/v2",_=>"managed-static-pcm24/v1"},project=target.project,revision=version.id,version.parent,
            version.changes,manifest=version.manifest,result_track=version.id,analysis,verification=new {wav_sha256=sha}};
        string relative=Path.Combine("mix-audio",target.project,version.id);
        string folder=Directory.CreateDirectory(Path.Combine(Root,relative)).FullName;
        using var db=Open();using var tx=db.BeginTransaction(deferred:false);
        var existing=Read(db,"SELECT * FROM tracks WHERE id=$p0",ToTrack,version.id).SingleOrDefault();
        string receipt=Path.Combine(ProjectRoot,target.project,"receipts",version.id+".json");
        if(File.Exists(receipt) && (string?)JsonNode.Parse(File.ReadAllText(receipt))?["verification"]?["wav_sha256"]!=sha ||
           existing!=null && (string?)existing.provenance["sha256"]!=sha)
            throw new InvalidDataException("Regenerated mix differs from its original WAV.");
        if(existing==null) {
            var origin=new {sha256=sha,actual_seconds=analysis.frames/(double)StaticMixRenderer.Rate,
                mix_project=target,recipe,materials=new[]{"recipe.json"},review=new {status="pending"}};
            AtomicJson(Path.Combine(folder,"master.wav.json"),origin);
            AtomicJson(Path.Combine(folder,"recipe.json"),recipe);
            Run(db,"INSERT INTO tracks(id,title,folder,origin) VALUES($p0,$p1,$p2,$p3)",version.id,
                version.manifest.title+"（ミックス）",relative,JsonSerializer.Serialize(origin));
        }
        if(!File.Exists(receipt))AtomicJson(receipt,recipe);
        File.Move(wav,Path.Combine(folder,"master.wav"),true);
        Run(db,"UPDATE creator_exports SET state='done',recipe=$p0 WHERE id=$p1",JsonSerializer.Serialize(recipe),job.id);
        TrimMixAudio(db,target.project);
        tx.Commit();
    }
    private void TrimMixAudio(SqliteConnection db,string project)
    {
        var outputs=Read(db,"""
            SELECT t.* FROM tracks t JOIN creator_exports e ON t.id=e.id
            WHERE json_extract(t.origin,'$.mix_project.project')=$p0 ORDER BY e.rowid DESC
            """,ToTrack,project);
        foreach(var take in outputs.Skip(MixKeepLatest).Where(t=>t.status!="adopted"))
            File.Delete(take.file_path);
    }
    private void RestoreAdoptedAudio(Track track)
    {
        if(File.Exists(track.file_path))return;
        using var wav=OpenTrackAudio(track.id,CancellationToken.None);
        string tmp=track.file_path+"."+Id()+".tmp";
        try {
            using(var output=File.Create(tmp)) wav.CopyTo(output);
            File.Move(tmp,track.file_path,true);
        } finally { File.Delete(tmp); }
    }
}
