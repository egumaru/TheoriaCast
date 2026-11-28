using System.Text.Json;
using System.Text.Json.Nodes;

namespace TheoriaCast.Music.Api;

public sealed partial class MusicStore
{
    private readonly CreatorAudio? creator;
    private string ExportDirectory => Path.Combine(Root,"exports");
    private static readonly JsonSerializerOptions ExportJson = new(JsonSerializerDefaults.Web);

    private string LocalId => new AIKernel.Providers.Distributed.ApplicationCredentials(Root).ServerId;
    public object MixVersion() => new {selected=LocalId,servers=new[]{new {server_id=LocalId,release=MusicRelease.Of(typeof(MusicStore).Assembly)}}};

    public async Task<TunedLoop> TuneLoop(LoopSuggestion request,CancellationToken ct)
    {
        var track=GetTrack(request.track);var duration=DurationOf(track);
        if(request.selection==null)throw new ArgumentException("ループ範囲を指定してください。");
        CreatorAudio.CheckLoop(request.selection with {crossfade_ms=10},duration);
        return await (creator??throw new InvalidOperationException("音声処理が未設定です。")).Tune(track.file_path,request.selection,duration,ct);
    }
    public ExportJob Export(CreatorExport request)
    {
        if(request.kind!="loop") throw new ArgumentException("Choose a loop export.");
        var track=GetTrack(request.track);
        CreatorAudio.CheckLoop(request.loop,DurationOf(track));
        string? sha=(string?)track.provenance["sha256"];
        if(sha==null || !IsHash(sha))throw new InvalidDataException("Missing source hash.");
        var id=Id();var date=DateTimeOffset.UtcNow;
        using var db=Open();
        Run(db,"INSERT INTO creator_exports(id,track,sha,request,state,created) VALUES($p0,$p1,$p2,$p3,'queued',$p4)",
            id,track.id,sha,JsonSerializer.Serialize(request,ExportJson),date.ToString("O"));
        return new(id,request,sha,"queued",date);
    }

    private static double DurationOf(Track track)
    {
        var duration=(double?)track.provenance["actual_seconds"];
        return duration is >0 && double.IsFinite(duration.Value)?duration.Value:
            throw new InvalidOperationException("元の曲の再生時間を確認できません。");
    }

    private static ExportJob ReadExport(Microsoft.Data.Sqlite.SqliteDataReader row) =>
        new(row.GetString(0),JsonSerializer.Deserialize<CreatorExport>(row.GetString(1),ExportJson)!,row.GetString(2),row.GetString(3),
            DateTimeOffset.Parse(row.GetString(4),System.Globalization.CultureInfo.InvariantCulture),row.IsDBNull(5)?null:row.GetString(5),
            row.GetString(3)=="done"?"/creator/jobs/"+row.GetString(0)+"/download":null,
            row.IsDBNull(6)?null:JsonNode.Parse(row.GetString(6))!.AsObject());

    private const string ExportQuery = "SELECT id,request,sha,state,created,error,recipe,model FROM creator_exports";

    public ExportJob GetExport(string id)
    {
        using var db=Open();
        return Read(db,ExportQuery+" WHERE id=$p0",ReadExport,id).SingleOrDefault()??throw new KeyNotFoundException();
    }

    internal Stream ExportFile(string id,CancellationToken ct=default)
    {
        var job=GetExport(id);
        if (job.state!="done") throw new InvalidOperationException("書き出しは完了していません。");
        if(job.request.kind=="project_mix")return MixZip(job,ct);
        return File.OpenRead(Path.Combine(ExportDirectory,id+".zip"));
    }

    internal void RecoverExports()
    {
        using var db=Open();
        Run(db,"UPDATE creator_exports SET state='queued' WHERE state='running'");
        RecoverMixProjects(db);
    }

    internal async Task<bool> RunExport(CancellationToken ct)
    {
        var processor=creator??throw new InvalidOperationException("Export processor not configured.");
        ExportJob? job;
        using (var db=Open())
        using (var tx=db.BeginTransaction(deferred:false))
        {
            job=Read(db,ExportQuery+" WHERE state='queued' ORDER BY rowid LIMIT 1",ReadExport).SingleOrDefault();
            if (job==null) return false;
            Run(db,"UPDATE creator_exports SET state='running' WHERE id=$p0",job.id);
            tx.Commit();
        }
        var temporary=Directory.CreateDirectory(Path.Combine(ExportDirectory,"stage-"+Id())).FullName;
        try
        {
            if(job.request.kind=="project_mix") {
                RunProjectMix(job,temporary,ct);
                return true;
            }
            var track=GetTrack(job.request.track);
            var recipe=await processor.Render(job,track,temporary,ct);
            var final=Path.Combine(ExportDirectory,job.id+".zip");
            ct.ThrowIfCancellationRequested();
            File.Move(Path.Combine(temporary,"download.zip"),final,overwrite:true);
            using var db=Open();
            Run(db,"UPDATE creator_exports SET state='done',recipe=$p0 WHERE id=$p1",recipe.ToJsonString(),job.id);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException or ArgumentException or KeyNotFoundException or System.ComponentModel.Win32Exception or JsonException)
        {
            using var db=Open();
            Run(db,"UPDATE creator_exports SET state='failed',error=$p0 WHERE id=$p1",
                job.request.kind=="project_mix" && e is FileNotFoundException?"ミックスに必要な元テイクまたは版ファイルが見つかりません。":
                job.request.kind=="project_mix" && e is IOException?"ミックスの音源または保存先を読み書きできません。":
                e is InvalidOperationException or InvalidDataException or KeyNotFoundException?e.Message:"音声を書き出せませんでした。FFmpeg と保存先を確認してください。",job.id);
        }
        finally { Directory.Delete(temporary,recursive:true); }
        return true;
    }
}

internal sealed class CreatorExportWorker(MusicStore library) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var lease=GpuLease.TryAcquire(Path.Combine(library.Root,"exports","export-worker.lock"));
        if (lease is null) return;
        library.RecoverExports();
        while (!stoppingToken.IsCancellationRequested)
            if (!await library.RunExport(stoppingToken)) await Task.Delay(500,stoppingToken);
    }
}
