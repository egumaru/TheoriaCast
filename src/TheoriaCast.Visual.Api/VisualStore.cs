using System.Security.Cryptography;
using System.Text.Json;

namespace TheoriaCast.Visual.Api;

internal sealed record VisualState(VisualAsset[] Assets, VisualRecipe[] Recipes, VisualJob[] Jobs, AssetSet[] Sets)
{
    public static VisualState Empty { get; } = new([], [], [], []);
}

internal sealed class VisualStore : IDisposable
{
    private readonly string root;
    private readonly string statePath;
    private readonly FileStream exclusive;
    private readonly object gate = new();
    private VisualState state;

    public VisualStore(string directory)
    {
        root = Path.Combine(directory, "library");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "images"));
        Directory.CreateDirectory(Path.Combine(root, "chunks"));
        Directory.CreateDirectory(Path.Combine(root, "recipes"));
        exclusive = File.Open(Path.Combine(root, "server.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            statePath = Path.Combine(root, "library.json");
            state = File.Exists(statePath)
                ? JsonSerializer.Deserialize<VisualState>(File.ReadAllBytes(statePath), JsonSerializerOptions.Web)
                    ?? throw new InvalidDataException("素材棚のデータを読み込めません。")
                : VisualState.Empty;
        }
        catch
        {
            exclusive.Dispose();
            throw;
        }
    }

    public VisualState Read()
    {
        lock (gate) return state;
    }

    public T Update<T>(Func<VisualState, (VisualState State, T Result)> transform)
    {
        lock (gate)
        {
            var (updated, result) = transform(state);
            if (!ReferenceEquals(state, updated))
            {
                Write(statePath, JsonSerializer.SerializeToUtf8Bytes(updated, JsonSerializerOptions.Web));
                state = updated;
            }
            return result;
        }
    }

    public void SaveRecipe(VisualRecipe recipe) =>
        Write(Path.Combine(root, "recipes", recipe.Id + ".json"),
            JsonSerializer.SerializeToUtf8Bytes(recipe, JsonSerializerOptions.Web));

    internal static void Write(string destination, ReadOnlySpan<byte> bytes)
    {
        var temporary = destination + "." + NewId() + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try
        {
            using (var stream = new FileStream(temporary, options))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(temporary, destination, overwrite: true);
                    break;
                }
                catch (Exception ex) when (OperatingSystem.IsWindows() && attempt < 4 &&
                    (ex.HResult & 0xffff) is 5 or 32 or 33)
                {
                    // An indexer or antivirus can briefly hold the published file open.
                    Thread.Sleep(20 * (1 << attempt));
                }
            }
        }
        finally { File.Delete(temporary); }
    }

    public string ImagePath(string hash) => Path.Combine(root, "images", CheckHash(hash));
    internal string ChunkPath(string hash) => Path.Combine(root, "chunks", CheckHash(hash));
    public void SaveImage(string hash, byte[] bytes)
    {
        lock (gate)
        {
            var path = ImagePath(hash);
            if (!File.Exists(path)) Write(path, bytes);
        }
    }

    public StagedImageChunk Stage(ImageChunk chunk)
    {
        if (chunk.Bytes is not { Length: > 0 and <= VisualProtocol.ChunkBytes })
            throw new ArgumentException("画像チャンクのサイズは 1〜384 KiB です。");
        var hash = Hash(chunk.Bytes);
        lock (gate)
        {
            var path = Path.Combine(root, "chunks", hash);
            Write(path, chunk.Bytes);
        }
        return new(hash);
    }

    public async Task<byte[]> ImportBytes(ImportImage request, CancellationToken ct)
    {
        if (request.Bytes is { Length: > 0 and <= VisualProtocol.ChunkBytes } inline && request.Chunks is null)
            return inline;
        const int maxChunks = (VisualProtocol.MaxImageBytes + VisualProtocol.ChunkBytes - 1) / VisualProtocol.ChunkBytes;
        if (request.Bytes is not null || request.Chunks is not { Length: > 0 and <= maxChunks } chunks)
            throw new ArgumentException("384 KiB までの画像、または順序どおりの画像チャンクを指定してください。");
        using var output = new MemoryStream();
        foreach (var hash in chunks)
        {
            var path = Path.Combine(root, "chunks", CheckHash(hash));
            if (!File.Exists(path)) throw new ArgumentException("画像チャンクが見つかりません。");
            var bytes = await File.ReadAllBytesAsync(path, ct);
            if (output.Length + bytes.Length > VisualProtocol.MaxImageBytes || Hash(bytes) != hash)
                throw new ArgumentException("画像チャンクのサイズまたは SHA-256 を確認してください。");
            await output.WriteAsync(bytes, ct);
        }
        return output.ToArray();
    }

    public void PruneChunks()
    {
        lock (gate)
        {
            foreach (var path in Directory.EnumerateFiles(Path.Combine(root, "chunks")))
                if (File.GetLastWriteTimeUtc(path) < DateTime.UtcNow.AddHours(-24)) File.Delete(path);
        }
    }

    public static string NewId() => Guid.NewGuid().ToString("N");
    public static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public static string CheckHash(string hash)
    {
        if (hash is not { Length: 64 } || hash.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("SHA-256 を小文字の16進数で指定してください。");
        return hash;
    }

    public static AssetRef CheckRef(AssetRef reference)
    {
        if (reference is null || !Guid.TryParseExact(reference.ServerId, "N", out var server)
            || !Guid.TryParseExact(reference.AssetId, "N", out var asset))
            throw new ArgumentException("素材のサーバー識別子と素材 ID を確認してください。");
        return new(server.ToString("N"), asset.ToString("N"), CheckHash(reference.Sha256));
    }

    public void Dispose() => exclusive.Dispose();
}
