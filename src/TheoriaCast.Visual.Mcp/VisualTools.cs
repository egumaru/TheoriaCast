using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using AIKernel.Abstractions.Hosting;
using AIKernel.Dtos.Hosting;
using Microsoft.Extensions.AI;

namespace TheoriaCast.Visual.Mcp;

public sealed class VisualTools : IMcpToolProvider
{
    private readonly IVisualLibrary library;
    private readonly IKaraokeLibrary karaoke;
    private readonly IReadOnlyDictionary<string, AIFunction> functions;

    public VisualTools(IVisualLibrary library, IKaraokeLibrary karaoke)
    {
        this.library = library;
        this.karaoke = karaoke;
        functions = typeof(VisualTools).GetMethods()
            .Where(method => method.IsDefined(typeof(DisplayNameAttribute)))
            .Select(method => AIFunctionFactory.Create(method, this, new AIFunctionFactoryOptions
            {
                Name = method.GetCustomAttribute<DisplayNameAttribute>()!.DisplayName,
                Description = method.GetCustomAttribute<DescriptionAttribute>()!.Description,
                SerializerOptions = JsonSerializerOptions.Web
            })).ToDictionary(function => function.Name, StringComparer.Ordinal);
    }

    public IEnumerable<McpOperation> Tools => functions.Values.Select(function =>
        new McpOperation(function.Name, function.Description ?? "", function.JsonSchema, function.Name, "client"));

    public async ValueTask<JsonElement> CallAsync(string operationId, JsonElement input, RequestIdentity caller, CancellationToken ct)
    {
        var args = new AIFunctionArguments();
        foreach (var property in input.EnumerateObject()) args[property.Name] = property.Value;
        return JsonSerializer.SerializeToElement(await functions[operationId].InvokeAsync(args, ct), JsonSerializerOptions.Web);
    }

    [DisplayName("visual_stage_image_chunk"), Description("Stage a base64-encoded PNG or JPEG chunk of up to 384 KiB. Keep returned SHA-256 values in order. Staged chunks expire after 24 hours.")]
    public StagedImageChunk Stage(ImageChunk chunk) => library.Stage(chunk);

    [DisplayName("visual_stage_karaoke_audio"), Description("Stage up to 384 KiB of existing Music WAV audio as a base64 chunk. Keep returned SHA-256 values in order.")]
    public StagedImageChunk StageKaraoke(ImageChunk chunk) => karaoke.Stage(chunk);

    [DisplayName("visual_submit_karaoke"), Description("Queue bars, score or standalone lyrics. Use Music guide start/duration/midi, lyric cue start/end/text in seconds, and WAV SHA-256 plus ordered staged chunks. Lyrics can use preview, underline or follow.")]
    public Task<KaraokeJob> SubmitKaraoke(KaraokeRequest request, CancellationToken cancellationToken) => karaoke.Submit(request, cancellationToken);

    [DisplayName("visual_list_karaoke_jobs"), Description("List karaoke video jobs on the selected server.")]
    public KaraokeJob[] KaraokeJobs() => karaoke.Jobs();

    [DisplayName("visual_get_karaoke_job"), Description("Poll the video job and retrieve its SHA-256 and byte count after completion.")]
    public KaraokeJob KaraokeJob(string id) => karaoke.Job(id);

    [DisplayName("visual_get_karaoke_recipe"), Description("Read the frozen Music inputs, drawing options, encoder and font fingerprints for a karaoke video.")]
    public KaraokeRecipe KaraokeRecipe(string id) => karaoke.Recipe(id);

    [DisplayName("visual_replay_karaoke"), Description("Regenerate a karaoke video from its stored recipe and verified audio.")]
    public KaraokeJob ReplayKaraoke(string id) => karaoke.Replay(id);

    [DisplayName("visual_get_karaoke_frame"), Description("Inspect the zero-based frame plan, exact time, playhead, notes and synchronized lyric states.")]
    public KaraokeFrame KaraokeFrame(string id, int frame) => karaoke.Frame(id, frame);

    [DisplayName("visual_read_karaoke_video"), Description("Read a completed MP4 or transparent MOV in binary chunks of up to 384 KiB. Start at offset 0 and increment by the decoded byte count.")]
    public Task<KaraokeVideoChunk> ReadKaraoke(string id, long offset, int count, CancellationToken cancellationToken) =>
        karaoke.ReadVideo(id, offset, count, cancellationToken);

    [DisplayName("visual_import_asset"), Description("Import PNG or JPEG with the SHA-256 of the original bytes. Supply either ordered staged chunk hashes or inline base64 up to 384 KiB.")]
    public Task<VisualAsset> Import(ImportImage request, CancellationToken cancellationToken) => library.Import(request, cancellationToken);

    [DisplayName("visual_list_assets"), Description("List assets on this server with server ID, asset ID, SHA-256 and recipe ID.")]
    public VisualAsset[] Assets() => library.Assets();

