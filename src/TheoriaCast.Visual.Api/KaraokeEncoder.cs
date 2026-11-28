using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TheoriaCast.Visual.Api;

internal sealed class KaraokeEncoder(IConfiguration configuration, ILogger<KaraokeEncoder> logger)
{
    internal const string LegacyEngine = "theoriacast-karaoke-ass/1";
    private const string Engine = "theoriacast-karaoke-ass/2";
    private readonly string? ffmpeg = configuration["Visual:KaraokeFFmpeg"];
    private readonly string? font = configuration["Visual:KaraokeFont"];
    private readonly string family = configuration["Visual:KaraokeFontFamily"] ?? "Noto Sans JP";

    public (string Engine, string Ffmpeg, string Font, string Family) Fingerprint()
    {
        if (!Path.IsPathFullyQualified(ffmpeg ?? "") || !File.Exists(ffmpeg) ||
            !Path.IsPathFullyQualified(font ?? "") || !File.Exists(font) ||
            family.Length is < 1 or > 80 || family.Any(c => char.IsControl(c) || c == ','))
            throw new VisualConflict("カラオケ動画用の FFmpeg と日本語フォントを Visual サーバーに設定してください。");
        try
        {
            if (!KaraokeFont.HasFamily(font!, family))
                throw new VisualConflict("設定したファミリー名が描画フォントに含まれていません。");
        }
        catch (IOException)
        {
            throw new VisualConflict("描画フォントの名前テーブルを読み込めません。");
        }
        return (Engine, Digest(ffmpeg!), Digest(font!), family);
    }

    private static string Digest(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    public async Task<string> Render(string directory, string audio, string extension, KaraokeRecipe recipe, CancellationToken ct)
    {
        var runtime = Fingerprint();
        if (recipe.Engine is not (Engine or LegacyEngine) || runtime.Ffmpeg != recipe.FfmpegSha256 ||
            runtime.Font != recipe.FontSha256 || runtime.Family != recipe.FontFamily ||
            Digest(audio) != recipe.AudioSha256)
            throw new VisualConflict("レシピで固定した音源、FFmpeg、フォントのいずれかが一致しません。");

        var fonts = Path.Combine(directory, "fonts");
        Directory.CreateDirectory(fonts);
        var copiedFont = Path.Combine(fonts, "lyrics" + Path.GetExtension(font!));
        File.Copy(font!, copiedFont, overwrite: true);
        if (Digest(copiedFont) != recipe.FontSha256)
            throw new VisualConflict("描画フォントの SHA-256 が一致しません。");
        await File.WriteAllTextAsync(Path.Combine(directory, "render.ass"), new KaraokeScene(recipe).RenderAss(),
            new UTF8Encoding(false), ct);

        var style = recipe.Style;
        var alpha = extension == "mov";
        var duration = (recipe.Frames / (decimal)style.Fps).ToString("0.########", CultureInfo.InvariantCulture);
        var source = FormattableString.Invariant($"color=c=0x{style.Background[1..]}:s={style.Width}x{style.Height}:r={style.Fps}:d={duration}");
        if (alpha) source += ",format=rgba,colorchannelmixer=aa=0";
        var temp = "render.tmp." + extension;
        string[] videoOptions = alpha
            ? ["-c:v", "qtrle", "-pix_fmt", "argb", "-c:a", "pcm_s16le"]
            : ["-c:v", "libx264", "-preset", "ultrafast", "-crf", "18", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart"];
        // New recipes let the finite color source finish independently of the audio encoder.
        string[] limit = recipe.Engine == LegacyEngine ? ["-frames:v", recipe.Frames.ToString(CultureInfo.InvariantCulture)] : [];
        string[] arguments = ["-hide_banner", "-nostats", "-loglevel", "info", "-nostdin", "-y", "-filter_threads", "1",
            "-f", "lavfi", "-i", source, "-f", "wav", "-i", audio, "-map", "0:v:0", "-map", "1:a:0",
            "-vf", "ass=render.ass:fontsdir=fonts:alpha=" + (alpha ? "1" : "0"),
            .. limit, "-map_metadata", "-1",
            "-map_chapters", "-1", "-fflags", "+bitexact", "-flags:v", "+bitexact", "-flags:a", "+bitexact",
            "-threads:v", "1", "-threads:a", "1", .. videoOptions, temp];
        bool fallback = false;
        await Execute(directory, arguments, null, line =>
        {
            if (line.Contains("fontselect:", StringComparison.OrdinalIgnoreCase))
                logger.LogDebug("Karaoke font selection: {Selection}", line);
            if (line.Contains("selecting one more font", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("failed to find any fallback", StringComparison.OrdinalIgnoreCase)) fallback = true;
        }, ct);
        if (fallback && recipe.Engine != LegacyEngine)
            throw new VisualConflict("歌詞に指定フォントで描画できない文字があります。");
        var check = new KaraokeMediaCheck(recipe);
        await Execute(directory, ["-v", "error", "-nostdin", "-xerror", "-i", temp, "-map", "0", "-c", "copy",
            "-f", "framecrc", "-"], check.Read, null, ct);
        check.Verify();
        return Path.Combine(directory, temp);
    }

    private async Task Execute(string directory, string[] arguments, Action<string>? output, Action<string>? diagnostic, CancellationToken ct)
    {
        var start = new ProcessStartInfo(ffmpeg!) { WorkingDirectory = directory, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("FFmpeg could not start.");
        string? last = null;
        try
        {
            await Task.WhenAll(Drain(process.StandardOutput, output), Drain(process.StandardError, line =>
            {
                last = line;
                diagnostic?.Invoke(line);
            }), process.WaitForExitAsync(ct));
            if (process.ExitCode != 0)
            {
                logger.LogWarning("Karaoke FFmpeg exited with {ExitCode}: {Diagnostic}", process.ExitCode, last);
                throw new IOException("FFmpeg could not process the karaoke video.");
            }
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }

        async Task Drain(StreamReader reader, Action<string>? consume)
        {
            try
            {
                while (await reader.ReadLineAsync(ct) is { } line) consume?.Invoke(line);
            }
            catch
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                throw;
            }
        }
    }
}
