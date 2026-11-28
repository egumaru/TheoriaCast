using System.Globalization;
using System.Text;

namespace TheoriaCast.Visual.Api;

internal sealed class KaraokeScene(KaraokeRecipe recipe)
{
    private KaraokeStyle Style => recipe.Style;
    private bool Overlay => recipe.Mode == "lyrics";
    private bool Staff => recipe.Mode == "score";
    private int Head => Pixels(Style.Width * Style.Head);
    private int Margin => Pixels(Style.Width * .05m);
    private decimal Speed => Style.Width * Style.Scroll;
    private int BottomLine => Pixels(Style.Height * .52m);
    private int End => FrameClock(recipe.Frames);
    private int Gap => gap ??= Math.Max(3, Math.Min(Pixels(Style.Height * .035m),
        Pixels(Style.Height * .32m / Math.Max(8, recipe.Notes.Select(note => Math.Abs(Diatonic(note.Midi) - Diatonic(64))).DefaultIfEmpty().Max()))));
    private int NoteRadius => Math.Max(3, Pixels(Style.Height * .009m));
    private int[]? pitchRange;
    private int? gap;

    private static int Pixels(decimal value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);
    private static string Number(decimal value) => value.ToString("0.####", CultureInfo.InvariantCulture);
    private static string AssColor(string hex) => $"&H{hex[5..7]}{hex[3..5]}{hex[1..3]}&";
    private static string Clock(int centiseconds) =>
        FormattableString.Invariant($"{centiseconds / 360000}:{centiseconds / 6000 % 60:00}:{centiseconds / 100 % 60:00}.{centiseconds % 100:00}");

    private bool Legacy => recipe.Engine == KaraokeEncoder.LegacyEngine;
    internal int FrameClock(int frame) => Legacy ? (int)Math.Floor(frame * 100m / Style.Fps)
        : frame == 0 ? 0 : (int)Math.Ceiling(frame * 100m / Style.Fps) - 1;
    private int Boundary(decimal seconds) =>
        Math.Clamp((int)Math.Ceiling(seconds * Style.Fps), 0, recipe.Frames);
    private decimal X(decimal onset, decimal seconds) => Head + (onset - seconds) * Speed;
    private static int Diatonic(int midi) => midi / 12 * 7 + ((midi % 12) switch
    {
        0 or 1 => 0, 2 or 3 => 1, 4 => 2, 5 or 6 => 3, 7 or 8 => 4, 9 or 10 => 5, _ => 6
    });

    private int PitchY(int midi)
    {
        if (Staff) return BottomLine - Pixels((Diatonic(midi) - Diatonic(64)) * Gap / 2m);
        pitchRange ??= [recipe.Notes.Min(note => note.Midi) - 2, recipe.Notes.Max(note => note.Midi) + 2];
        return Pixels(Style.Height * (.59m - .36m * (midi - pitchRange[0]) / (pitchRange[1] - pitchRange[0])));
    }

    public KaraokeFrame Frame(int index)
    {
        if (index < 0 || index >= recipe.Frames) throw new ArgumentException("フレーム番号が動画の範囲外です。");
        var time = index / (decimal)Style.Fps;
        var notes = Overlay ? [] : recipe.Notes.Select((note, i) => new KaraokeFrameNote(i, note.Midi,
            X(note.Start, time), PitchY(note.Midi), note.Start <= time && time < note.Start + note.Duration))
            .Where(note => note.X < Style.Width - Margin && note.X + (Staff
                ? NoteRadius * 2 : recipe.Notes[note.Index].Duration * Speed) >= Margin).ToArray();
        return new(index, time, Overlay ? null : Head, notes, LyricsAt(time));
    }

