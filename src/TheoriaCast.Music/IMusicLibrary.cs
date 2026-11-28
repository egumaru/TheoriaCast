namespace TheoriaCast.Music;

public interface IMusicLibrary
{
    object MixProjects(MixProjectCommand input);
    object MixVersion();
    Task<StagedChunk> StageAudio(AudioChunk chunk,CancellationToken ct);
    Task<ImportedTrack> ImportRecording(RecordingImport input,CancellationToken ct);
    Task<TunedLoop> TuneLoop(LoopSuggestion request,CancellationToken ct);
    ExportJob Export(CreatorExport request);
    ExportJob GetExport(string id);
    Job Submit(JobSpec spec);
    Job GetJob(string id);
    List<Job> Jobs();
    object Nodes();
    List<Track> Tracks();
    CompositionDocument Generation(string id);
    Track GetTrack(string id);
    Track SetReview(string id, Review review, string reviewer="human");
    List<Consultation> OpenConsultations();
    Consultation ConsultationById(string id);
    Consultation Suggest(string id, string reply, DraftFields? changes);
}
