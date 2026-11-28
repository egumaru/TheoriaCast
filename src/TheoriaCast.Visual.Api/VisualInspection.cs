using System.Globalization;
using System.Text.Json;
using AIKernel.Providers.Image;
using OpenCvSharp;

namespace TheoriaCast.Visual.Api;

internal sealed partial class VisualProcessing
{
    private const string CosmosModel = "NVIDIA Cosmos 3 Edge (4B)";
    private const string CosmosSource = "https://huggingface.co/nvidia/Cosmos3-Edge";
    private Lazy<CosmosImageTagger>? cosmos;
    private string? cosmosRevision;
    private string? cosmosHash;
    public bool HasCosmos => cosmos is not null;
    public string? AutoInspectMode { get; private set; }

    private void ConfigureInspection(IConfiguration config)
    {
        var directory = config["Visual:CosmosModel"];
        cosmosRevision = config["Visual:CosmosRevision"];
        cosmosHash = config["Visual:CosmosManifestSha256"];
        if (directory is not null || cosmosRevision is not null || cosmosHash is not null)
        {
            if (string.IsNullOrWhiteSpace(directory) || cosmosRevision is not { Length: 40 } ||
                cosmosRevision.Any(c => !Uri.IsHexDigit(c)) || cosmosHash is null)
                throw new ArgumentException("Cosmos のディレクトリ、40桁のリビジョン、マニフェストの SHA-256 を設定してください。");
            cosmosHash = VisualStore.CheckHash(cosmosHash);
            cosmosRevision = cosmosRevision.ToLowerInvariant();
            cosmos = new(() =>
            {
                var manifest = File.ReadAllBytes(Path.Combine(directory, "import-manifest.json"));
                if (VisualStore.Hash(manifest) != cosmosHash)
                    throw new VisualConflict("Cosmos マニフェストの SHA-256 が一致しません。");
                using var metadata = JsonDocument.Parse(manifest);
                if (!metadata.RootElement.TryGetProperty("revision", out var revision) || revision.GetString() != cosmosRevision)
                    throw new VisualConflict("Cosmos マニフェストのリビジョンが一致しません。");
                return new CosmosImageTagger(directory, cosmosHash,
                    maximumEncodedBytes: VisualProtocol.MaxImageBytes, maximumPixels: 4 * 1024 * 1024);
            });
        }
        AutoInspectMode = config["Visual:AutoInspect"] ?? (HasCosmos ? "cosmos" : null);
        if (AutoInspectMode is not (null or "off" or "pixels" or "cosmos") || AutoInspectMode == "cosmos" && !HasCosmos)
            throw new ArgumentException("Visual:AutoInspect の設定と Cosmos モデルの登録を確認してください。");
        if (AutoInspectMode == "off") AutoInspectMode = null;
    }

    public ProcessingEngine Engine(EditSettings settings, string hardware, string? inspectionVersion = null)
    {
        var engine = EditRecipes.Engine(settings, hardware, inspectionVersion);
        if (settings.InspectionMode != "cosmos") return engine;
        if (!HasCosmos) throw new VisualConflict("固定済みの Cosmos 3 Edge モデルが登録されていません。");
        return engine with
        {
            Libraries = new Dictionary<string, string>(engine.Libraries) { [EditRecipes.ImageProvider] = EditRecipes.ImageProviderVersion },
            Models = new Dictionary<string, string> { [CosmosModel] = cosmosHash! },
            FixedParameters = new Dictionary<string, string>(engine.FixedParameters)
            {
                ["cosmosRevision"] = cosmosRevision!,
                ["modelSource"] = CosmosSource,
                ["modelLicense"] = "OpenMDW-1.1"
            }
        };
    }

    public async Task<VisualInspection> Inspect(VisualRecipe recipe, byte[] bytes, VisualRecipe? source, CancellationToken ct)
    {
        if (!EditRecipes.Matches(recipe.Engine, Engine(recipe.Settings, "cpu", recipe.Engine.Version), recipe.Settings))
            throw new VisualConflict("登録済みの検品モデルと固定レシピが一致しません。");
        var detailed = recipe.Engine.Version == EditRecipes.InspectionEngineVersion;
        var (tags, flags) = PixelInspection(bytes, source?.Settings, detailed);
        string[] providerTags = [];
        ModelLicense? license = null;
        if (recipe.Settings.InspectionMode == "cosmos")
        {
            providerTags = (await cosmos!.Value.TagAsync(bytes, ct)).ToArray();
            tags.AddRange(providerTags.SelectMany(tag => new[] { tag }.Concat(tag.Split(' ', StringSplitOptions.RemoveEmptyEntries)))
                .Select(tag => JapaneseTags.GetValueOrDefault(tag)).OfType<string>());
            license = new(CosmosModel, recipe.Engine.Models[CosmosModel], "OpenMDW-1.1",
                CosmosSource + "/tree/" + recipe.Engine.FixedParameters["cosmosRevision"]);
        }
        return new(recipe.Input, recipe.Id, DateTimeOffset.UtcNow, tags.Distinct().ToArray(), providerTags, flags, license,
            detailed ? source?.Input : null);
    }

