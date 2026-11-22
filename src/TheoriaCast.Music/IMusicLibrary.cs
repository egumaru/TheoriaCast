namespace TheoriaCast.Music;

public interface IMusicLibrary
{
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
