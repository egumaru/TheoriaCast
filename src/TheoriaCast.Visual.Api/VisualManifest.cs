using System.IO.Compression;
using System.Text.Json;

namespace TheoriaCast.Visual.Api;

internal sealed partial class VisualLibrary
{
    public async Task<VisualSetManifest> Manifest(string id,CancellationToken ct)
    {
        var set=StoredSet(id);
        if(set.State!="approved" || set.ApprovedAt is not {} approved)
            throw new VisualConflict("素材セットを承認してからマニフェストを取得してください。");
        return set.Manifest??await FreezeManifest(set,null,approved,ct);
    }

    private async Task<VisualSetManifest> FreezeManifest(AssetSet set,SetCut[]? requested,DateTimeOffset approved,
        CancellationToken ct)
    {
        var accepted=set.Items.Where(item=>item.Decision=="accepted").Select(item=>item.Asset).ToHashSet();
        var cuts=requested??set.Items.Where(item=>item.Decision=="accepted").Select(item=>new SetCut(item.Asset)).ToArray();
        if(cuts.Length!=accepted.Count || cuts.Any(cut=>cut is null ||
            cut.Seconds is double seconds && (!double.IsFinite(seconds) || seconds<=0) ||
            cut.LyricLine is <1 || cut.Note?.Length>2000))
            throw new ArgumentException("採用素材を各1回指定し、秒数、歌詞行番号、メモを確認してください。");
        cuts = cuts.Select(cut => cut with { Asset = VisualStore.CheckRef(cut.Asset) }).ToArray();
        if(cuts.Any(cut => !accepted.Contains(cut.Asset)) ||
            cuts.Select(cut=>cut.Asset).Distinct().Count()!=cuts.Length)
            throw new ArgumentException("採用素材を各1回指定し、秒数、歌詞行番号、メモを確認してください。");
        var items=new List<ManifestCut>(cuts.Length);
        var seen=new Dictionary<AssetRef,VisualRecipe?>();
        foreach(var cut in cuts)
        {
            var asset=await Resolve(cut.Asset,ct)??throw new VisualConflict("カットの素材を確認できません。");
            var chain=new List<VisualRecipe>();
            var sources=new HashSet<AssetRef>();
            var recipes=new HashSet<(string ServerId, string RecipeId)>();
            async Task Trace(AssetRef reference)
            {
                if(!sources.Add(reference)) return;
                if(!seen.TryGetValue(reference,out var recipe))
                {
                    var source=reference==cut.Asset?asset:await Resolve(reference,ct)
                        ??throw new VisualConflict("カットの来歴にある素材を確認できません。");
                    recipe=source.RecipeId is null?null:await AssetRecipe(reference,ct);
                    seen.Add(reference,recipe);
                }
                if(recipe is not null) await TraceRecipe(reference.ServerId, recipe);
            }
            async Task TraceRecipe(string serverId, VisualRecipe recipe)
            {
                if (!recipes.Add((serverId, recipe.Id))) return;
                if (recipe.ParentId is { } parentId)
                {
                    var parent = serverId == ServerId ? Recipe(parentId) : await peers.Recipe(serverId, parentId, ct)
                        ?? throw new VisualConflict("カットの親レシピを確認できません。");
                    await TraceRecipe(serverId, parent);
                }
                await Trace(recipe.Input);
                if(recipe.Settings.BackgroundAsset is {} background) await Trace(background);
                if(recipe.Settings.ReferenceAsset is {} target) await Trace(target);
                chain.Add(recipe);
            }
            await Trace(cut.Asset);
            var order=items.Count+1;
            var extension=asset.MediaType=="image/jpeg"?"jpg":"png";
            var licenses=chain.SelectMany(recipe=>recipe.Engine.Models.Select(License)).Distinct().ToArray();
            items.Add(new(order,FormattableString.Invariant($"images/cut-{order:000}.{extension}"),cut.Asset,asset.Name,
                cut.Seconds,cut.Note,cut.LyricLine,chain.ToArray(),licenses));
        }
        return new(1,set.Id,set.Name,set.Version,approved,items.ToArray());
    }

    private static ModelLicense License(KeyValuePair<string,string> model)
    {
        (string terms,string source)=model.Key switch
        {
            "birefnet-lite-512.onnx"=>("MIT","https://huggingface.co/studioludens/birefnet-lite-512/blob/962e67969ec6acec9c19fab76acee4f5e22e990c/LICENSE"),
            "flux-2-klein-4b-Q4_0.gguf"=>("Apache-2.0","https://huggingface.co/black-forest-labs/FLUX.2-klein-4B"),
            "Qwen3-4B-Q4_K_M.gguf"=>("Apache-2.0","https://huggingface.co/Qwen/Qwen3-4B"),
            "flux2-vae.safetensors"=>("Apache-2.0","https://huggingface.co/black-forest-labs/FLUX.2-klein-4B"),
            _=>throw new VisualConflict("使用モデルのライセンス情報が見つかりません。")
        };
        return new(model.Key,model.Value,terms,source);
    }

    public async Task<Stream> ExportSet(string id,CancellationToken ct)
    {
        var manifest=await Manifest(id,ct);
        var file=new FileStream(Path.Combine(Path.GetTempPath(),VisualStore.NewId()+".zip"),
            FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None,65536,FileOptions.Asynchronous|FileOptions.DeleteOnClose);
        try
        {
            using (var zip=new ZipArchive(file,ZipArchiveMode.Create,leaveOpen:true))
            {
                await using(var json=zip.CreateEntry("visual-set-manifest.json").Open())
                    await JsonSerializer.SerializeAsync(json,manifest,JsonSerializerOptions.Web,ct);
                foreach(var cut in manifest.Cuts)
                {
                    var bytes=await ReadImage(cut.Asset,ct);
                    await using var output=zip.CreateEntry(cut.Path,CompressionLevel.NoCompression).Open();
                    await output.WriteAsync(bytes,ct);
                }
            }
            file.Position=0;
            return file;
        }
        catch
        {
            await file.DisposeAsync();
            throw;
        }
    }
}
