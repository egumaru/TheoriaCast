using System.Text.Json.Nodes;

namespace TheoriaCast.Music;

public record Clip(string track, double gain = 1);
public record Cue(double start, double end, string text);
public record TimingPoint(double from, double to);
public record FrozenTake(string model, string revision, JsonObject runtime, JsonObject arguments, string master_sha256)
{
    public void Check()
    {
        if (string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(revision) || runtime == null ||
            arguments == null || master_sha256 is not {Length:64} || !master_sha256.All(Uri.IsHexDigit))
            throw new ArgumentException("再現用 JSON のモデル、引数、照合値を確認してください。");
    }
}
public record CompositionDocument(string format, int version, JobSpec job, FrozenTake? take = null)
{
    public CompositionDocument Check()
    {
        if (format!="TheoriaCast.Music" || version!=1 || job==null ||
            job.type is not ("generate_bgm" or "generate_vocal") || job.replay!=null)
            throw new ArgumentException("対応する作曲ファイル（バージョン1）を指定してください。");
        if(job.theme==null || job.style==null || job.lyrics==null || job.instruments==null ||
            job.source!=null || job.replacement!=null || job.assign_only || job.clips!=null || job.work!=null)
            throw new ArgumentException("Invalid composition fields.");
        if(take!=null) (job with {replay=take}).Check();
        else (job with {theme=string.IsNullOrWhiteSpace(job.theme)?"Draft":job.theme,
                        lyrics=string.IsNullOrWhiteSpace(job.lyrics)?"Draft":job.lyrics}).Check();
        return this;
    }
}
public record JobSpec(string type, string theme = "", string lyrics = "", string style = "",
    int seconds = 120, Dictionary<string,string>? readings = null, string priority = "interactive",
    string? source = null, Clip[]? clips = null, string mode = "join", double crossfade = 0,
    double start = 0, double? end = null, double fade_in = 0, double fade_out = 0,
    Cue[]? cues = null, string? work = null, bool shorts = false, int? bpm = null, int? seed = null,
    string instruments = "", string creator = "human", TimingPoint[]? timing = null, bool assign_only = false,
    string? preferred_node = null, string? replacement = null, double replacement_start = 0,
    string? prompt_md = null, string? score_abc = null, FrozenTake? replay = null)
{
    public void CheckMaterials()
    {
        if (prompt_md?.Length>32768 || score_abc?.Length>262144 || lyrics?.Length>32768)
            throw new ArgumentException("指示書・歌詞は32,768文字、楽譜は262,144文字までです。");
        if (type=="generate_bgm" && !string.IsNullOrWhiteSpace(score_abc))
            throw new ArgumentException("楽譜を使う場合は歌のある曲を選んでください。");
    }
    public JobSpec Check()
    {
        CheckMaterials();
        if (replay!=null)
        {
            replay.Check();
            if (type is not ("generate_bgm" or "generate_vocal") || seed==null)
                throw new ArgumentException("再現には曲の種類と確定したシードが必要です。");
        }
        if (type is not ("generate_bgm" or "generate_vocal" or "regen_reading_fix" or "mix" or "edit"))
            throw new ArgumentException("曲の種類を選んでください。");
        if (priority is not ("interactive" or "batch") || seconds is < 5 or > 600)
            throw new ArgumentException("長さは5〜600秒で指定してください。");
        if (type.StartsWith("generate_") && string.IsNullOrWhiteSpace(theme))
            throw new ArgumentException("テーマを入力してください。");
        if (assign_only && (type!="edit" || string.IsNullOrWhiteSpace(work))) throw new ArgumentException("作品名を入力してください。");
        if(preferred_node is not (null or "A" or "B"))
            throw new ArgumentException("曲を作れる機器を指定してください。");
        double previousFrom=0, previousTo=0;
        foreach(var point in timing??[])
        {
            if(type!="edit" || point==null || point.from<=previousFrom || point.to<=previousTo ||
                (point.to-previousTo)/(point.from-previousFrom) is < 0.5 or > 2)
                throw new ArgumentException("歌の時刻は前から順に指定し、各区間の長さを元の半分〜2倍にしてください。");
            previousFrom=point.from;previousTo=point.to;
        }
        if (bpm is < 30 or > 300 || seed is < 0 || creator is not ("human" or "codex"))
            throw new ArgumentException("テンポは30〜300、再現用の番号は0以上で指定してください。");
        if (type == "generate_vocal" && string.IsNullOrWhiteSpace(lyrics))
            throw new ArgumentException("歌詞を入力してください。");
        if ((type is "regen_reading_fix" or "edit") && string.IsNullOrWhiteSpace(source))
            throw new ArgumentException("元の曲を選んでください。");
        if (replacement != null && (type != "edit" || string.IsNullOrWhiteSpace(replacement) ||
            end is null || replacement_start < 0 || assign_only || timing?.Length > 0 ||
            cues != null || fade_in != 0 || fade_out != 0))
            throw new ArgumentException("差し替える曲と開始・終了位置を確認してください。");
        if (type == "regen_reading_fix" && (readings == null || readings.Count == 0))
            throw new ArgumentException("読み方を指定してください。");
        if (readings?.Any(r => string.IsNullOrWhiteSpace(r.Key) || string.IsNullOrWhiteSpace(r.Value)
            || !System.Text.RegularExpressions.Regex.IsMatch(r.Value, @"\A[ぁ-ゖァ-ヺー ・　]+\z")) == true)
            throw new ArgumentException("読みはかなで入力してください。");
        if (type == "mix" && (clips is not { Length: >= 2 and <= 16 } || mode is not ("join" or "layer")
            || clips.Any(c => c == null || c.gain is < 0 or > 4)))
            throw new ArgumentException("合成する曲と音量を確認してください。");
        if (start < 0 || end <= start || fade_in < 0 || fade_out < 0 || crossfade < 0 ||
            cues?.Any(c => c == null || c.start < 0 || c.end <= c.start || string.IsNullOrWhiteSpace(c.text)) == true)
            throw new ArgumentException("開始・終了の秒数を確認してください。");
        return this;
    }
}
public record NodeReport(string node, bool ready, int free_mib, string detail, string[] types);
public record Update(string lease, string state, int progress = 0, string detail = "");
public record Review(string? status = null, bool? favorite = null);
public record WorkTrack(string track);
public record Claim(string id, string lease, JobSpec spec);
public record Job(string id, JobSpec spec, string state, string? node, int progress, string detail,
    string created_at, int? position, string? track, JsonNode? result);
public record Track(string id, string title, string audio, [property:System.Text.Json.Serialization.JsonIgnore] string file_path, JsonNode provenance,
    string status, bool favorite);
