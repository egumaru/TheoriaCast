using System.Text.Json;
using System.Text.Json.Serialization;

namespace TheoriaCast.Visual;

public static class VisualProtocol
{
    public const string Service = "TheoriaCast.Visual.Api";
    public const string Prefix = "/api/v1/visual";
    public const int MaxImageBytes = 16 * 1024 * 1024;
    public const int ChunkBytes = 384 * 1024;
    public const int MaxBatch = 200;
}

public sealed record AssetRef(string ServerId, string AssetId, string Sha256);
public sealed record VisualAsset(AssetRef Reference, string Name, string MediaType, int Width, int Height,
    DateTimeOffset CreatedAt, string? RecipeId = null, DateTimeOffset? ImportedAt = null, string? OriginalSha256 = null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] VisualInspection? Inspection = null);
public sealed record AssetSearch(string Query);
public sealed record InspectionFlag(string Code, string Reason);
public sealed record VisualInspection(AssetRef Asset, string RecipeId, DateTimeOffset InspectedAt,
    string[] Tags, string[] ProviderTags, InspectionFlag[] Flags, ModelLicense? ModelLicense = null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] AssetRef? SourceAsset = null);
public sealed record ImageChunk(byte[] Bytes);
public sealed record StagedImageChunk(string Sha256);
public sealed record ImportImage(string Name, string Sha256, string[]? Chunks = null, byte[]? Bytes = null);
public sealed record ImageSize(int Width, int Height);
public sealed record MarginOffsets(double Left = 0, double Top = 0, double Right = 0, double Bottom = 0);

public sealed record EditSettings(string Operation, string? Touch = null, double? Strength = null,
    int? Width = null, int? Height = null, string? Background = null, string? Color = null,
    AssetRef? BackgroundAsset = null, string? Prompt = null, int? Seed = null, int? Resolution = null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] string? FitMode = null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] string? PadStyle = null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] string? PadColor = null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] MarginOffsets? MarginOffsets = null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] MarginOffsets? SafeAreaMargin = null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] AssetRef? ReferenceAsset = null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] double? ColorMatchStrength = null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] string? InspectionMode = null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] int? MaskRefineRadius = null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] int? MaskErodePx = null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] bool? DecontaminateEdge = null);
public sealed record EditBatch(EditSettings Settings, AssetRef[]? Inputs = null, string? SetId = null, string? Hardware = null);
public sealed record RecipeChange(string Parameter, JsonElement Value);
public sealed record ReplayRequest(string RecipeId, RecipeChange? Change = null, string? SetId = null);
public sealed record PreviewSweep(string SetId, AssetRef[] Representatives, string[]? Touches = null,
    double[]? Strengths = null, string? Hardware = null);
public sealed record ProcessingEngine(string Algorithm, string Version, string Hardware,
    IReadOnlyDictionary<string, string> Libraries, IReadOnlyDictionary<string, string> Models,
    IReadOnlyDictionary<string, string> FixedParameters);
public sealed record VisualRecipe(string Id, string? ParentId, RecipeChange? Change, DateTimeOffset CreatedAt,
    AssetRef Input, EditSettings Settings, ProcessingEngine Engine, string? EffectivePrompt = null)
{
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]
    public ImageSize? TargetResolution => Settings.FitMode is not null && Settings.Width is int width && Settings.Height is int height
        ? new(width,height) : null;
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]
    public string? TargetAspect
    {
        get
        {
            if (TargetResolution is not {} size) return null;
            int a=size.Width, b=size.Height;
            while (b>0) (a,b)=(b,a%b);
            return FormattableString.Invariant($"{size.Width/a}:{size.Height/a}");
        }
    }
}
public sealed record ResourceEstimate(string Hardware, string Model, double MinutesPerImage, int ImagesPerNight,
    double BatchMinutes, bool ExceedsNight, string? Alternative);
public sealed record JobItem(string RecipeId, string Status = "queued", AssetRef? Output = null,
    string? Error = null, DateTimeOffset? FinishedAt = null, string? ExecutedOn = null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] VisualInspection? Inspection = null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] ProcessingEngine? PinnedEngine = null,
    int Attempts = 0);
public sealed record VisualJob(string Id, DateTimeOffset CreatedAt, string Status, ResourceEstimate Estimate, JobItem[] Items,
    string? SourceSetId = null, string? ParentJobId = null, string? Kind = null);
public sealed record ComputeResource(string ServerId, string Name, string Hardware, string Algorithm, bool Matting,
    double FluxSecondsAt256, int LogicalProcessors, bool CosmosInspection = false, bool MatteEdgeRefinement = false);
public sealed record GpuWork(VisualRecipe Recipe, byte[] Input);

public sealed record SetItem(AssetRef Asset, string Decision = "pending");
public sealed record SetCut(AssetRef Asset, double? Seconds = null, string? Note = null, int? LyricLine = null);
public sealed record ModelLicense(string File, string Sha256, string License, string Source);
public sealed record ManifestCut(int Order, string Path, AssetRef Asset, string Name, double? Seconds,
    string? Note, int? LyricLine, VisualRecipe[] Recipes, ModelLicense[] ModelLicenses);
public sealed record VisualSetManifest(int SchemaVersion, string SetId, string Name, int Version,
    DateTimeOffset ApprovedAt, ManifestCut[] Cuts);
public sealed record AssetSet(string Id, string Name, int Version, string State, SetItem[] Items,
    DateTimeOffset? ApprovedAt = null, VisualSetManifest? Manifest = null);
public sealed record AvailableSetItem(AssetRef Asset, string Decision, string Availability, string? Name, string? RecipeId);
public sealed record AssetSetView(string Id, string Name, int Version, string State, AvailableSetItem[] Items,
    DateTimeOffset? ApprovedAt);
public sealed record CreateSet(string Name, AssetRef[]? Assets = null, string? JobId = null);
public sealed record ReviewSet(AssetRef Asset, string Decision, int Version);
public sealed record ApproveSet(int Version, SetCut[]? Cuts = null);

public interface IVisualLibrary
{
    StagedImageChunk Stage(ImageChunk chunk);
    Task<VisualAsset> Import(ImportImage request, CancellationToken ct);
    Task<VisualImageChunk> ReadImageChunk(AssetRef reference, long offset, int count, CancellationToken ct);
    VisualAsset[] Assets();
    VisualAsset[] Search(AssetSearch request);
    VisualAsset Asset(string id);
    VisualRecipe Recipe(string id);
    Task<VisualRecipe> AssetRecipe(AssetRef reference, CancellationToken ct);
    Task<ResourceEstimate> Estimate(EditBatch request, CancellationToken ct);
    Task<VisualJob> Submit(EditBatch request, CancellationToken ct);
    Task<VisualJob> Sweep(PreviewSweep request, CancellationToken ct);
    Task<VisualJob> Replay(ReplayRequest request, CancellationToken ct);
    VisualJob[] Jobs();
    VisualJob Job(string id);
    Task<ComputeResource[]> Resources(CancellationToken ct);
    AssetSet[] Sets();
    Task<AssetSetView> Set(string id, CancellationToken ct);
    Task<AssetSetView> CreateSet(CreateSet request, CancellationToken ct);
    Task<AssetSetView> Review(string id, ReviewSet request, CancellationToken ct);
    Task<AssetSetView> Approve(string id, ApproveSet request, CancellationToken ct);
    Task<VisualSetManifest> Manifest(string id, CancellationToken ct);
}

public sealed record VisualImageChunk(long Offset, long TotalBytes, string Sha256, string MediaType, byte[] Bytes);

public sealed class VisualConflict(string message) : Exception(message);
