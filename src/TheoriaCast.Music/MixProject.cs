namespace TheoriaCast.Music;

public sealed record ProjectLane(string id, string take, long start_sample = 0, double gain_db = 0,
    double pan = 0, bool mute = false, bool solo = false, bool invert_polarity = false, string? sha256 = null,
    ProjectClip[]? clips = null, MixProcessor[]? dsp = null);
public sealed record MixManifest(string title, ProjectLane[] tracks, double master_gain_db = 0, double? bpm = null,
    int sample_rate = 48000, string time_unit = "samples", string schema = "theoriacast.static-mix/v1");
public sealed record MixProjectCommand(string action, string? project = null, string? expected = null,
    MixManifest? manifest = null, string? revision = null, string? label = null, ClipEditCommand? clip = null,
    DspEditCommand? dsp = null);
public sealed record MixProcessor(string id, string type, Dictionary<string,double> parameters);
public sealed record DspEditCommand(string lane, string id, string? type = null,
    Dictionary<string,double>? parameters = null, int? index = null);
public sealed record ProjectRenderRef(string project, string revision);
