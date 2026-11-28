using System.Globalization;
using System.Text.RegularExpressions;

namespace TheoriaCast.Visual.Api;

internal static partial class KaraokeSrt
{
    [GeneratedRegex(@"^([0-9]{1,3}):([0-5][0-9]):([0-5][0-9])[,.]([0-9]{3})\s+-->\s+([0-9]{1,3}):([0-5][0-9]):([0-5][0-9])[,.]([0-9]{3})(?:\s+.*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex Timestamp();

    public static KaraokeCue[] Parse(string srt)
    {
        if (srt.Length > 2_000_000) throw new ArgumentException("SRT が長すぎます。");
        var lines = srt.TrimStart('\uFEFF').ReplaceLineEndings("\n").Split('\n');
        var invalidLine = Array.FindIndex(lines, line => line.Contains('\uFFFD'));
        if (invalidLine >= 0) throw Error(invalidLine + 1, "文字化けがあります。UTF-8 で保存してください。");
        var cues = new List<KaraokeCue>();
        int i = 0;
        while (i < lines.Length)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) { i++; continue; }
            if (int.TryParse(lines[i].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out _)) i++;
            var timingLine = i + 1;
            var match = Timestamp().Match(i < lines.Length ? lines[i++].Trim() : "");
            if (!match.Success) throw Error(timingLine, "時刻行を確認してください。");
            var start = Seconds(match, 1);
            var end = Seconds(match, 5);
            if (end <= start || cues.Count > 0 && start < cues[^1].End)
                throw Error(timingLine, "歌詞区間の重なり、順序、終了時刻を確認してください。");
            var first = i;
            while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]))
                i++;
            var text = string.Join('\n', lines[first..i]).Trim();
            if (text.Length is 0 or > 500 || cues.Count >= 2400)
                throw Error(timingLine, "歌詞の長さまたは件数を確認してください。");
            cues.Add(new(start, end, text));
        }
        return cues.ToArray();
    }

    private static decimal Seconds(Match m, int i)
    {
        int Number(int n) => int.Parse(m.Groups[n].Value, CultureInfo.InvariantCulture);
        return Number(i) * 3600 + Number(i + 1) * 60 + Number(i + 2) + Number(i + 3) / 1000m;
    }

    private static ArgumentException Error(int line, string message) => new($"SRT {line} 行目：{message}");
}
