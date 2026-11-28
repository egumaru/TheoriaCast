using System.Text.Json.Nodes;

namespace TheoriaCast.Music;

public sealed record ProjectClipSource(string take, string? loop_job=null, JsonObject? short_recipe=null,
    string? sha256=null, string? loop_sha256=null);
public sealed record ProjectClip(string id, ProjectClipSource source, long start_sample=0, long in_sample=0,
    long? out_sample=null, long fade_in_samples=0, long fade_out_samples=0, int repeat_count=1, long? length_samples=null);
public sealed record ClipEditCommand(string lane, string id, ProjectClipSource? source=null, long? start_sample=null,
    long? in_sample=null, long? out_sample=null, long? fade_in_samples=null, long? fade_out_samples=null,
    int? repeat_count=null, long? length_samples=null, string? loop_job=null, string? to_lane=null);
