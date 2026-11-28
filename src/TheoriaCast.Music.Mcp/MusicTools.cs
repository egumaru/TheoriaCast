using System.ComponentModel;
using AIKernel.Abstractions.Hosting;
using AIKernel.Dtos.Hosting;
using Microsoft.Extensions.AI;
using System.Reflection;
using System.Text.Json;

namespace TheoriaCast.Music.Mcp;
public sealed class MusicTools : IMcpToolProvider
{
    private readonly IMusicLibrary store;
    private readonly IReadOnlyDictionary<string, AIFunction> functions;

    public MusicTools(IMusicLibrary store)
    {
        this.store = store;
        functions = typeof(MusicTools).GetMethods().Where(m => m.IsDefined(typeof(DisplayNameAttribute)))
            .Select(m => AIFunctionFactory.Create(m, this, new AIFunctionFactoryOptions {
                Name = m.GetCustomAttribute<DisplayNameAttribute>()!.DisplayName,
                Description = m.GetCustomAttribute<DescriptionAttribute>()!.Description,
                SerializerOptions = JsonSerializerOptions.Web
            })).ToDictionary(f => f.Name, StringComparer.Ordinal);
    }

    public IEnumerable<McpOperation> Tools => functions.Values.Select(f =>
        new McpOperation(f.Name, f.Description ?? "", f.JsonSchema, f.Name, "client"));

    public async ValueTask<JsonElement> CallAsync(string operationId, JsonElement input, RequestIdentity caller, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var function = functions[operationId];
        var args = new AIFunctionArguments();
        foreach (var arg in input.EnumerateObject()) args[arg.Name] = arg.Value;
        return JsonSerializer.SerializeToElement(await function.InvokeAsync(args, ct), JsonSerializerOptions.Web);
    }

