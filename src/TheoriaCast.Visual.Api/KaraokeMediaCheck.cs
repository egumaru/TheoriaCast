using System.Globalization;

namespace TheoriaCast.Visual.Api;

// FFmpeg's framecrc muxer exposes the demuxed packet PTS, duration and stream headers.
// Both supported video encoders emit one packet per frame with no frame reordering.
internal sealed class KaraokeMediaCheck(KaraokeRecipe recipe)
{
    private readonly decimal[] tick = new decimal[2];
    private bool video, audio, resolution, invalid;
    private int frames;
    private decimal audioEnd;

    public void Read(string line)
    {
        if (line.StartsWith("#media_type ", StringComparison.Ordinal))
        {
            if (line == "#media_type 0: video") video = true;
            else if (line == "#media_type 1: audio") audio = true;
            else invalid = true;
        }
        else if (line.StartsWith("#dimensions 0: ", StringComparison.Ordinal))
            resolution = line == FormattableString.Invariant($"#dimensions 0: {recipe.Style.Width}x{recipe.Style.Height}");
        else if (line.StartsWith("#tb ", StringComparison.Ordinal))
        {
            var parts = line[4..].Split([':', '/'], StringSplitOptions.TrimEntries);
            if (parts.Length == 3 && int.TryParse(parts[0], out var stream) && stream is 0 or 1 &&
                Parse(parts[1], out var n) && Parse(parts[2], out var d) && n > 0 && d > 0) tick[stream] = n / d;
            else invalid = true;
        }
        else if (line.Length > 0 && line[0] != '#')
        {
            var parts = line.Split(',', 6, StringSplitOptions.TrimEntries);
            if (parts.Length != 6 || !Parse(parts[2], out var pts) || !Parse(parts[3], out var duration))
            { invalid = true; return; }
            if (parts[0] == "0")
            {
                var rate = recipe.Style.Fps;
                if (Math.Abs(pts * tick[0] * rate - frames) > .001m ||
                    Math.Abs(duration * tick[0] * rate - 1) > .001m) invalid = true;
                frames++;
            }
            else if (parts[0] == "1") audioEnd = Math.Max(audioEnd, (pts + duration) * tick[1]);
            else invalid = true;
        }
    }

    private static bool Parse(string value, out decimal number) =>
        decimal.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number);

    public void Verify()
    {
        // MOV/MP4 track edit lists may round the final audio sample to a millisecond.
        if (invalid || !video || !audio || !resolution || frames != recipe.Frames ||
            audioEnd <= 0 || Math.Abs(audioEnd - recipe.AudioSeconds) > .001m + tick[1])
            throw new VisualConflict(FormattableString.Invariant(
                $"動画の事後検査に失敗しました。映像 {frames}/{recipe.Frames} フレーム、音声 {audioEnd:0.###}/{recipe.AudioSeconds:0.###} 秒。"));
    }
}
