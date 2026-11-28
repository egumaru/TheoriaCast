using System.Text.Json.Nodes;

namespace TheoriaCast.Music;

public sealed record LoopSelection(double start,double end,int crossfade_ms=30);
public sealed record LoopSuggestion(string track, LoopSelection selection);
public sealed record CreatorExport(string track,string kind,LoopSelection? loop=null,ProjectRenderRef? project_mix=null);
public sealed record ExportJob(string id,CreatorExport request,string source_sha256,string state,DateTimeOffset created_at,
    string? error=null,string? audio_package=null,JsonObject? recipe=null);
