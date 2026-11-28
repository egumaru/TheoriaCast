using System.Text.Json;
using AIKernel.Abstractions.Hosting;
using AIKernel.Dtos.Hosting;

namespace TheoriaCast.Visual.Api;

internal sealed class KaraokeEndpoints(KaraokeLibrary library) : IApiEndpointProvider
{
    private const string Prefix = VisualProtocol.Prefix + "/karaoke";
    private const string Json = "application/json";

    public IEnumerable<ApiEndpoint> Endpoints =>
    [
        new("POST", Prefix + "/chunks", "karaokeStage", "client", Json),
        new("POST", Prefix + "/jobs", "karaokeSubmit", "client", Json),
        new("GET", Prefix + "/jobs", "karaokeList", "client"),
        new("GET", Prefix + "/jobs/{id}", "karaokeJob", "client"),
        new("GET", Prefix + "/jobs/{id}/recipe", "karaokeRecipe", "client"),
        new("POST", Prefix + "/jobs/{id}/replay", "karaokeReplay", "client", Json),
        new("GET", Prefix + "/jobs/{id}/frames/{frame}", "karaokeFrame", "client"),
        new("GET", Prefix + "/jobs/{id}/video", "karaokeVideo", "client")
    ];

    private static ApiResponse Result(object value, int status = 200) =>
        new(status, JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web));
    private static ApiResponse Accepted(KaraokeJob job) =>
        Result(job, 202) with { Location = Prefix + "/jobs/" + job.Id };

    public async ValueTask<ApiResponse> InvokeAsync(string operationId, ApiInvocation request, CancellationToken ct)
    {
        var id = request.RouteValues.GetValueOrDefault("id") ?? "";
        async ValueTask<T> Body<T>() => await JsonSerializer.DeserializeAsync<T>(request.Body, JsonSerializerOptions.Web, ct)
            ?? throw new ArgumentException("入力が空です。");
        try
        {
            switch (operationId)
            {
                case "karaokeStage": return Result(library.Stage(await Body<ImageChunk>()));
                case "karaokeSubmit": return Accepted(await library.Submit(await Body<KaraokeRequest>(), ct));
                case "karaokeList": return Result(library.Jobs());
                case "karaokeJob": return Result(library.Job(id));
                case "karaokeRecipe": return Result(library.Recipe(id));
                case "karaokeReplay": return Accepted(library.Replay(id));
                case "karaokeFrame":
                    if (!int.TryParse(request.RouteValues.GetValueOrDefault("frame"), out var frame))
                        throw new ArgumentException("フレーム番号を指定してください。");
                    return Result(library.Frame(id, frame));
                case "karaokeVideo":
                    var output = library.OpenVideo(id);
                    return new(200, Body: output.Stream, ContentType: output.Type);
                default: return new(404);
            }
        }
        catch (KeyNotFoundException) { return Result(new { error = "指定された動画ジョブが見つかりません。" }, 404); }
        catch (VisualConflict ex) { return Result(new { error = ex.Message }, 409); }
        catch (JsonException) { return Result(new { error = "入力 JSON の形式を確認してください。" }, 400); }
        catch (ArgumentException ex) { return Result(new { error = ex.Message }, 400); }
    }
}