    private KaraokeFrameLyric[] LyricsAt(decimal time)
    {
        var current = Array.FindLastIndex(recipe.Lyrics, cue => cue.Start <= time);
        var next = current + 1;
        var active = current >= 0 && time < recipe.Lyrics[current].End;
        var upcoming = next < recipe.Lyrics.Length;
        var index = active ? current
            : upcoming && time >= recipe.Lyrics[next].Start - Style.LeadMs / 1000m ? next
            : current >= 0 && time < recipe.Lyrics[current].End + Style.HoldMs / 1000m ? current
            : Style.Lyrics == "preview" && upcoming ? next : -1;
        if (index < 0) return [];
        var main = new KaraokeFrameLyric(index, recipe.Lyrics[index].Text, active, active && Style.Lyrics == "underline",
            active && Style.Lyrics != "follow" ? Style.ActiveColor : Style.LyricsColor, 0);
        return Style.Lines == 2 && index + 1 < recipe.Lyrics.Length
            ? [main, new(index + 1, recipe.Lyrics[index + 1].Text, false, false, Style.PreviewColor, 1)]
            : [main];
    }

    public string RenderAss()
    {
        var ass = new StringBuilder(FormattableString.Invariant($"""
            [Script Info]
            ScriptType: v4.00+
            PlayResX: {Style.Width}
            PlayResY: {Style.Height}
            WrapStyle: 2
            ScaledBorderAndShadow: yes

            [V4+ Styles]
            Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
            Style: Base,{recipe.FontFamily},32,&H00FFFFFF,&H00FFFFFF,&H00101823,&H00000000,0,0,0,0,100,100,0,0,1,0,0,7,0,0,0,1

            [Events]
            Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
            """));
        ass.Append('\n');
        if (!Overlay) RenderGuide(ass);
        RenderLyrics(ass);
        return ass.ToString();
    }

    private void RenderGuide(StringBuilder ass)
    {
        var right = Style.Width - Margin;
        var top = Pixels(Style.Height * .16m);
        var bottom = Pixels(Style.Height * .65m);
        if (Staff)
        {
            for (var i = 0; i < 5; i++)
                Draw(ass, 0, 0, End, At(Margin, BottomLine - i * Gap), Style.Grid, Rectangle(right - Margin, 2));
        }
        else
        {
            for (var i = 0; i <= 6; i++)
                Draw(ass, 0, 0, End, At(Margin, top + (bottom - top) * i / 6), Style.Grid, Rectangle(right - Margin, 1));
        }
        if (recipe.Bpm is { } bpm)
        {
            var beat = 60m / bpm;
            var step = Staff ? beat * recipe.BeatsPerBar : beat;
            for (var grid = 0m; grid < recipe.AudioSeconds; grid += step)
                Move(ass, 0, grid, Staff ? BottomLine - 4 * Gap : top, 2,
                    Style.Grid, Rectangle(2, Staff ? 4 * Gap : bottom - top));
        }
        foreach (var note in recipe.Notes)
        {
            var y = PitchY(note.Midi);
            if (Staff) DrawStaffNote(ass, note, y);
            else
            {
                var length = Math.Max(2, Pixels(note.Duration * Speed));
                Move(ass, 1, note.Start, y - NoteRadius, length, Style.Notes, Rectangle(length, NoteRadius * 2));
            }
        }
        Draw(ass, 2, 0, End, At(Head - 2, top), Style.Playhead!, Rectangle(4, bottom - top));
    }