    [DisplayName("visual_search_assets"), Description("Find local assets by filename or visual inspection tags, including Japanese tags. Each search term must match.")]
    public VisualAsset[] Search(AssetSearch request) => library.Search(request);

    [DisplayName("visual_get_asset"), Description("Inspect an asset by ID on the selected server, including provenance and recipe ID.")]
    public VisualAsset Asset(string id) => library.Asset(id);

    [DisplayName("visual_read_image"), Description("Read PNG or JPEG bytes for an asset reference. Start at offset 0, request up to 393216 bytes, append decoded bytes through totalBytes, and verify the SHA-256.")]
    public Task<VisualImageChunk> ReadImage(AssetRef reference, long offset, int count, CancellationToken cancellationToken) =>
        library.ReadImageChunk(reference, offset, count, cancellationToken);

    [DisplayName("visual_estimate_batch"), Description("Estimate time and compute resources for an edit batch before queueing it.")]
    public Task<ResourceEstimate> Estimate(EditBatch request, CancellationToken cancellationToken) => library.Estimate(request, cancellationToken);

    [DisplayName("visual_submit_batch"), Description("Queue up to 200 assets or one asset set. Background editing accepts maskRefineRadius (0–32 px), maskErodePx (0–2 px), and decontaminateEdge. Resize supports crop/pad and asymmetric margins, color matching takes a fixed reference and strength, and inspection adds advisory flags and tags.")]
    public Task<VisualJob> Submit(EditBatch request, CancellationToken cancellationToken) => library.Submit(request, cancellationToken);
    [DisplayName("visual_preview_sweep"), Description("Queue a touch × strength matrix for one or two representative assets in a set. Pick a completed result and replay its recipe with setId to process the full set.")]
    public Task<VisualJob> Sweep(PreviewSweep request, CancellationToken cancellationToken) => library.Sweep(request,cancellationToken);

    [DisplayName("visual_list_jobs"), Description("List edit jobs and per-asset result references.")]
    public VisualJob[] Jobs() => library.Jobs();

    [DisplayName("visual_get_job"), Description("Read job progress, per-asset output or error, and processing recipe IDs.")]
    public VisualJob Job(string id) => library.Job(id);

    [DisplayName("visual_get_recipe"), Description("Read frozen processing conditions, input references, model hashes and the parent recipe if present.")]
    public VisualRecipe Recipe(string id) => library.Recipe(id);

    [DisplayName("visual_get_asset_recipe"), Description("Read the frozen recipe from a local or connected server's asset reference, including the processing conditions and parent recipe.")]
    public Task<VisualRecipe> AssetRecipe(AssetRef reference, CancellationToken cancellationToken) => library.AssetRecipe(reference, cancellationToken);

    [DisplayName("visual_replay_recipe"), Description("Queue the same model and conditions again. Optionally vary one parameter. Supply setId to derive a batch from a completed preview or existing set member recipe.")]
    public Task<VisualJob> Replay(ReplayRequest request, CancellationToken cancellationToken) => library.Replay(request, cancellationToken);

    [DisplayName("visual_get_resources"), Description("Read available local and configured GPU resources.")]
    public Task<ComputeResource[]> Resources(CancellationToken cancellationToken) => library.Resources(cancellationToken);

    [DisplayName("visual_create_set"), Description("Create a review set from asset references or from the completed outputs of a job.")]
    public Task<AssetSetView> CreateSet(CreateSet request, CancellationToken cancellationToken) => library.CreateSet(request, cancellationToken);

    [DisplayName("visual_list_sets"), Description("List review and approved asset sets on the selected server.")]
    public AssetSet[] Sets() => library.Sets();

    [DisplayName("visual_get_set"), Description("Check a set version, decisions, recipe IDs and live availability. Unreachable references remain in the result.")]
    public Task<AssetSetView> Set(string id, CancellationToken cancellationToken) => library.Set(id, cancellationToken);

    [DisplayName("visual_review_set_item"), Description("Record an accepted, rejected or pending decision for a reference using the current set version. Approve only at the person's request.")]
    public Task<AssetSetView> Review(string id, ReviewSet request, CancellationToken cancellationToken) => library.Review(id, request, cancellationToken);

    [DisplayName("visual_approve_set"), Description("Freeze the specified reviewed set version. All references must be available and decided, with at least one accepted. Only approve at the person's request.")]
    public Task<AssetSetView> Approve(string id, ApproveSet request, CancellationToken cancellationToken) => library.Approve(id, request, cancellationToken);
    [DisplayName("visual_get_set_manifest"), Description("Read the approved MV cut order, display duration, lyric lines, asset paths, hashes, full recipes and model licenses.")]
    public Task<VisualSetManifest> Manifest(string id,CancellationToken cancellationToken) => library.Manifest(id,cancellationToken);
}
