using TheoriaCast.Visual;
using TheoriaCast.Visual.Api;

internal static class KaraokeSceneTests
{
    internal static void Run()
    {
        static void Check(bool ok, string message)
        {
            if (!ok) throw new InvalidOperationException(message);
        }

        foreach (var fps in new[] { 30, 60 })
        {
            var recipe = new KaraokeRecipe("score", "fixture", null, "", 5, [new(1, .2m, 60)],
                [new(0, .2m, "A"), new(.3m, .5m, "B"), new(3.7m, 5, "C")], null, 4,
                new(Lyrics: "underline", Lines: 2, Fps: fps, LeadMs: 100, HoldMs: 100), "theoriacast-karaoke-ass/2", "", "", "", 5 * fps);
            var scene = new KaraokeScene(recipe);
            int F(decimal seconds) => (int)(fps * seconds);
            for (int n = 1; n <= fps * 5; n++)
                Check(scene.FrameClock(n) < n * 100m / fps && scene.FrameClock(n) > (n - 1) * 100m / fps, $"clock {n}");
            Check(scene.FrameClock(0) == 0 && scene.FrameClock(F(3.7m)) == 369, "zero and exact centisecond");
            Check(scene.Frame(0).Lyrics is [{ Index: 0, Active: true, Underline: true }, { Index: 1, Row: 1 }], "opening rows");
            Check(scene.Frame(F(.2m)).Lyrics is [{ Index: 1, Active: false }, { Index: 2 }], "next lead wins over hold");
            Check(scene.Frame(F(.3m)).Lyrics[0] is { Index: 1, Active: true }, "onset");
            Check(scene.Frame(F(.5m)).Lyrics[0] is { Index: 1, Active: false, Underline: false }, "hold");
            Check(scene.Frame(F(.6m)).Lyrics.Length == 0 && scene.Frame(F(3.6m) - 1).Lyrics.Length == 0, "empty interval");
            Check(scene.Frame(F(3.6m)).Lyrics is [{ Index: 2, Active: false }], "lead");
            Check(scene.Frame(F(3.7m) - 1).Lyrics[0].Active == false && scene.Frame(F(3.7m)).Lyrics[0].Active, "cue boundary");
            Check(scene.Frame(5 * fps - 1).Lyrics[0].Active, "final frame");
            Check(scene.Frame(fps).Notes[0].Active && !scene.Frame(F(1.2m)).Notes[0].Active, "note end exclusive");
        }

        Check(KaraokeSrt.Parse("\uFEFF1\r\n00:00:00,100 --> 00:00:00,200\r\n一\r\n二")[0] == new KaraokeCue(.1m, .2m, "一\n二"), "multiline SRT");
        foreach (var (srt, line) in new[] {
            ("00:00:00,000 --> 00:00:01,000\n�", 2),
            ("00:00:00,000 --> 00:00:01,000 align:�\nA", 1),
            ("1\n00:00:00,000 --> 00:00:01,000\nA\n\n2\n00:00:00,500 --> 00:00:01,500\nB", 6) })
        {
            try { KaraokeSrt.Parse(srt); throw new Exception("invalid SRT accepted"); }
            catch (ArgumentException e) { Check(e.Message.Contains($"{line} 行目"), e.Message); }
        }
        var mediaRecipe = new KaraokeRecipe("lyrics", "probe", null, "", .1m, [], [], null, 4,
            new(Width: 640, Height: 360, Fps: 30), "theoriacast-karaoke-ass/2", "", "", "", 3);
        string[] packets = ["#tb 0: 1/30", "#media_type 0: video", "#dimensions 0: 640x360",
            "#tb 1: 1/8000", "#media_type 1: audio", "0,0,0,1,8,0", "0,1,1,1,8,0", "0,2,2,1,8,0", "1,0,0,800,1600,0"];
        void VerifyMedia(IEnumerable<string> lines)
        {
            var check = new KaraokeMediaCheck(mediaRecipe);
            foreach (var line in lines) check.Read(line);
            check.Verify();
        }
        VerifyMedia(packets);
        foreach (var invalid in new[] { packets.SkipLast(1), packets.Where(line => line != packets[6]),
            packets.Append("#media_type 2: video"), packets.Select(line => line.Replace("640x360", "320x240")),
            packets.Select(line => line.Replace("800,1600", "100,200")) })
        {
            try { VerifyMedia(invalid); throw new Exception("invalid media accepted"); }
            catch (VisualConflict) { }
        }
        Console.WriteLine("PASS: ASS clock, lyric/note transitions at 30/60 fps, SRT diagnostics, output media checks.");
    }
}