    private static readonly Dictionary<string, string> JapaneseTags = new(StringComparer.OrdinalIgnoreCase)
    {
        ["person"] = "人物", ["people"] = "人物", ["woman"] = "女性", ["man"] = "男性", ["child"] = "子供",
        ["cat"] = "猫", ["dog"] = "犬", ["bird"] = "鳥", ["flower"] = "花", ["tree"] = "木", ["car"] = "車",
        ["mountain"] = "山", ["beach"] = "海辺", ["forest"] = "森林", ["street"] = "街路", ["city"] = "都市",
        ["outdoors"] = "屋外", ["indoors"] = "屋内", ["sky"] = "空", ["ocean"] = "海", ["river"] = "川",
        ["portrait"] = "ポートレート", ["close-up"] = "近景", ["landscape"] = "風景", ["smile"] = "笑顔",
        ["smiling"] = "笑顔", ["laughing"] = "笑い顔", ["serious"] = "真剣な表情", ["red"] = "赤",
        ["blue"] = "青", ["green"] = "緑", ["yellow"] = "黄", ["white"] = "白", ["black"] = "黒",
        ["orange"] = "橙", ["purple"] = "紫", ["astronaut"] = "宇宙飛行士", ["spacesuit"] = "宇宙服",
        ["space suit"] = "宇宙服", ["helmet"] = "ヘルメット", ["building"] = "建物"
    };

    private static (List<string> Tags, InspectionFlag[] Flags) PixelInspection(byte[] bytes, EditSettings? source, bool detailed)
    {
        using var decoded = VisualImages.Decode(bytes);
        using var bgra = VisualImages.ToBgra(decoded);
        using var image = new Mat();
        var scale = Math.Min(1, 512d / Math.Max(bgra.Width, bgra.Height));
        Cv2.Resize(bgra, image, new Size(Math.Max(1, (int)Math.Round(bgra.Width * scale)),
            Math.Max(1, (int)Math.Round(bgra.Height * scale))), interpolation: InterpolationFlags.Area);
        using var gray = new Mat();
        using var alpha = new Mat();
        using var visible = new Mat();
        using var highlights = new Mat();
        using var edges = new Mat();
        Cv2.CvtColor(image, gray, ColorConversionCodes.BGRA2GRAY);
        Cv2.ExtractChannel(image, alpha, 3);
        Cv2.Threshold(alpha, visible, 127, 255, ThresholdTypes.Binary);
        Cv2.Threshold(gray, highlights, 249, 255, ThresholdTypes.Binary);
        Cv2.BitwiseAnd(highlights, visible, highlights);
        Cv2.Canny(gray, edges, 70, 140);
        Cv2.BitwiseAnd(edges, visible, edges);
        var count = Cv2.CountNonZero(visible);
        var coverage = count / (double)(image.Width * image.Height);
        var clipped = Cv2.CountNonZero(highlights) / (double)Math.Max(1, count);
        var density = Cv2.CountNonZero(edges) / (double)Math.Max(1, count);
        var color = Cv2.Mean(image, visible);
        var brightness = Cv2.Mean(gray, visible).Val0;
        var tags = new List<string> { decoded.Width == decoded.Height ? "正方形" : decoded.Width > decoded.Height ? "横長" : "縦長" };
        var flags = new List<InspectionFlag>();
        if (count > 0)
        {
            tags.Add(brightness >= 170 ? "明るい" : brightness <= 80 ? "暗い" : "中間調");
            if (color.Val2 - color.Val0 >= 15) tags.Add("暖色");
            if (color.Val0 - color.Val2 >= 15) tags.Add("寒色");
        }
        if (coverage < 1) tags.Add("透過");
        if (source?.Operation == "background" && source.Background == "transparent" || coverage < 1)
        {
            if (coverage < .03) flags.Add(new("matte-missing", "不透明領域が " + Percent(coverage) + " です。被写体の欠損を確認してください。"));
            if (coverage > .97) flags.Add(new("matte-residue", "不透明領域が " + Percent(coverage) + " です。背景の残りを確認してください。"));
        }
        if (clipped > .30) flags.Add(new("highlight-clipping", "白に近い画素が " + Percent(clipped) + " です。白飛びを確認してください。"));
        if (source?.Operation == "stylize" && density < .006)
            flags.Add(new("low-edge-density", "輪郭画素が " + Percent(density) + " です。細部の潰れを確認してください。"));
        if (detailed && coverage is >= .03 and <= .97)
            InspectMatteTopology(visible, count, flags);
        return (tags, flags.ToArray());
    }

    private static string Percent(double value) => value.ToString("P1", CultureInfo.GetCultureInfo("ja-JP"));
}
