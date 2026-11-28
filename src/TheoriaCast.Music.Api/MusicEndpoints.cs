using System.Text.Json;
using System.Globalization;
using AIKernel.Abstractions.Hosting;
using AIKernel.Dtos.Hosting;

namespace TheoriaCast.Music.Api;

internal sealed class MusicEndpoints(MusicStore store, ILogger<MusicEndpoints> log) : IApiEndpointProvider
{
    private const string Client="client", Worker="worker", Json="application/json";
    public IEnumerable<ApiEndpoint> Endpoints => [
        new("GET","/version","version",Client),
        new("POST","/creator/mix-projects","mixProjects",Client,Json),
        new("GET","/creator/jobs/{id}","exportJob",Client),
        new("GET","/creator/jobs/{id}/download","exportFile",Client),
        new("GET","/jobs","jobs",Client), new("POST","/jobs","submit",Client,Json),
        new("GET","/jobs/revision","revision",Client),
        new("GET","/jobs/{id}","job",Client), new("POST","/jobs/{id}/cancel","cancel",Client),
        new("GET","/tracks","tracks",Client), new("GET","/tracks/{id}","track",Client),
        new("GET","/tracks/{id}/audio","audio",Client), new("GET","/tracks/{id}/captions","captions",Client),
        new("GET","/tracks/{id}/waveform","waveform",Client),
        new("GET","/tracks/{id}/materials/{name}","material",Client),
        new("GET","/tracks/{id}/composition.zip","trackSet",Client),
        new("POST","/composition/import","importSet",Client),new("POST","/composition/pack","packSet",Client,Json),
        new("POST","/tracks/{id}/adopt","adopt",Client), new("GET","/nodes","nodes",Client),
        new("GET","/works","works",Client), new("PUT","/works/{name}","work",Client,Json),
        new("POST","/studio/requests","ask",Client,Json), new("GET","/studio/requests/{id}","consultation",Client),
        new("POST","/studio/requests/{id}/decision","decide",Client,Json),
        new("GET","/worker/demand/{node}","demand",Worker), new("POST","/worker/poll","poll",Worker,Json),
        new("POST","/worker/{id}/progress","progress",Worker,Json), new("POST","/worker/{id}/assign","assign",Worker),
        new("POST","/worker/{id}/finish","finish",Worker,"application/zip"),
        new("GET","/worker/jobs/{id}","job",Worker), new("GET","/worker/tracks/{id}","track",Worker),
        new("GET","/worker/tracks/{id}/audio","audio",Worker)
    ];

    private static ApiResponse Result(object? value,int status=200,string? location=null) =>
        new(status,JsonSerializer.SerializeToElement(value,JsonSerializerOptions.Web),Location:location);
    private static ApiResponse Accepted(Job job) => Result(job,202,"/jobs/"+job.id);

