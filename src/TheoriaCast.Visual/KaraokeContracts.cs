namespace TheoriaCast.Visual;

// The note and cue fields match the Music guide recipe and lyric timing exports.
public sealed record KaraokeNote(decimal Start, decimal Duration, int Midi);
public sealed record KaraokeCue(decimal Start, decimal End, string Text);
public sealed record KaraokeAudio(string Sha256, string[]? Chunks = null);
public sealed record KaraokeInput(string Title, KaraokeAudio Audio, KaraokeCue[] Lyrics,
    KaraokeNote[]? Notes = null, string? TrackId = null, decimal? Bpm = null, int BeatsPerBar = 4);

public sealed record KaraokeStyle(string? Lyrics = null, int Width = 1920, int Height = 1080, int Fps = 30,
    decimal Scroll = .22m, decimal Head = .34m, int? Lines = null, int LeadMs = 250, int HoldMs = 150,
    string Background = "#131923", string Grid = "#465364", string Notes = "#70C9DB",
    string? Playhead = null, string LyricsColor = "#F5F2EB", string ActiveColor = "#FFD969",
    string PreviewColor = "#AEB9C8", decimal FontSize = .055m, decimal LyricsTop = .73m);

public sealed record KaraokeRequest(string Mode, KaraokeInput Input, KaraokeStyle? Style = null);
public sealed record KaraokeRecipe(string Mode, string Title, string? TrackId, string AudioSha256, decimal AudioSeconds,
    KaraokeNote[] Notes, KaraokeCue[] Lyrics, decimal? Bpm, int BeatsPerBar, KaraokeStyle Style,
    string Engine, string FfmpegSha256, string FontSha256, string FontFamily, int Frames);
public sealed record KaraokeJob(string Id, string Mode, string Title, string State, DateTimeOffset CreatedAt,
    string? ParentId = null, string? VideoSha256 = null, long? VideoBytes = null, string? DownloadPath = null,
    string? Error = null, int Attempts = 0);
public sealed record KaraokeFrameNote(int Index, int Midi, decimal X, int Y, bool Active);
public sealed record KaraokeFrameLyric(int Index, string Text, bool Active, bool Underline, string Color, int Row);
public sealed record KaraokeFrame(int Index, decimal Seconds, int? PlayheadX, KaraokeFrameNote[] Notes, KaraokeFrameLyric[] Lyrics);
public sealed record KaraokeVideoChunk(long Offset, long TotalBytes, string Sha256, byte[] Bytes);

public interface IKaraokeLibrary
{
    StagedImageChunk Stage(ImageChunk chunk);
    Task<KaraokeJob> Submit(KaraokeRequest request, CancellationToken cancellationToken);
    KaraokeJob[] Jobs();
    KaraokeJob Job(string id);
    KaraokeRecipe Recipe(string id);
    KaraokeJob Replay(string id);
    KaraokeFrame Frame(string id, int frame);
    Task<KaraokeVideoChunk> ReadVideo(string id, long offset, int count, CancellationToken cancellationToken);
}