    private void DrawStaffNote(StringBuilder ass, KaraokeNote note, int y)
    {
        var r = NoteRadius;
        var head = FormattableString.Invariant(
            $"m {-r} 0 b {-r} {-r} {r} {-r} {r} 0 b {r} {r} {-r} {r} {-r} 0");
        Move(ass, 1, note.Start, y, r * 2, Style.Notes, head);
        Move(ass, 1, note.Start, y - r * 3, r, Style.Notes,
            FormattableString.Invariant($"m {r - 2} 0 l {r} 0 {r} {r * 3} {r - 2} {r * 3}"));
        var topLine = BottomLine - 4 * Gap;
        for (var ledger = BottomLine + Gap; ledger <= y + 1; ledger += Gap)
            Ledger(ledger);
        for (var ledger = topLine - Gap; ledger >= y - 1; ledger -= Gap)
            Ledger(ledger);
        if (note.Midi % 12 is 1 or 3 or 6 or 8 or 10)
            Move(ass, 1, note.Start, y, r * 3, Style.Notes, FormattableString.Invariant(
                $"m {-3*r} -2 l {-r-2} -2 {-r-2} 0 {-3*r} 0 m {-3*r} 3 l {-r-2} 3 {-r-2} 5 {-3*r} 5 m {-2*r-2} -7 l {-2*r} -7 {-2*r} 8 {-2*r-2} 8 m {-2*r+3} -7 l {-2*r+5} -7 {-2*r+5} 8 {-2*r+3} 8"));

        void Ledger(int line) => Move(ass, 0, note.Start, line, r * 2, Style.Grid,
            FormattableString.Invariant($"m {-2*r} 0 l {2*r} 0 {2*r} 2 {-2*r} 2"));
    }

    private void RenderLyrics(StringBuilder ass)
    {
        var boundaries = recipe.Lyrics.SelectMany(cue => new[]
        {
            Boundary(cue.Start - Style.LeadMs / 1000m), Boundary(cue.Start),
            Boundary(cue.End), Boundary(cue.End + Style.HoldMs / 1000m)
        }).Append(0).Append(recipe.Frames).Distinct().Order().ToArray();
        for (var i = 0; i < boundaries.Length - 1; i++)
        {
            var start = FrameClock(boundaries[i]);
            var end = FrameClock(boundaries[i + 1]);
            foreach (var line in LyricsAt(boundaries[i] / (decimal)Style.Fps))
            {
                var text = line.Text.Replace('\n', '　').Replace('\\', '＼').Replace('{', '｛').Replace('}', '｝');
                var normal = Style.Height * Style.FontSize * (line.Row == 0 ? 1m : .74m);
                var size = Math.Max(5, Pixels(Math.Min(normal, (Style.Width - 2 * Margin) / (decimal)text.EnumerateRunes().Count())));
                var y = Pixels(Style.Height * (Style.LyricsTop + line.Row * Style.FontSize * 1.55m));
                var tags = FormattableString.Invariant(
                    $"\\an8\\pos({Style.Width / 2},{y})\\fs{size}\\1c{AssColor(line.Color)}\\bord{Math.Max(1, Pixels(normal / 24))}\\u{(line.Underline ? 1 : 0)}");
                Line(ass, 3, start, end, tags, text);
            }
        }
    }

    private void Move(StringBuilder ass, int layer, decimal onset, int y, int span, string color, string shape)
    {
        var enters = onset - (Style.Width + span - Head) / Speed;
        var leaves = onset + (Head + span) / Speed;
        var first = Legacy ? Math.Max(0, (int)Math.Floor(enters * 100)) : FrameClock(Boundary(enters));
        var last = Legacy ? Math.Min(End, (int)Math.Ceiling(leaves * 100)) : FrameClock(Boundary(leaves));
        if (last <= first) return;
        var from = X(onset, first / 100m);
        var to = X(onset, last / 100m);
        var position = $"\\move({Number(from)},{y},{Number(to)},{y})\\clip({Margin},0,{Style.Width - Margin},{Style.Height})";
        Draw(ass, layer, first, last, position, color, shape);
    }

    private static string At(int x, int y) => FormattableString.Invariant($"\\pos({x},{y})");
    private static string Rectangle(int w, int h) => FormattableString.Invariant($"m 0 0 l {w} 0 {w} {h} 0 {h}");
    private static void Draw(StringBuilder ass, int layer, int start, int end, string at, string color, string shape) =>
        Line(ass, layer, start, end, $"\\an7{at}\\1c{AssColor(color)}\\p1", shape);

    private static void Line(StringBuilder ass, int layer, int start, int end, string tags, string text)
    {
        if (start < end)
            ass.Append(CultureInfo.InvariantCulture, $"Dialogue: {layer},{Clock(start)},{Clock(end)},Base,,0,0,0,,{{{tags}}}{text}\n");
    }
}