    public async ValueTask<ApiResponse> InvokeAsync(string operationId,ApiInvocation request,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var id=request.RouteValues.GetValueOrDefault("id") ?? "";
        async ValueTask<T> Body<T>() => await JsonSerializer.DeserializeAsync<T>(request.Body,JsonSerializerOptions.Web,ct)
            ?? throw new InvalidDataException("入力が空です。");
        try
        {
            switch (operationId)
            {
                case "version": return Result(MusicRelease.Of(typeof(MusicStore).Assembly));
                case "mixProjects": return Result(store.MixProjects(await Body<MixProjectCommand>()));
                case "exportJob": return Result(store.GetExport(id));
                case "exportFile": return new(200,Body:store.ExportFile(id,ct),ContentType:"application/zip",EnableRanges:true);
                case "jobs": return Result(store.Jobs());
                case "revision": return Result(store.Revisions());
                case "job": return Result(store.GetJob(id));
                case "submit": return Accepted(store.Submit(await Body<JobSpec>()));
                case "cancel": return Result(store.Cancel(id));
                case "tracks": return Result(store.Tracks());
                case "track": return Result(store.GetTrack(id));
                case "importSet": return Result(await CompositionFiles.Import(request.Body,ct));
                case "packSet": return new(200,Body:CompositionFiles.Pack(await Body<CompositionDocument>()),ContentType:"application/zip");
                case "trackSet": return new(200,Body:CompositionFiles.TrackSet(store.GetTrack(id),store.GetJob(id)),ContentType:"application/zip");
                case "material":
                    var file=request.RouteValues["name"];
                    if (!CompositionFiles.Materials.Contains(file)) return new(404);
                    var material=Path.Combine(Path.GetDirectoryName(store.GetTrack(id).file_path)!,file);
                    return File.Exists(material)?new(200,Body:File.OpenRead(material),ContentType:file=="generation.json"?Json:"text/plain; charset=utf-8"):new(404);
                case "audio": return new(200,Body:store.OpenTrackAudio(id,ct),ContentType:"audio/wav",EnableRanges:true);
                case "waveform":
                    double? Seconds(string name)
                    {
                        if (!request.Query.TryGetValue(name,out var value)) return null;
                        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var t) && double.IsFinite(t)
                            ? t : throw new ArgumentException("表示範囲を秒数で指定してください。");
                    }
                    return Result(await Waveform.CachedAsync(store.GetTrack(id).file_path,Seconds("start") ?? 0,Seconds("end"),ct));
                case "captions":
                    var path=Path.Combine(Path.GetDirectoryName(store.GetTrack(id).file_path)!,"lyrics.vtt");
                    return File.Exists(path)?new(200,Body:File.OpenRead(path),ContentType:"text/vtt; charset=utf-8"):new(404);
                case "adopt":
                    using (var reader=new StreamReader(request.Body,leaveOpen:true))
                    {
                        var text=await reader.ReadToEndAsync(ct);
                        var review=string.IsNullOrWhiteSpace(text)?null:JsonSerializer.Deserialize<Review>(text,JsonSerializerOptions.Web);
                        return Result(store.SetReview(id,review??new Review("adopted")));
                    }
                case "nodes": return Result(store.Nodes());
                case "works": return Result(store.Works());
                case "work":
                    var name=request.RouteValues["name"];
                    return Accepted(store.Submit(new("edit",theme:name+" の使用曲",source:(await Body<WorkTrack>()).track,work:name,assign_only:true)));
                case "ask": return Result(store.Ask(await Body<StudioQuestion>()));
                case "consultation": return Result(store.ConsultationById(id));
                case "decide": return Result(store.Decide(id,(await Body<StudioDecision>()).decision));
                case "demand": return Result(new {pending=store.Pending(request.RouteValues["node"])});
                case "poll": return Result(store.Poll(await Body<NodeReport>(),bool.TryParse(request.Query.GetValueOrDefault("claim"),out var claim)&&claim));
                case "progress": store.Progress(id,await Body<Update>());return new(200);
                case "assign": store.Assign(id,request.Query.GetValueOrDefault("lease")??"");return new(200);
                case "finish":
                    var lease = request.Query.GetValueOrDefault("lease") ?? "";
                    if (request.Query.ContainsKey("offset") || request.Query.ContainsKey("length"))
                    {
                        if (!long.TryParse(request.Query.GetValueOrDefault("offset"),out var offset) ||
                            !long.TryParse(request.Query.GetValueOrDefault("length"),out var length))
                            throw new InvalidDataException("音声の転送サイズを確認してください。");
                        if (!await Delivery.ReceivePart(id,lease,offset,length,request.Body,store,ct))
                            return new(202);
                    }
                    else await Delivery.Receive(id,lease,request.Body,store,ct);
                    return Result(new {track=id});
                default: return new(404);
            }
        }
        catch (KeyNotFoundException) { return Result(new {error="曲または予約が見つかりません。"},404); }
        catch (InvalidOperationException e) { return Result(new {error=e.Message},409); }
        catch (Exception e) when (e is ArgumentException or InvalidDataException or JsonException or EndOfStreamException)
        {
            if (operationId=="finish")
            {
                log.LogWarning("Invalid delivery for job {JobId} ({ErrorType})",id,e.GetType().Name);
                try { store.Progress(id,new(request.Query.GetValueOrDefault("lease")??"","failed",0,"成果物の形式を確認してください。")); }
                catch (InvalidOperationException) { }
            }
            return Result(new {error=e.Message},400);
        }
    }
}