    [DisplayName("mix_projects"),Description("Versioned shelf mixes. Actions list, create(manifest), get, save, snapshot, render, history, rollback(revision), regenerate(revision). Clip actions place_clip, move_clip, trim_clip, fade_clip, loop_clip, delete_clip take clip {lane,id,...}. place_clip source {take,short_recipe or loop_job optional}, 48000 frames/sec. DSP actions add_dsp, set_dsp, move_dsp, delete_dsp take dsp {lane,id,type?,parameters?,index?}. Types gain_pan, hpf, eq, compressor, limiter, reverb. The lane_mixer uses lane gain and pan. Mutations use project and expected head. Poll get_export for recipe.analysis (peak_dbfs, integrated_lufs, clipped_samples). Outputs are pending.")]
    public object MixProjects(MixProjectCommand input)=>store.MixProjects(input);
    [DisplayName("stage_track_audio"),Description("Stage up to 384 KiB of WAV bytes (base64). Pass returned SHA-256 values in file order to import_recording. Chunks expire after one hour of inactivity.")]
    public Task<StagedChunk> StageAudio(AudioChunk chunk,CancellationToken cancellationToken)
        =>store.StageAudio(chunk,cancellationToken);
    [DisplayName("import_recording"),Description("Import an ordinary WAV using a title and ordered SHA-256 chunk IDs from stage_track_audio. Returns the shelf track ID.")]
    public Task<ImportedTrack> ImportRecording(RecordingImport input,CancellationToken cancellationToken)=>store.ImportRecording(input,cancellationToken);
    [DisplayName("tune_loop"),Description("Tune loop edges and crossfade duration from stereo samples near the requested range. Pass the returned start, end and crossfade_ms to export_track with kind loop.")]
    public Task<TunedLoop> TuneLoop(LoopSuggestion request,CancellationToken cancellationToken)=>store.TuneLoop(request,cancellationToken);
    [DisplayName("get_export"),Description("Read the state of an export job and its output recipe and download path.")]
    public ExportJob ExportJob(string id)=>store.GetExport(id);
    [DisplayName("export_track"),Description("Render a tuned loop from a shelf take. Use kind loop and loop {start,end,crossfade_ms}, then poll get_export.")]
    public ExportJob ExportTrack(CreatorExport request)=>store.Export(request);
    [DisplayName("get_music_versions"),Description("Identify the connected shelf and its DTM API release.")]
    public object MixVersion()=>store.MixVersion();
    [DisplayName("list_studio_requests"),Description("Find unanswered requests from people using the composition GUI. Includes the draft as shared and the person's question. Use this for assistance with an open screen.")]
    public List<Consultation> StudioRequests()=>store.OpenConsultations();
    [DisplayName("get_studio_request"),Description("Read one composition GUI request, its draft, and whether the person applied or dismissed your suggestion.")]
    public Consultation StudioRequest(string id)=>store.ConsultationById(id);
    [DisplayName("suggest_in_studio"),Description("Send a suggestion to the originating composition screen. Respond in the user's language. Supply only changed draft fields. For lyrics use singable lines only. The person previews and applies the changes, then can queue the music from their screen. For advice alone omit changes.")]
    public Consultation SuggestInStudio(string id,string reply,DraftFields? changes=null)=>store.Suggest(id,reply,changes);
    [DisplayName("submit_job"),Description("Queue music generation or editing. Use generate_bgm or generate_vocal, a theme, lyrics for vocals, style, target seconds and optional readings. Returns the persistent job ID.")]
    public Job Submit(JobSpec spec)=>store.Submit(spec with { creator = "codex" });
    [DisplayName("get_job"),Description("Read job state, queue position, node and final provenance.")] public Job Get(string id)=>store.GetJob(id);
    [DisplayName("list_jobs"),Description("List household jobs.")] public List<Job> Jobs()=>store.Jobs();
    [DisplayName("get_nodes"),Description("Read both workers' availability.")] public object Nodes()=>store.Nodes();
    [DisplayName("list_tracks"),Description("List tracks with playback links.")] public List<Track> Tracks()=>store.Tracks();
    [DisplayName("get_track"),Description("Get lyrics, provenance, playback URL and review state.")] public Track Track(string id)=>store.GetTrack(id);
    [DisplayName("get_generation"),Description("Read generation.json with the final model arguments and master SHA-256.")]
    public CompositionDocument Generation(string track)=>store.Generation(track);
    [DisplayName("replay_generation"),Description("Queue the frozen take and verify the master SHA-256.")]
    public Job Replay(CompositionDocument document)
    {
        document.Check();
        if (document.take==null) throw new ArgumentException("A frozen take is required.");
        return Submit(document.job with {replay=document.take});
    }
    [DisplayName("fix_readings"),Description("Queue a vocal remake with whole-sheet reading substitutions.")] public Job Fix(string track,Dictionary<string,string> readings)=>store.Submit(new("regen_reading_fix",source:track,readings:readings,creator:"codex"));
    [DisplayName("mix_tracks"),Description("Queue a join with crossfade seconds or layer tracks, with per-track linear gain.")] public Job Mix(Clip[] clips,double crossfade=0,string mode="join")=>store.Submit(new("mix",clips:clips,crossfade:crossfade,mode:mode,creator:"codex"));
    [DisplayName("edit_track"),Description("Queue a trim, fades, timing changes or timed lyric cues. To replace an interval without changing its length, specify start, end, replacement (track ID), and replacement_start. Times are seconds.")]
    public Job Edit(string track,double start=0,double? end=null,double fade_in=0,double fade_out=0,Cue[]? cues=null,TimingPoint[]? timing=null,string? replacement=null,double replacement_start=0)
        =>store.Submit(new("edit",source:track,start:start,end:end,fade_in:fade_in,fade_out:fade_out,cues:cues,timing:timing,creator:"codex",replacement:replacement,replacement_start:replacement_start));
    [DisplayName("get_review"),Description("Read adoption and favorite status.")] public Review GetReview(string track){var t=store.GetTrack(track);return new(t.status,t.favorite);}
    [DisplayName("set_review"),Description("Update adoption (pending, adopted, rejected) or favorite. Reviews made through MCP are recorded as codex.")] public Track SetReview(string track,Review review)=>store.SetReview(track,review,"codex");
}
