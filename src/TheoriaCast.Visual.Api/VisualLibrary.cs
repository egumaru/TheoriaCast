using AIKernel.Abstractions.Hosting;

namespace TheoriaCast.Visual.Api;

internal sealed partial class VisualLibrary(VisualStore store, VisualProcessing processing, VisualPeers peers,
    IApplicationIdentity identity, VisualHost host) : IVisualLibrary
{
    private readonly VisualWorkQueue queue = new();
    private string ServerId => identity.ServerId;

    public StagedImageChunk Stage(ImageChunk chunk) => store.Stage(chunk);

    public async Task<VisualAsset> Import(ImportImage request, CancellationToken ct)
    {
        var name = Name(Path.GetFileName(request.Name));
        var hash = VisualStore.CheckHash(request.Sha256);
        var bytes = await store.ImportBytes(request, ct);
        if (VisualStore.Hash(bytes) != hash) throw new ArgumentException("元ファイルの SHA-256 が一致しません。");
        using var image = VisualImages.Decode(bytes);
        var now = DateTimeOffset.UtcNow;
        var asset = new VisualAsset(new(ServerId, VisualStore.NewId(), hash), name, VisualImages.MediaType(bytes),
            image.Width, image.Height, now, ImportedAt: now, OriginalSha256: hash);
        store.SaveImage(hash, bytes);
        return store.Update(s =>
        {
            var previous = s.Assets.FirstOrDefault(a => a.OriginalSha256 == hash && a.Name == name);
            if (previous is not null) return (s, previous);
            var state = s with { Assets = [.. s.Assets, asset] };
            if (processing.AutoInspectMode is { } mode)
            {
                var settings = new EditSettings("inspect", InspectionMode: mode);
                var engine = processing.Engine(settings, "cpu");
                var recipe = new VisualRecipe(VisualStore.NewId(), null, null, now, asset.Reference, settings, engine);
                var estimate = EstimateFor(settings, engine, LocalCpu(), 1);
                var job = PrepareJob([recipe], estimate, kind: "automaticInspection");
                store.SaveRecipe(recipe);
                state = state with { Recipes = [.. state.Recipes, recipe], Jobs = [.. state.Jobs, job] };
            }
            return (state, asset);
        });
    }

    private static string Name(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200 || name.Any(char.IsControl))
            throw new ArgumentException("名前を1〜200文字で指定してください。");
        return name.Trim();
    }

    public VisualAsset[] Assets()
    {
        var state = store.Read();
        var inspections = state.Jobs.SelectMany(job => job.Items ?? []).OfType<JobItem>().Select(item => item.Inspection)
            .OfType<VisualInspection>().GroupBy(result => result.Asset)
            .ToDictionary(results => results.Key, results => results.MaxBy(result => result.InspectedAt)!);
        return state.Assets.Reverse().Select(asset => asset with { Inspection = inspections.GetValueOrDefault(asset.Reference) }).ToArray();
    }

    public VisualAsset[] Search(AssetSearch request)
    {
        if (request.Query is null || request.Query.Length > 160) throw new ArgumentException("検索語は160文字までです。");
        var terms = request.Query.Split((char[]?)null, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return Assets().Where(asset => terms.All(term => asset.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            asset.Inspection?.Tags.Any(tag => tag.Contains(term, StringComparison.OrdinalIgnoreCase)) == true ||
            asset.Inspection?.ProviderTags.Any(tag => tag.Contains(term, StringComparison.OrdinalIgnoreCase)) == true)).ToArray();
    }

    public VisualAsset Asset(string id)
    {
        var asset = StoredAsset(id);
        var inspection = store.Read().Jobs.SelectMany(job => job.Items ?? []).OfType<JobItem>().Select(item => item.Inspection)
            .OfType<VisualInspection>().Where(result => result.Asset == asset.Reference).MaxBy(result => result.InspectedAt);
        return asset with { Inspection = inspection };
    }

    private VisualAsset StoredAsset(string id)
    {
        var asset = Find(store.Read().Assets, id, a => a.Reference.AssetId);
        if (!File.Exists(store.ImagePath(asset.Reference.Sha256))) throw new KeyNotFoundException();
        return asset;
    }

    public Stream OpenImage(VisualAsset asset) => File.OpenRead(store.ImagePath(asset.Reference.Sha256));

    public VisualJob[] Jobs() => store.Read().Jobs.Reverse().ToArray();
    public VisualJob Job(string id) => Find(store.Read().Jobs, id, j => j.Id);
    public VisualRecipe Recipe(string id) => Find(store.Read().Recipes, id, r => r.Id);
    public async Task<VisualRecipe> AssetRecipe(AssetRef reference, CancellationToken ct)
    {
        reference = VisualStore.CheckRef(reference);
        var asset = await Resolve(reference, ct) ?? throw new VisualConflict("参照先の素材を確認できません。");
        if (asset.RecipeId is not { } id) throw new KeyNotFoundException();
        if (reference.ServerId == ServerId) return Recipe(id);
        return await peers.Recipe(reference.ServerId, id, ct) ?? throw new VisualConflict("参照先のレシピを確認できません。");
    }
    public AssetSet[] Sets() => store.Read().Sets.Reverse().ToArray();
    private AssetSet StoredSet(string id) => Find(store.Read().Sets, id, s => s.Id);

    private static T Find<T>(IEnumerable<T> items, string id, Func<T, string> key) where T : class =>
        items.FirstOrDefault(item => key(item) == id) ?? throw new KeyNotFoundException();

    private async Task<VisualAsset?> Resolve(AssetRef reference, CancellationToken ct)
    {
        if (reference.ServerId != ServerId) return await peers.Asset(reference, ct);
        try
        {
            var asset = StoredAsset(reference.AssetId);
            return asset.Reference == reference ? asset : null;
        }
        catch (KeyNotFoundException) { return null; }
    }

    private async Task RequireAssets(AssetRef[] assets, CancellationToken ct)
    {
        var resolved = await Task.WhenAll(assets.Distinct().Select(asset => Resolve(asset, ct)));
        if (resolved.Contains(null)) throw new VisualConflict("確認不可の素材があります。サーバーへの接続と素材の SHA-256 を確認してください。");
    }

    public async Task<byte[]> ReadImage(AssetRef reference, CancellationToken ct)
    {
        reference = VisualStore.CheckRef(reference);
        byte[] bytes;
        if (reference.ServerId == ServerId)
        {
            if (await Resolve(reference, ct) is null) throw new VisualConflict("入力素材を確認できません。");
            var path = store.ImagePath(reference.Sha256);
            if (new FileInfo(path).Length > VisualProtocol.MaxImageBytes) throw new InvalidDataException("入力画像が 16 MiB を超えました。");
            bytes = await File.ReadAllBytesAsync(path, ct);
        }
        else bytes = await peers.Image(reference, ct);
        if (VisualStore.Hash(bytes) != reference.Sha256) throw new InvalidDataException("参照素材の SHA-256 が一致しません。");
        return bytes;
    }

    public async Task<VisualImageChunk> ReadImageChunk(AssetRef reference, long offset, int count, CancellationToken ct)
    {
        reference = VisualStore.CheckRef(reference);
        if (offset < 0 || count is < 1 or > VisualProtocol.ChunkBytes)
            throw new ArgumentException("画像チャンクの位置またはサイズが範囲外です。");
        if (reference.ServerId != ServerId)
        {
            var bytes = await ReadImage(reference, ct);
            await using var remote = new MemoryStream(bytes, writable: false);
            return await Read(remote, VisualImages.MediaType(bytes));
        }
        var asset = StoredAsset(reference.AssetId);
        if (asset.Reference != reference) throw new VisualConflict("参照素材の SHA-256 が一致しません。");
        await using var local = OpenImage(asset);
        return await Read(local, asset.MediaType);

        async Task<VisualImageChunk> Read(Stream stream, string mediaType)
        {
            var length = stream.Length;
            if (length > VisualProtocol.MaxImageBytes) throw new InvalidDataException("入力画像が 16 MiB を超えました。");
            if (offset > length) throw new ArgumentException("画像チャンクの位置が範囲外です。");
            stream.Position = offset;
            var chunk = new byte[(int)Math.Min(count, length - offset)];
            await stream.ReadExactlyAsync(chunk, ct);
            return new(offset, length, reference.Sha256, mediaType, chunk);
        }
    }

    private ComputeResource LocalCpu() => new(ServerId, host.Name, "cpu", "OpenCV NPR", processing.HasMatting,
        processing.CpuFluxSecondsAt256, Environment.ProcessorCount, processing.HasCosmos, MatteEdgeRefinement: true);

    public ComputeResource LocalGpu() => processing.HasGpu
        ? new(ServerId, host.Name, "gpu", "FLUX.2 [klein] 4B", processing.HasMatting, processing.GpuSecondsAt256,
            Environment.ProcessorCount, MatteEdgeRefinement: true)
        : throw new KeyNotFoundException();

    public async Task<ComputeResource[]> Resources(CancellationToken ct)
    {
        var nodes = new List<ComputeResource> { LocalCpu() };
        if (processing.HasGpu) nodes.Add(LocalGpu());
        nodes.AddRange((await peers.Resources(ct)).Where(node => node.ServerId != ServerId));
        return nodes.ToArray();
    }

    private async Task<ComputeResource> Choose(EditSettings settings, string? hardware, CancellationToken ct,
        bool allowLegacyMatting = false)
    {
        if (hardware is not (null or "auto" or "cpu" or "gpu" or "cloud-gpu"))
            throw new ArgumentException("計算資源は auto、cpu、gpu、cloud-gpu のいずれかです。");
        var eligible = settings.Operation == "stylize" || settings.Operation == "background" && settings.Background == "generated";
        if (!eligible && hardware is ("gpu" or "cloud-gpu"))
            throw new ArgumentException("この操作は CPU で処理します。");
        if (eligible && hardware != "cpu")
        {
            var wanted = hardware == "cloud-gpu" ? "cloud-gpu" : "gpu";
            if (wanted == "gpu" && processing.Supports(settings, "gpu")) return LocalGpu();
            var node = (await peers.Resources(ct)).FirstOrDefault(peer => peer.ServerId != ServerId && peer.Hardware == wanted
                && (settings.Operation != "background" || peer.Matting && (peer.MatteEdgeRefinement || allowLegacyMatting)));
            if (node is not null) return node;
            if (hardware is "gpu" or "cloud-gpu") throw new VisualConflict("指定した GPU で処理できません。計算資源と固定モデルを確認してください。");
        }
        if (!processing.Supports(settings, "cpu"))
            throw new VisualConflict("この処理に必要な固定モデルが CPU に登録されていません。");
        return LocalCpu();
    }

    private (AssetRef[] Inputs, EditSettings Settings) Inputs(EditBatch request)
    {
        if (request.Settings is null || (request.SetId is null) == (request.Inputs is null))
            throw new ArgumentException("編集方法と、素材リストまたは素材セットのいずれかを指定してください。");
        var refs = request.Inputs ?? StoredSet(request.SetId!).Items.Select(item => item.Asset).ToArray();
        if (refs.Length is < 1 or > VisualProtocol.MaxBatch)
            throw new ArgumentException("一括登録できる素材は1〜200件です。");
        return (refs.Select(VisualStore.CheckRef).Distinct().ToArray(), request.Settings);
    }

    private static ResourceEstimate EstimateFor(EditSettings settings, ProcessingEngine engine, ComputeResource resource, int count)
    {
        var seconds = settings.Operation switch
        {
            "resize" or "colorMatch" => 0.5,
            "inspect" => settings.InspectionMode == "cosmos" ? 7 : 1,
            "stylize" when engine.Hardware == "cpu" => 2,
            "background" when settings.Background != "generated" => 12.0 * 4 / Math.Clamp(resource.LogicalProcessors, 1, 8),
            _ => Math.Clamp(resource.FluxSecondsAt256, 1, 3600) * Math.Pow(settings.Resolution!.Value / 256.0, 2)
        };
        int perNight = (int)(TimeSpan.FromHours(8).TotalSeconds / seconds);
        string? alternative = count > perNight
            ? settings.Operation == "stylize" ? "CPU の軽量イラスト化、またはモデル解像度を下げる"
                : "モデル解像度を下げるか、無地または手持ち画像の背景に変更"
            : null;
        return new(engine.Hardware, engine.Algorithm, Math.Round(seconds / 60, 2), perNight,
            Math.Round(count * seconds / 60, 2), count > perNight, alternative);
    }

    public async Task<ResourceEstimate> Estimate(EditBatch request, CancellationToken ct)
    {
        var (inputs, settings) = Inputs(request);
        var resource = await Choose(settings, request.Hardware, ct);
        settings = EditRecipes.Normalize(settings, resource.Hardware);
        return EstimateFor(settings, processing.Engine(settings, resource.Hardware), resource, inputs.Length);
    }

    public async Task<VisualJob> Submit(EditBatch request, CancellationToken ct)
    {
        var (inputs, settings) = Inputs(request);
        if (settings.Operation == "resize" && settings.FitMode is null) settings = settings with { FitMode = "crop" };
        var node = await Choose(settings, request.Hardware, ct);
        settings = EditRecipes.WithMattingDefaults(EditRecipes.Normalize(settings, node.Hardware));
        if (request.SetId is not null && settings.ReferenceAsset is {} reference && !inputs.Contains(reference))
            throw new ArgumentException("基準画像は指定した素材セットから選んでください。");
        await RequireInputs(inputs,settings,ct);
        var engine = processing.Engine(settings, node.Hardware);
        var now = DateTimeOffset.UtcNow;
        var recipes = inputs.Select(input => new VisualRecipe(VisualStore.NewId(), null, null, now, input,
            settings, engine, EditRecipes.EffectivePrompt(settings, engine))).ToArray();
        return Queue(recipes, recipes, EstimateFor(settings, engine, node, inputs.Length), request.SetId,
            kind: settings.Operation == "inspect" ? "inspection" : null);
    }

    private Task RequireInputs(AssetRef[] inputs,EditSettings settings,CancellationToken ct)
    {
        var reference=settings.BackgroundAsset??settings.ReferenceAsset;
        return RequireAssets(reference is null?inputs:[.. inputs,reference],ct);
    }

    public async Task<VisualJob> Sweep(PreviewSweep request,CancellationToken ct)
    {
        var set=StoredSet(request.SetId);
        if (request.Representatives is not {Length:>=1 and <=2})
            throw new ArgumentException("素材セットから代表画像を1〜2枚指定してください。");
        var inputs=request.Representatives.Select(VisualStore.CheckRef).Distinct().ToArray();
        if (inputs.Length!=request.Representatives.Length || inputs.Any(reference=>!set.Items.Any(item=>item.Asset==reference)))
            throw new ArgumentException("代表画像は重複しないセット内の素材を指定してください。");
        var touches=request.Touches??["illustration","pencil","detail"];
        var strengths=request.Strengths??[.3,.6,.9];
        if (touches.Length==0 || strengths.Length==0 || (long)inputs.Length*touches.Length*strengths.Length>VisualProtocol.MaxBatch ||
            touches.Any(touch=>touch is not ("illustration" or "pencil" or "detail")) ||
            touches.Distinct().Count()!=touches.Length || strengths.Distinct().Count()!=strengths.Length)
            throw new ArgumentException("タッチと強さの組み合わせを確認してください。");
        var node=await Choose(new("stylize"),request.Hardware,ct);
        await RequireAssets(inputs,ct);
        var now=DateTimeOffset.UtcNow;
        var recipes=(from input in inputs from touch in touches from strength in strengths
            let settings=EditRecipes.Normalize(new("stylize",touch,strength),node.Hardware)
            let engine=EditRecipes.Engine(settings,node.Hardware)
            select new VisualRecipe(VisualStore.NewId(),null,null,now,input,settings,engine,
                EditRecipes.EffectivePrompt(settings,engine))).ToArray();
        return Queue(recipes,recipes,EstimateFor(recipes[0].Settings,recipes[0].Engine,node,recipes.Length),
            set.Id,kind:"previewSweep");
    }

    public async Task<VisualJob> Replay(ReplayRequest request, CancellationToken ct)
    {
        var original = Recipe(request.RecipeId);
        var hardware = original.Engine.Hardware;
        var normalizeJpeg = EditRecipes.NormalizesJpeg(original.Engine);
        var current = processing.Engine(original.Settings, hardware, original.Engine.Version, normalizeJpeg);
        if (!EditRecipes.Matches(original.Engine, current, original.Settings))
            throw new VisualConflict("固定レシピの処理環境を利用できません。");
        var defaults = EditRecipes.WithMattingDefaults(original.Settings);
        var settings = request.Change is null ? defaults : EditRecipes.Change(defaults, request.Change, hardware);
        var engine = processing.Engine(settings, hardware, original.Engine.Version, normalizeJpeg);
        var runtime = settings.Operation == "resize" ? current with { Algorithm = engine.Algorithm } : current;
        if (!EditRecipes.SameRuntime(runtime, engine) && request.Change?.Parameter != "inspectionMode")
            throw new VisualConflict("変更した条件を同じモデルで処理できません。");
        var exact = request.Change is null && request.SetId is null;
        var node = await Choose(settings, hardware, ct, allowLegacyMatting: exact && EditRecipes.UsesLegacyImageProvider(original.Engine));
        var set=request.SetId is null?null:StoredSet(request.SetId);
        var preview=store.Read().Jobs.FirstOrDefault(job=>job.Kind=="previewSweep" &&
            job.Items.Any(item=>item.RecipeId==original.Id));
        if (set!=null && (preview!=null
            ? preview.SourceSetId!=set.Id || !preview.Items.Any(item=>item.RecipeId==original.Id && item.Status=="done")
            : !set.Items.Any(item=>item.Asset==original.Input)))
            throw new VisualConflict("素材セットに対応する完了済みの試行またはレシピを指定してください。");
        var inputs=set?.Items.Select(item=>item.Asset).ToArray()??[original.Input];
        if (set is not null && settings.ReferenceAsset is { } reference && !inputs.Contains(reference))
            throw new ArgumentException("基準画像は指定した素材セットから選んでください。");
        await RequireInputs(inputs,settings,ct);
        var kind = settings.Operation == "inspect" ? "inspection" : null;
        if (exact)
            return Queue([original], [], EstimateFor(settings, original.Engine, node, 1), kind: kind);
        var now=DateTimeOffset.UtcNow;
        var recipes=inputs.Select(input=>new VisualRecipe(VisualStore.NewId(),original.Id,request.Change,now,
            input,settings,engine,EditRecipes.EffectivePrompt(settings,engine))).ToArray();
        return Queue(recipes,recipes,EstimateFor(settings,engine,node,recipes.Length),set?.Id,preview?.Id,kind);
    }

    private VisualJob Queue(VisualRecipe[] recipes, VisualRecipe[] added, ResourceEstimate estimate,
        string? sourceSetId=null,string? parentJobId=null,string? kind=null)
    {
        var job = PrepareJob(recipes, estimate, sourceSetId, parentJobId, kind);
        foreach (var recipe in added) store.SaveRecipe(recipe);
        return store.Update(s => (s with { Recipes = [.. s.Recipes, .. added], Jobs = [.. s.Jobs, job] }, job));
    }

    private static VisualJob PrepareJob(VisualRecipe[] recipes, ResourceEstimate estimate,
        string? sourceSetId = null, string? parentJobId = null, string? kind = null) =>
        new(VisualStore.NewId(), DateTimeOffset.UtcNow, "queued", estimate,
            recipes.Select(recipe => new JobItem(recipe.Id)).ToArray(), sourceSetId, parentJobId, kind);

    public async Task<bool> RunNext(CancellationToken ct)
    {
        var jobs = store.Read().Jobs.Where(job => VisualWorkQueue.Pending(job.Status)).ToArray();
        var work = (from candidate in jobs
                    from entry in (candidate.Items ?? []).Select((item, index) => (item, index))
                    where entry.item is not null && VisualWorkQueue.Pending(entry.item.Status)
                        && queue.CanRun($"{candidate.Id}/{entry.index}")
                    orderby entry.item.Status == "running", candidate.Kind == "automaticInspection", candidate.CreatedAt
                    select (Job: candidate, Index: entry.index)).FirstOrDefault();
        if (work.Job is null)
        {
            var stale = jobs.FirstOrDefault(job => (job.Items ?? []).All(item => item is null || !VisualWorkQueue.Pending(item.Status))
                && queue.CanRun(job.Id));
            if (stale is null) return false;
            var items = ReadableItems(stale);
            await queue.Process(stale.Id, 0, _ => { },
                () => { SaveJob(stale with { Status = FinishedStatus(items), Items = items }); return Task.CompletedTask; },
                _ => SaveJob(stale with { Status = "failed", Items = items }), "ジョブの完了状態を保存できません。",
                host.Logger, ct);
            return true;
        }

        var (job, index) = work;
        var item = job.Items[index];
        string? nodeId = null;
        void SaveItem(JobItem updated, VisualAsset? asset = null)
        {
            var items = ReadableItems(job);
            items[index] = updated;
            var next = job with { Status = FinishedStatus(items), Items = items };
            SaveJob(next, asset);
            job = next;
            item = updated;
        }

        await queue.Process($"{job.Id}/{index}", item.Attempts,
            attempts => SaveItem(item with { Status = "running", Attempts = attempts }),
            Run,
            error => SaveItem(item with { Status = "failed", Error = error, FinishedAt = DateTimeOffset.UtcNow, ExecutedOn = nodeId }),
            "画像処理に失敗しました。入力、固定モデル、画像処理ランタイムを確認してください。",
            host.Logger, ct);
        return true;

        async Task Run()
        {
            var recipe = Recipe(item.RecipeId);
            VisualAsset? asset = null;
            VisualInspection? inspection = null;
            var node = await Choose(recipe.Settings, recipe.Engine.Hardware, ct,
                allowLegacyMatting: EditRecipes.UsesLegacyImageProvider(recipe.Engine));
            nodeId = node.ServerId;
            if (recipe.Engine.Models.Count > 0)
            {
                var candidate = processing.Engine(recipe.Settings, recipe.Engine.Hardware, recipe.Engine.Version,
                    EditRecipes.NormalizesJpeg(recipe.Engine));
                if (!EditRecipes.Matches(recipe.Engine, candidate, recipe.Settings))
                    throw new VisualConflict("予約時に承認された固定モデルを確認できません。");
                if (recipe.Settings.Operation == "background" && !node.MatteEdgeRefinement)
                    candidate = recipe.Engine;
                if (item.PinnedEngine is { } pinned && !EditRecipes.Matches(pinned, candidate, recipe.Settings))
                    throw new VisualConflict("予約時に承認された固定モデルを確認できません。");
                if (item.PinnedEngine is null)
                    SaveItem(item with { PinnedEngine = candidate });
            }
            var input = await ReadImage(recipe.Input, ct);
            var source = await Resolve(recipe.Input, ct);
            if (recipe.Settings.Operation == "inspect")
            {
                var provenance = source?.RecipeId is not null ? await AssetRecipe(recipe.Input, ct) : null;
                inspection = await processing.Inspect(recipe, input, provenance, ct);
            }
            else
            {
                var background = (recipe.Settings.BackgroundAsset ?? recipe.Settings.ReferenceAsset) is { } reference
                    ? await ReadImage(reference, ct) : null;
                var png = nodeId == ServerId
                    ? await processing.Run(recipe, input, background, ct)
                    : await peers.Execute(node, new(recipe, input), ct);
                if (!VisualImages.IsPng(png)) throw new InvalidDataException("処理結果に PNG の署名がありません。");
                using var image = VisualImages.Decode(png);
                var output = VisualImages.EmbedRecipe(png, recipe);
                var hash = VisualStore.Hash(output);
                var baseName = Path.GetFileNameWithoutExtension(source?.Name ?? "image");
                var name = baseName[..Math.Min(baseName.Length, 140)] + "-" + recipe.Settings.Operation + ".png";
                ct.ThrowIfCancellationRequested();
                store.SaveImage(hash, output);
                asset = new(new(ServerId, VisualStore.NewId(), hash), name, "image/png", image.Width, image.Height,
                    DateTimeOffset.UtcNow, recipe.Id);
            }
            ct.ThrowIfCancellationRequested();
            SaveItem(item with { Status = "done", Output = asset?.Reference,
                FinishedAt = DateTimeOffset.UtcNow, ExecutedOn = nodeId, Inspection = inspection }, asset);
        }
    }

    private static string FinishedStatus(JobItem[] items) =>
        items.Any(entry => VisualWorkQueue.Pending(entry.Status)) ? "running"
            : items.Length == 0 ? "failed"
            : items.All(entry => entry.Status == "done") ? "done"
            : items.All(entry => entry.Status == "failed") ? "failed" : "partial";

    private static JobItem[] ReadableItems(VisualJob job) => (job.Items ?? [])
        .Select(item => item ?? new JobItem("", "failed", Error: "ジョブの項目を読み取れません。")).ToArray();

    private void SaveJob(VisualJob job, VisualAsset? asset = null) =>
        store.Update(s => (s with
        {
            Jobs = s.Jobs.Select(old => old.Id == job.Id ? job : old).ToArray(),
            Assets = asset is null ? s.Assets : [.. s.Assets, asset]
        }, job));

    public async Task<byte[]> ExecuteGpu(GpuWork work, CancellationToken ct)
    {
        var recipe = work.Recipe;
        if (recipe is null || recipe.Engine?.Hardware is not ("gpu" or "cloud-gpu") ||
            !(recipe.Settings?.Operation == "stylize" ||
              recipe.Settings?.Operation == "background" && recipe.Settings.Background == "generated"))
            throw new ArgumentException("GPU 対応のレシピを指定してください。");
        VisualStore.CheckRef(recipe.Input);
        var normalized = EditRecipes.Normalize(recipe.Settings, recipe.Engine.Hardware);
        if (normalized != recipe.Settings || recipe.EffectivePrompt != EditRecipes.EffectivePrompt(normalized, recipe.Engine))
            throw new ArgumentException("固定レシピのパラメータが一致しません。");
        return await processing.Run(recipe, work.Input, null, ct);
    }

    public async Task<AssetSetView> Set(string id, CancellationToken ct) => await View(StoredSet(id), ct);
    private async Task<AssetSetView> View(AssetSet set, CancellationToken ct)
    {
        var items = await Task.WhenAll(set.Items.Select(async item =>
        {
            var asset = await Resolve(item.Asset, ct);
            return new AvailableSetItem(item.Asset, item.Decision, asset is null ? "unavailable" : "available", asset?.Name, asset?.RecipeId);
        }));
        return new(set.Id, set.Name, set.Version, set.State, items, set.ApprovedAt);
    }

    public async Task<AssetSetView> CreateSet(CreateSet request, CancellationToken ct)
    {
        if ((request.Assets is null) == (request.JobId is null))
            throw new ArgumentException("素材リストまたは処理済みバッチを指定してください。");
        AssetRef[] refs;
        if (request.JobId is { } id)
        {
            var job = Job(id);
            if (job.Status is not ("done" or "partial")) throw new VisualConflict("確認できる処理結果がありません。");
            refs = job.Items.Select(item => item.Output).OfType<AssetRef>().ToArray();
        }
        else refs = request.Assets!;
        if (refs.Length is < 1 or > VisualProtocol.MaxBatch) throw new ArgumentException("セットの素材数は1〜200件です。");
        var set = new AssetSet(VisualStore.NewId(), Name(request.Name), 1, "review",
            refs.Select(VisualStore.CheckRef).Distinct().Select(reference => new SetItem(reference)).ToArray());
        store.Update(s => (s with { Sets = [.. s.Sets, set] }, set));
        return await View(set, ct);
    }

    public async Task<AssetSetView> Review(string id, ReviewSet request, CancellationToken ct)
    {
        var reference = VisualStore.CheckRef(request.Asset);
        if (request.Decision is not ("accepted" or "rejected" or "pending"))
            throw new ArgumentException("判定は accepted、rejected、pending のいずれかです。");
        if (request.Decision == "accepted" && await Resolve(reference, ct) is null)
            throw new VisualConflict("確認不可の素材は採用できません。");
        var set = UpdateSet(id, request.Version, current =>
        {
            if (!current.Items.Any(i => i.Asset == reference)) throw new KeyNotFoundException();
            return current with { Version = current.Version + 1,
                Items = current.Items.Select(i => i.Asset == reference ? i with { Decision = request.Decision } : i).ToArray() };
        });
        return await View(set, ct);
    }

    public async Task<AssetSetView> Approve(string id, ApproveSet request, CancellationToken ct)
    {
        var set = StoredSet(id);
        CheckVersion(set, request.Version);
        var view = await View(set, ct);
        if (view.Items.Any(item => item.Decision == "pending" || item.Availability == "unavailable")
            || view.Items.All(item => item.Decision != "accepted"))
            throw new VisualConflict("全素材を確認して判定し、少なくとも1件を採用してから承認してください。");
        var moment=DateTimeOffset.UtcNow;
        var manifest=await FreezeManifest(set,request.Cuts,moment,ct);
        var approved = UpdateSet(id, request.Version, current =>
            current with { State = "approved", ApprovedAt = moment, Manifest=manifest });
        return view with { State = approved.State, ApprovedAt = approved.ApprovedAt };
    }

    private AssetSet UpdateSet(string id, int version, Func<AssetSet, AssetSet> edit) => store.Update(s =>
    {
        var current = Find(s.Sets, id, set => set.Id);
        CheckVersion(current, version);
        var updated = edit(current);
        return (s with { Sets = s.Sets.Select(set => set.Id == id ? updated : set).ToArray() }, updated);
    });

    private static void CheckVersion(AssetSet set, int version)
    {
        if (set.State == "approved") throw new VisualConflict("承認済みの素材セットは版が固定されています。");
        if (set.Version != version) throw new VisualConflict("素材セットが更新されました。最新の版を読み込んでください。");
    }
}

internal sealed record VisualHost(string Name, ILogger Logger);

internal sealed class VisualQueue(VisualLibrary library, VisualStore store, ILogger<VisualQueue> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DateTime nextPrune = DateTime.MinValue;
        return VisualWorkQueue.Run(Next, ct => Task.Delay(TimeSpan.FromSeconds(1), ct), logger, stoppingToken);

        Task<bool> Next(CancellationToken ct)
        {
            if (DateTime.UtcNow < nextPrune) return library.RunNext(ct);
            nextPrune = DateTime.UtcNow.AddHours(1);
            store.PruneChunks();
            return library.RunNext(ct);
        }
    }
}
