using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace TheoriaCast.Music.Api;

public sealed partial class MusicStore
{
    private const string ConsultationSelect = "SELECT id,status,question,draft,reply,changes,created FROM consultations";

    private static Consultation ReadConsultation(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), JsonSerializer.Deserialize<DraftFields>(r.GetString(3))!,
        r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : JsonSerializer.Deserialize<DraftFields>(r.GetString(5)),
        r.GetString(6));

    public List<Consultation> OpenConsultations()
    {
        using var db = Open();
        return Read(db, ConsultationSelect + " WHERE status='waiting' ORDER BY rowid", ReadConsultation);
    }

    public Consultation ConsultationById(string id)
    {
        using var db = Open();
        return Read(db, ConsultationSelect + " WHERE id=$p0", ReadConsultation, id).SingleOrDefault()
            ?? throw new KeyNotFoundException();
    }

    public Consultation Ask(StudioQuestion request)
    {
        if (string.IsNullOrWhiteSpace(request.question) || request.question.Length > 3000 || request.draft is null)
            throw new ArgumentException("相談したいことを3000文字以内で入力してください。");
        request.draft.Validate();
        using var db = Open();
        using var tx = db.BeginTransaction();
        if (request.replaces != null)
            Run(db, "UPDATE consultations SET status='closed' WHERE id=$p0 AND status IN ('waiting','suggested')", request.replaces);
        var id = Id();
        Run(db, "INSERT INTO consultations(id,status,question,draft,created) VALUES($p0,'waiting',$p1,$p2,$p3)",
            id, request.question.Trim(), JsonSerializer.Serialize(request.draft), DateTimeOffset.UtcNow.ToString("O"));
        tx.Commit();
        return ConsultationById(id);
    }

    public Consultation Suggest(string id, string reply, DraftFields? changes)
    {
        if (string.IsNullOrWhiteSpace(reply) || reply.Length > 4000)
            throw new ArgumentException("提案の説明を4000文字以内で入力してください。");
        changes?.Validate();
        using var db = Open();
        using var cmd = Command(db, "UPDATE consultations SET status='suggested',reply=$p1,changes=$p2 WHERE id=$p0 AND status='waiting'",
            id, reply.Trim(), changes == null ? null : JsonSerializer.Serialize(changes));
        if (cmd.ExecuteNonQuery() != 1) throw new InvalidOperationException("この相談は回答済みか、別の相談に切り替わりました。");
        return ConsultationById(id);
    }

    public Consultation Decide(string id, string decision)
    {
        if (decision is not ("applied" or "closed")) throw new ArgumentException("提案の扱いを選んでください。");
        using var db = Open();
        using var cmd = Command(db, """
            UPDATE consultations SET status=$p1 WHERE id=$p0 AND
              (status=$p1 OR status='suggested' OR (status='waiting' AND $p1='closed'))
            """, id, decision);
        if (cmd.ExecuteNonQuery() != 1) throw new InvalidOperationException("この相談の状態が変わりました。");
        return ConsultationById(id);
    }
}
