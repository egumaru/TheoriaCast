using System.Text.Json;
using AIKernel.Abstractions.Hosting;
using AIKernel.Dtos.Hosting;

namespace TheoriaCast.Visual.Api;

internal sealed class VisualEndpoints(VisualLibrary library) : IApiEndpointProvider
{
    private const string Json = "application/json";
    private const string Prefix = VisualProtocol.Prefix;

    public IEnumerable<ApiEndpoint> Endpoints =>
    [
        Get("/assets", "assets"),
        Get("/assets/{id}", "asset"),
        Get("/assets/{id}/image", "image"),
        Get("/asset-refs/{server}/{id}/{hash}/image", "referenceImage"),
        Get("/asset-refs/{server}/{id}/{hash}/recipe", "referenceRecipe"),
        Post("/assets/chunks", "stage"),
        Post("/assets/import", "import"),
        Post("/assets/search", "search"),
        Get("/jobs", "jobs"),
        Get("/jobs/{id}", "job"),
        Post("/jobs/estimate", "estimate"),
        Post("/jobs", "submit"),
        Post("/jobs/preview-sweep", "sweep"),
        Get("/recipes/{id}", "recipe"),
        Post("/recipes/replay", "replay"),
        Get("/nodes", "nodes"),
        Get("/nodes/local", "localGpu"),
        Post("/nodes/execute", "gpu"),
        Get("/sets", "sets"),
        Get("/sets/{id}", "set"),
        Get("/sets/{id}/manifest", "manifest"),
        Get("/sets/{id}/export", "setExport"),
        Post("/sets", "createSet"),
        Post("/sets/{id}/review", "review"),
        Post("/sets/{id}/approve", "approve")
    ];

    private static ApiEndpoint Get(string path, string id) => new("GET", Prefix + path, id, "client");
    private static ApiEndpoint Post(string path, string id) => new("POST", Prefix + path, id, "client", Json);
    private static ApiResponse Result(object value, int status = 200) =>
        new(status, JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web));
    private static ApiResponse Accepted(VisualJob job) =>
        Result(job, 202) with { Location = Prefix + "/jobs/" + job.Id };

    public async ValueTask<ApiResponse> InvokeAsync(string operationId, ApiInvocation request, CancellationToken ct)
    {
        var id = request.RouteValues.GetValueOrDefault("id") ?? "";
        async ValueTask<T> Body<T>() => await JsonSerializer.DeserializeAsync<T>(request.Body, JsonSerializerOptions.Web, ct)
            ?? throw new InvalidDataException("リクエストが空です。");
        try
        {
            switch (operationId)
            {
                case "assets": return Result(library.Assets());
                case "search": return Result(library.Search(await Body<AssetSearch>()));
                case "asset": return Result(library.Asset(id));
                case "image":
                    var asset = library.Asset(id);
                    return new(200, Body: library.OpenImage(asset), ContentType: asset.MediaType);
                case "referenceImage":
                    var bytes = await library.ReadImage(new(request.RouteValues["server"], id, request.RouteValues["hash"]), ct);
                    return new(200, Body: new MemoryStream(bytes, writable: false), ContentType: VisualImages.MediaType(bytes));
                case "referenceRecipe":
                    return Result(await library.AssetRecipe(new(request.RouteValues["server"], id, request.RouteValues["hash"]), ct));
                case "stage": return Result(library.Stage(await Body<ImageChunk>()));
                case "import": return Result(await library.Import(await Body<ImportImage>(), ct), 201);
                case "jobs": return Result(library.Jobs());
                case "job": return Result(library.Job(id));
                case "estimate": return Result(await library.Estimate(await Body<EditBatch>(), ct));
                case "submit": return Accepted(await library.Submit(await Body<EditBatch>(), ct));
                case "sweep": return Accepted(await library.Sweep(await Body<PreviewSweep>(), ct));
                case "recipe": return Result(library.Recipe(id));
                case "replay": return Accepted(await library.Replay(await Body<ReplayRequest>(), ct));
                case "nodes": return Result(await library.Resources(ct));
                case "localGpu": return Result(library.LocalGpu());
                case "gpu":
                    var image = await library.ExecuteGpu(await Body<GpuWork>(), ct);
                    return new(200, Body: new MemoryStream(image, writable: false), ContentType: "image/png");
                case "sets": return Result(library.Sets());
                case "set": return Result(await library.Set(id, ct));
                case "manifest": return Result(await library.Manifest(id,ct));
                case "setExport": return new(200, Body:await library.ExportSet(id,ct),ContentType:"application/zip");
                case "createSet": return Result(await library.CreateSet(await Body<CreateSet>(), ct), 201);
                case "review": return Result(await library.Review(id, await Body<ReviewSet>(), ct));
                case "approve": return Result(await library.Approve(id, await Body<ApproveSet>(), ct));
                default: return new(404);
            }
        }
        catch (KeyNotFoundException) { return Result(new { error = "指定されたデータが見つかりません。" }, 404); }
        catch (VisualConflict e) { return Result(new { error = e.Message }, 409); }
        catch (JsonException) { return Result(new { error = "入力 JSON の形式を確認してください。" }, 400); }
        catch (InvalidDataException e) { return Result(new { error = e.Message }, 400); }
        catch (ArgumentException) { return Result(new { error = "入力画像または編集パラメータを確認してください。" }, 400); }
    }
}
