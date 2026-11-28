using System.Net.Http.Headers;
using System.Text.Json;
using AIKernel.Dtos.Hosting;
using AIKernel.Providers.Distributed;

namespace TheoriaCast.Visual.Api;

internal sealed class VisualPeers : IDisposable
{
    private sealed record Peer(ConnectionProfile Profile, string Token, string? Hardware)
    {
        public override string ToString() => Profile.ServerId.ToString("N");
    }
    private readonly Dictionary<string, Peer> peers = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions HeartbeatJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };
    private readonly HttpClient http = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    public VisualPeers(IConfiguration configuration)
    {
        var files = new ConnectionFiles(configuration.GetValue("Distributed:AllowPlainHttp", false));
        foreach (var entry in configuration.GetSection("Visual:Peers").GetChildren())
        {
            using var stream = File.OpenRead(entry["Connection"] ?? throw new InvalidDataException("接続ファイルを指定してください。"));
            var descriptor = files.Read(stream);
            var profile = files.Validate(descriptor);
            var hardware = entry["Hardware"];
            if (profile.Service != VisualProtocol.Service || hardware is not (null or "gpu" or "cloud-gpu"))
                throw new InvalidDataException("素材サーバーの接続設定を確認してください。");
            peers.Add(profile.ServerId.ToString("N"), new(profile, descriptor.Token, hardware));
        }
    }

    public async Task<ComputeResource[]> Resources(CancellationToken ct)
    {
        var nodes = await Task.WhenAll(peers.Values.Where(peer => peer.Hardware is not null).Select(async peer =>
        {
            var id = peer.Profile.ServerId.ToString("N");
            return await Probe(id, async token =>
            {
                using var response = await Send(peer, HttpMethod.Get, "api/v1/visual/nodes/local", null, token);
                var resource = await response.Content.ReadFromJsonAsync<ComputeResource>(cancellationToken: token);
                return resource is { Hardware: "gpu" } && resource.ServerId == id
                    && double.IsFinite(resource.FluxSecondsAt256) && resource.FluxSecondsAt256 is >= 1 and <= 3600
                    ? resource with { Hardware = peer.Hardware!, Name = peer.Profile.Name } : null;
            }, ct);
        }));
        return nodes.OfType<ComputeResource>().ToArray();
    }

    public async Task<VisualAsset?> Asset(AssetRef reference, CancellationToken ct) =>
        await Probe(reference.ServerId, async token =>
        {
            using var response = await Send(peers[reference.ServerId], HttpMethod.Get,
                "api/v1/visual/assets/" + reference.AssetId, null, token);
            var asset = await response.Content.ReadFromJsonAsync<VisualAsset>(cancellationToken: token);
            return asset?.Reference == reference ? asset : null;
        }, ct);

    public async Task<VisualRecipe?> Recipe(string serverId, string id, CancellationToken ct)
    {
        if (!Guid.TryParseExact(id, "N", out _)) return null;
        return await Probe(serverId, async token =>
        {
            using var response = await Send(peers[serverId], HttpMethod.Get, "api/v1/visual/recipes/" + id, null, token);
            var recipe = await response.Content.ReadFromJsonAsync<VisualRecipe>(cancellationToken: token);
            return recipe?.Id == id ? recipe : null;
        }, ct);
    }

    public async Task<byte[]> Image(AssetRef reference, CancellationToken ct)
    {
        if (!peers.TryGetValue(reference.ServerId, out var peer))
            throw new VisualConflict("参照先の素材を確認できません。");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await CheckIdentity(peer, timeout.Token);
            using var response = await Send(peer, HttpMethod.Get, "api/v1/visual/assets/" + reference.AssetId + "/image", null, timeout.Token);
            return await ImageBytes(response, timeout.Token);
        }
        catch (Exception e) when (IsUnavailable(e, ct))
        {
            throw new VisualConflict("参照先の素材を確認できません。");
        }
    }

    public async Task<byte[]> Execute(ComputeResource node, GpuWork work, CancellationToken ct)
    {
        var peer = peers[node.ServerId];
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromHours(1));
            await CheckIdentity(peer, timeout.Token);
            using var response = await Send(peer, HttpMethod.Post, "api/v1/visual/nodes/execute", JsonContent.Create(work), timeout.Token);
            return await ImageBytes(response, timeout.Token);
        }
        catch (Exception e) when (IsUnavailable(e, ct))
        {
            throw new VisualConflict("登録先の GPU ノードで処理を完了できませんでした。");
        }
    }

    private async Task<T?> Probe<T>(string serverId, Func<CancellationToken, Task<T?>> read, CancellationToken ct) where T : class
    {
        if (!peers.TryGetValue(serverId, out var peer)) return null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            await CheckIdentity(peer, timeout.Token);
            return await read(timeout.Token);
        }
        catch (Exception e) when (IsUnavailable(e, ct)) { return null; }
    }

    private async Task CheckIdentity(Peer peer, CancellationToken ct)
    {
        using var response = await Send(peer, HttpMethod.Get, "heartbeat", null, ct);
        var heartbeat = await response.Content.ReadFromJsonAsync<Heartbeat>(HeartbeatJson, ct);
        if (heartbeat?.ServerId != peer.Profile.ServerId.ToString("N") || heartbeat.Service != VisualProtocol.Service)
            throw new InvalidDataException("素材サーバーの識別子が一致しません。");
    }

    private async Task<HttpResponseMessage> Send(Peer peer, HttpMethod method, string path, HttpContent? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, new Uri(peer.Profile.ApiUrl, path)) { Content = body };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", peer.Token);
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.IsSuccessStatusCode) return response;
        response.Dispose();
        throw new HttpRequestException("素材サーバーが要求を受け付けませんでした。");
    }

    private static async Task<byte[]> ImageBytes(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > VisualProtocol.MaxImageBytes)
            throw new InvalidDataException("画像が 16 MiB を超えました。");
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            if (output.Length + read > VisualProtocol.MaxImageBytes)
                throw new InvalidDataException("画像が 16 MiB を超えました。");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static bool IsUnavailable(Exception error, CancellationToken ct) =>
        error is HttpRequestException or IOException or InvalidDataException or System.Text.Json.JsonException
            || error is OperationCanceledException && !ct.IsCancellationRequested;

    public void Dispose() => http.Dispose();
}
