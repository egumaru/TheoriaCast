namespace TheoriaCast.Music;

public record DraftFields(string? kind = null, string? theme = null, string? lyrics = null,
    string? style = null, string? instruments = null, int? seconds = null, int? bpm = null,
    bool? shorts = null, bool? alternate = null, string? prompt_md = null)
{
    public void Validate()
    {
        if (kind is not (null or "generate_bgm" or "generate_vocal") || seconds is < 5 or > 600 ||
            bpm is < 30 or > 300 || theme?.Length > 500 || lyrics?.Length > 32768 ||
            style?.Length > 1000 || instruments?.Length > 1000 || prompt_md?.Length > 32768)
            throw new ArgumentException("曲の種類、長さ、速さ、文章の長さを確認してください。");
    }
}

public record StudioQuestion(string question, DraftFields draft, string? replaces = null);
public record StudioDecision(string decision);
public record Consultation(string id, string status, string question, DraftFields draft,
    string? reply, DraftFields? changes, string created);
