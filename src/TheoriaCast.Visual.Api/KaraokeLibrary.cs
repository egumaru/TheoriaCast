using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace TheoriaCast.Visual.Api;

internal sealed class KaraokeLibrary(VisualStore chunks, KaraokeEncoder encoder, string data,
    ILogger<KaraokeLibrary> logger) : IKaraokeLibrary
{
    private const long MaxAudioBytes = 128 * 1024 * 1024;
    private readonly string root = Path.Combine(data, "karaoke");
    private readonly SemaphoreSlim ready = new(0);
    private readonly VisualWorkQueue queue = new();
    private string AudioPath(string digest) => Path.Combine(root, "audio", VisualStore.CheckHash(digest) + ".wav");
    private string JobsPath => Path.Combine(root, "jobs");

    public StagedImageChunk Stage(ImageChunk chunk) => chunks.Stage(chunk);

    public async Task<KaraokeJob> Submit(KaraokeRequest request, CancellationToken cancellationToken)
    {
        var input = request.Input ?? throw new ArgumentException("音源と歌詞の入力が必要です。");
        var style = ValidateStyle(request.Mode, request.Style ?? new());
        var score = input.Notes ?? [];
        if (string.IsNullOrWhiteSpace(input.Title) || input.Title.Length > 160 ||
            input.TrackId is { Length: > 160 } || input.Lyrics is not { Length: > 0 and <= 2400 } ||
            score.Length > 24000 || request.Mode != "lyrics" && score.Length == 0 ||
            input.Bpm is < 20 or > 350 || input.BeatsPerBar is < 1 or > 12)
            throw new ArgumentException("曲名、ガイド譜、歌詞キュー、テンポを確認してください。");

        var runtime = encoder.Fingerprint();
        var audio = input.Audio ?? throw new ArgumentException("WAV 音源が必要です。");
        var seconds = await ImportAudio(audio, cancellationToken);
        decimal end = 0;
        foreach (var cue in input.Lyrics)
        {
            if (cue is null || cue.Start < end || cue.End <= cue.Start || cue.End > seconds + .001m ||
                string.IsNullOrWhiteSpace(cue.Text) || cue.Text.Length > 500 ||
                cue.Text.Any(c => char.IsControl(c) && c is not ('\r' or '\n')))
                throw new ArgumentException("歌詞は重ならない順序で、音源内の有効な時間区間を指定してください。");
            end = cue.End;
        }
        end = 0;
        foreach (var note in score)
        {
            if (note is null || note.Start < 0 || note.Start + .000001m < end || note.Duration < .01m ||
                note.Start + note.Duration > seconds + .001m || note.Midi is < 21 or > 108)
                throw new ArgumentException("ガイド譜は音源内の単旋律を開始順で指定してください。");
            end = note.Start + note.Duration;
        }
        var recipe = new KaraokeRecipe(request.Mode, input.Title.Trim(), input.TrackId, audio.Sha256, seconds,
            score, input.Lyrics.Select(cue => cue with { Text = cue.Text.Replace("\r\n", "\n").Replace('\r', '\n').Trim() }).ToArray(),
            input.Bpm, input.BeatsPerBar, style, runtime.Engine, runtime.Ffmpeg, runtime.Font, runtime.Family, (int)Math.Ceiling(seconds * style.Fps));
        return Queue(recipe, null);
    }

    private static KaraokeStyle ValidateStyle(string mode, KaraokeStyle style)
    {
        if (mode is not ("bars" or "score" or "lyrics")) throw new ArgumentException("表示方式を確認してください。");
        var lyrics = style.Lyrics ?? (mode == "score" ? "underline" : mode == "lyrics" ? "follow" : "preview");
        var lines = style.Lines ?? (lyrics == "preview" ? 2 : 1);
        if (lyrics is not ("preview" or "underline" or "follow") || lines is < 1 or > 2 ||
            style.Width is < 320 or > 3840 || style.Height is < 240 or > 2160 ||
            style.Width % 2 != 0 || style.Height % 2 != 0 || style.Width * style.Height > 8_294_400 ||
            style.Fps is < 15 or > 60 || style.Scroll is < .03m or > 2m || style.Head is < .08m or > .92m ||
            style.LeadMs is < 0 or > 3000 || style.HoldMs is < 0 or > 3000 ||
            style.FontSize is < .025m or > .10m || style.LyricsTop is < .1m or > .90m ||
            style.LyricsTop + style.FontSize * (lines == 2 ? 2.4m : 1m) > .99m)
            throw new ArgumentException("カラオケ動画のサイズ、表示行数、歌詞位置、スクロール設定を確認してください。");
        return style with
        {
            Lyrics = lyrics, Lines = lines, Background = Color(style.Background), Grid = Color(style.Grid),
            Notes = Color(style.Notes), Playhead = Color(style.Playhead ?? (mode == "score" ? "#EF5151" : "#F5F2EB")),
            LyricsColor = Color(style.LyricsColor), ActiveColor = Color(style.ActiveColor), PreviewColor = Color(style.PreviewColor)
        };
    }

    private static string Color(string value) => value is { Length: 7 } && value[0] == '#' &&
        value.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0
            ? value.ToUpperInvariant() : throw new ArgumentException("色は #RRGGBB で指定してください。");

    private async Task<decimal> ImportAudio(KaraokeAudio audio, CancellationToken ct)
    {
        var target = AudioPath(audio.Sha256);
        if (audio.Chunks is null && File.Exists(target)) return WavDuration(target);
        if (audio.Chunks is not { Length: > 0 and <= 342 })
            throw new ArgumentException("WAV チャンクを順番に指定してください。");

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = target + "." + VisualStore.NewId() + ".tmp";
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = File.Create(temp))
            {
                foreach (var digest in audio.Chunks)
                {
                    var path = chunks.ChunkPath(digest);
                    if (!File.Exists(path)) throw new ArgumentException("WAV チャンクが見つかりません。");
                    var bytes = await File.ReadAllBytesAsync(path, ct);
                    if (bytes.Length is < 1 or > VisualProtocol.ChunkBytes ||
                        output.Length + bytes.Length > MaxAudioBytes || VisualStore.Hash(bytes) != digest)
                        throw new ArgumentException("WAV チャンクの長さまたは SHA-256 が一致しません。");
                    hash.AppendData(bytes);
                    await output.WriteAsync(bytes, ct);
                }
            }
            if (Convert.ToHexStringLower(hash.GetHashAndReset()) != audio.Sha256)
                throw new ArgumentException("WAV の SHA-256 が一致しません。");
            var seconds = WavDuration(temp);
            try { File.Move(temp, target); }
            catch (IOException) when (File.Exists(target)) { }
            return seconds;
        }
        finally { File.Delete(temp); }
    }

    private static decimal WavDuration(string path)
    {
        using var file = File.OpenRead(path);
        Span<byte> header = stackalloc byte[12];
        if (file.Length < 12 || file.Length > MaxAudioBytes) throw new ArgumentException("WAV は128 MiB以内です。");
        file.ReadExactly(header);
        if (!header[..4].SequenceEqual("RIFF"u8) || !header[8..].SequenceEqual("WAVE"u8))
            throw new ArgumentException("RIFF WAV 音源を指定してください。");

        uint rate = 0, samples = 0;
        ushort align = 0;
        Span<byte> format = stackalloc byte[16];
        while (file.Position + 8 <= file.Length)
        {
            file.ReadExactly(header[..8]);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(4, 4));
            var next = file.Position + length;
            if (next > file.Length) throw new ArgumentException("WAV 音源のチャンクが途中で終わっています。");
            if (header[..4].SequenceEqual("fmt "u8) && length >= 16)
            {
                file.ReadExactly(format);
                if (BinaryPrimitives.ReadUInt16LittleEndian(format) is not (1 or 3 or 65534))
                    throw new ArgumentException("PCM または浮動小数点 WAV を指定してください。");
                rate = BinaryPrimitives.ReadUInt32LittleEndian(format[4..8]);
                align = BinaryPrimitives.ReadUInt16LittleEndian(format[12..14]);
            }
            if (header[..4].SequenceEqual("data"u8)) samples = length;
            file.Position = next + (length & 1);
            if (rate != 0 && samples != 0) break;
        }
        var seconds = rate == 0 || align == 0 ? 0 : samples / (decimal)align / rate;
        if (seconds is <= 0 or > 1200) throw new ArgumentException("WAV の長さは20分以内です。");
        return seconds;
    }

    private string DirectoryFor(string id)
    {
        if (!Guid.TryParseExact(id, "N", out var guid)) throw new KeyNotFoundException();
        return Path.Combine(JobsPath, guid.ToString("N"));
    }

    private static string Extension(string mode) => mode == "lyrics" ? "mov" : "mp4";
    private static T Read<T>(string path) where T : class
    {
        try
        {
            // A poll can read the previous snapshot while the worker atomically replaces it.
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            return JsonSerializer.Deserialize<T>(stream, JsonSerializerOptions.Web) ?? throw new InvalidDataException();
        }
        catch (FileNotFoundException) { throw new KeyNotFoundException(); }
        catch (DirectoryNotFoundException) { throw new KeyNotFoundException(); }
    }
    private static void Save<T>(string path, T value) =>
        VisualStore.Write(path, JsonSerializer.SerializeToUtf8Bytes(value, JsonSerializerOptions.Web));

    public KaraokeJob Job(string id)
    {
        var path = DirectoryFor(id);
        id = Path.GetFileName(path);
        try
        {
            var job = Read<KaraokeJob>(Path.Combine(path, "job.json"));
            if (job.Id != id || job.Mode is not ("bars" or "score" or "lyrics") || string.IsNullOrWhiteSpace(job.Title) ||
                !VisualWorkQueue.Pending(job.State) && job.State is not ("done" or "failed") || job.Attempts < 0)
                throw new InvalidDataException();
            return job;
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            // Expose the failed record by its directory ID and leave the unreadable snapshot available for diagnosis.
            return new(id, "unknown", "読み取れないジョブ", "failed", DateTimeOffset.UnixEpoch,
                Error: "ジョブの状態ファイルを読み取れません。");
        }
    }
    public KaraokeRecipe Recipe(string id)
    {
        _ = Job(id);
        return Read<KaraokeRecipe>(Path.Combine(DirectoryFor(id), "recipe.json"));
    }
    public KaraokeJob[] Jobs() => Directory.Exists(JobsPath)
        ? Directory.EnumerateDirectories(JobsPath)
            .Where(path => Guid.TryParseExact(Path.GetFileName(path), "N", out _) && File.Exists(Path.Combine(path, "job.json")))
            .Select(path => Job(Path.GetFileName(path))).OrderByDescending(job => job.CreatedAt).ToArray()
        : [];
    public KaraokeJob Replay(string id) => Queue(Recipe(id), id);
    public KaraokeFrame Frame(string id, int frame) => new KaraokeScene(Recipe(id)).Frame(frame);

    private KaraokeJob Queue(KaraokeRecipe recipe, string? parentId)
    {
        var job = new KaraokeJob(VisualStore.NewId(), recipe.Mode, recipe.Title, "queued", DateTimeOffset.UtcNow, parentId);
        var path = DirectoryFor(job.Id);
        Directory.CreateDirectory(path);
        Save(Path.Combine(path, "recipe.json"), recipe);
        Save(Path.Combine(path, "job.json"), job);
        ready.Release();
        return job;
    }

    public (Stream Stream, string Type) OpenVideo(string id)
    {
        var job = Job(id);
        if (job.State != "done") throw new VisualConflict("動画はまだ書き出されていません。");
        return (File.OpenRead(Path.Combine(DirectoryFor(id), "result." + Extension(job.Mode))),
            job.Mode == "lyrics" ? "video/quicktime" : "video/mp4");
    }

    public async Task<KaraokeVideoChunk> ReadVideo(string id, long offset, int count, CancellationToken cancellationToken)
    {
        var job = Job(id);
        await using var stream = OpenVideo(id).Stream;
        if (offset < 0 || offset > stream.Length || count is < 1 or > VisualProtocol.ChunkBytes)
            throw new ArgumentException("動画チャンクの位置またはサイズが範囲外です。");
        stream.Position = offset;
        var bytes = new byte[(int)Math.Min(count, stream.Length - offset)];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        return new(offset, stream.Length, job.VideoSha256!, bytes);
    }

    internal Task Run(CancellationToken ct) =>
        VisualWorkQueue.Run(RunNext, ready.WaitAsync, logger, ct);

    private async Task<bool> RunNext(CancellationToken ct)
    {
        var job = Jobs().Where(item => VisualWorkQueue.Pending(item.State) && queue.CanRun(item.Id))
            .OrderBy(item => item.State == "running").ThenBy(item => item.CreatedAt).FirstOrDefault();
        if (job is null) return false;
        var path = DirectoryFor(job.Id);
        var status = Path.Combine(path, "job.json");
        void SaveJob(KaraokeJob value)
        {
            Save(status, value);
            job = value;
        }

        await queue.Process(job.Id, job.Attempts,
            attempts => SaveJob(job with { State = "running", Attempts = attempts }),
            Render,
            error => SaveJob(job with { State = "failed", Error = error }),
            "動画を書き出せませんでした。FFmpeg と入力を確認してください。", logger, ct);
        return true;

        async Task Render()
        {
            var ext = Extension(job.Mode);
            var final = Path.Combine(path, "result." + ext);
            var recipe = Recipe(job.Id);
            var temp = await encoder.Render(path, AudioPath(recipe.AudioSha256), ext, recipe, ct);
            File.Move(temp, final, overwrite: true);
            using var video = File.OpenRead(final);
            var sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(video, ct));
            ct.ThrowIfCancellationRequested();
            SaveJob(job with
            {
                State = "done", VideoSha256 = sha256,
                VideoBytes = video.Length, DownloadPath = VisualProtocol.Prefix + "/karaoke/jobs/" + job.Id + "/video"
            });
        }
    }
}

internal sealed class KaraokeWorker(KaraokeLibrary library) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => library.Run(stoppingToken);
}
