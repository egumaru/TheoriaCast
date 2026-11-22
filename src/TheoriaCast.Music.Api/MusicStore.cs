using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TheoriaCast.Music.Api;

public sealed partial class MusicStore : IMusicLibrary
{
    public readonly string Root;
    private readonly string connection;
    private readonly IReadOnlyDictionary<string,string[]> capabilities;
    public MusicStore(string root, IReadOnlyDictionary<string,string[]>? capabilities=null)
    {
        Root = Path.GetFullPath(root);
        this.capabilities=capabilities??new Dictionary<string,string[]>();
        Directory.CreateDirectory(Root);
        Delivery.CleanPartialFiles(new DirectoryInfo(Path.Combine(Root,"partial")));
        connection = new SqliteConnectionStringBuilder { DataSource = Path.Combine(Root, "music.sqlite") }.ToString();
        using var db = Open();
        using var cmd = Command(db, """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS jobs(id TEXT PRIMARY KEY, spec TEXT NOT NULL, state TEXT NOT NULL,
              node TEXT, lease TEXT, expires INTEGER, progress INTEGER DEFAULT 0, detail TEXT DEFAULT '',
              created TEXT NOT NULL, priority INTEGER NOT NULL, role TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS dispatch ON jobs(state,role,priority);
            CREATE TABLE IF NOT EXISTS tracks(id TEXT PRIMARY KEY, title TEXT, folder TEXT, origin TEXT,
              status TEXT DEFAULT 'pending', favorite INTEGER DEFAULT 0);
            CREATE TABLE IF NOT EXISTS nodes(id TEXT PRIMARY KEY, report TEXT, seen INTEGER);
            CREATE TABLE IF NOT EXISTS works(name TEXT PRIMARY KEY, track TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS pending_exports(track TEXT PRIMARY KEY);
            CREATE TABLE IF NOT EXISTS consultations(
                id TEXT PRIMARY KEY, status TEXT NOT NULL, question TEXT NOT NULL, draft TEXT NOT NULL,
                reply TEXT, changes TEXT, created TEXT NOT NULL);
            """);
        cmd.ExecuteNonQuery();
        using (var migration=db.BeginTransaction())
        {
            if (Value(db,"SELECT 1 FROM pragma_table_info('jobs') WHERE name='attempts'")==null)
                Run(db,"ALTER TABLE jobs ADD COLUMN attempts INTEGER NOT NULL DEFAULT 0");
            foreach (var table in new[]{"jobs","tracks","works"})
            {
                Run(db,"CREATE TABLE IF NOT EXISTS revisions(name TEXT PRIMARY KEY,version INTEGER NOT NULL)");
                Run(db,"INSERT OR IGNORE INTO revisions VALUES($p0,0)",table);
                foreach (var op in new[]{"INSERT","UPDATE","DELETE"})
                    Run(db,$"CREATE TRIGGER IF NOT EXISTS rev_{table}_{op} AFTER {op} ON {table} BEGIN UPDATE revisions SET version=version+1 WHERE name='{table}'; END");
            }
            migration.Commit();
        }
        FlushReviews();
    }
    private SqliteConnection Open() { var db = new SqliteConnection(connection); db.Open(); return db; }
    private static SqliteCommand Command(SqliteConnection db, string sql, params object?[] args)
    {
        var cmd = db.CreateCommand(); cmd.CommandText = sql;
        for (int i = 0; i < args.Length; i++) cmd.Parameters.AddWithValue("$p"+i, args[i] ?? DBNull.Value);
        return cmd;
    }
    private static void Run(SqliteConnection db,string sql,params object?[] args)
    { using var cmd = Command(db,sql,args); cmd.ExecuteNonQuery(); }
    private static object? Value(SqliteConnection db,string sql,params object?[] args)
    { using var cmd = Command(db,sql,args); return cmd.ExecuteScalar(); }
    private static List<T> Read<T>(SqliteConnection db,string sql,Func<SqliteDataReader,T> map,params object?[] args)
    {
        using var cmd = Command(db,sql,args); using var rows = cmd.ExecuteReader();
        var list = new List<T>(); while (rows.Read()) list.Add(map(rows)); return list;
    }
    private static void Reap(SqliteConnection db) => Run(db, """
        UPDATE jobs SET state=CASE WHEN attempts>=3 THEN 'failed' ELSE 'queued' END,node=NULL,lease=NULL,expires=NULL,progress=0,
          detail=CASE WHEN attempts>=3 THEN '担当期限が3回切れたため終了しました' ELSE '作る機器の再接続を待っています' END
        WHERE state='running' AND expires<unixepoch()
        """);
    private static string Id() => Guid.NewGuid().ToString("N");
    public Job Submit(JobSpec input)
    {
        var s = (input with { type = input.type switch { "bgm"=>"generate_bgm","vocal"=>"generate_vocal",_=>input.type } }).Check();
        using var db = Open();
        if (s.type is "edit" or "regen_reading_fix")
        {
            var parent = GetTrack(s.source!);
            if (s.replacement != null)
            {
                var donor = GetTrack(s.replacement);
                if (s.end > (double?)parent.provenance["actual_seconds"] + .001 ||
                    s.replacement_start + s.end - s.start > (double?)donor.provenance["actual_seconds"] + .001)
                    throw new ArgumentException("差し替える範囲が曲の長さを超えています。");
            }
            if (s.type == "regen_reading_fix")
            {
                var original = parent.provenance["generation"]?.Deserialize<JobSpec>()
                    ?? throw new ArgumentException("この曲には歌の生成条件がありません。");
                if (string.IsNullOrWhiteSpace(original.lyrics)) throw new ArgumentException("歌のある曲を選んでください。");
                var changes = new Dictionary<string,string>(original.readings ?? []);
                if(s.readings!.Keys.Any(word=>!original.lyrics.Contains(word,StringComparison.Ordinal)))
                    throw new ArgumentException("読みを直すことばが元の歌詞にありません。");
                foreach (var r in s.readings!)
                {
                    foreach(var longer in changes.Keys.Where(k=>k!=r.Key && k.Contains(r.Key,StringComparison.Ordinal)).ToArray())
                        changes.Remove(longer);
                    changes[r.Key] = r.Value;
                }
                s = original with { type = s.type, source = s.source, priority = s.priority, readings = changes, creator=s.creator,
                    preferred_node=s.preferred_node??original.preferred_node,replay=null };
            }
            if(s.type=="edit" && !s.assign_only && (double?)parent.provenance["actual_seconds"] is double duration)
            {
                var last=s.timing?.LastOrDefault();
                if(last!=null && last.from>duration) throw new ArgumentException("移動する位置が曲の長さを超えています。");
                var warped=duration+(last==null?0:last.to-last.from);
                var finish=s.end??warped;
                if(finish>warped+.001 || finish<=s.start || Math.Max(s.fade_in,s.fade_out)>finish-s.start ||
                   s.cues?.Any(c=>c.end>finish-s.start+.001)==true)
                    throw new ArgumentException("切り取り、字幕、フェードの範囲が曲の長さを超えています。");
            }
        }
        if (s.type == "mix") foreach(var clip in s.clips!)
        {
            var track=GetTrack(clip.track);
            if (s.mode=="join" && s.crossfade>0 && s.crossfade>=(double?)track.provenance["actual_seconds"])
                throw new ArgumentException("つなぎ目の長さを各曲より短く指定してください。");
        }
        bool Supports(string node)
        {
            if (capabilities.TryGetValue(node,out var types)) return types.Contains(s.type);
            return Value(db,"SELECT 1 FROM nodes,json_each(nodes.report,'$.types') j WHERE nodes.id=$p0 AND j.value=$p1",node,s.type)!=null;
        }
        var order=s.preferred_node!=null?new[]{s.preferred_node}:s.type is "generate_vocal" or "regen_reading_fix"?new[]{"B","A"}:new[]{"A","B"};
        var role=order.FirstOrDefault(Supports)??throw new ArgumentException("この種類の曲を処理するエンジンが設定されていません。");
        var id = Id();
        Run(db,"INSERT INTO jobs(id,spec,state,created,priority,role) VALUES($p0,$p1,'queued',$p2,$p3,$p4)",
            id, JsonSerializer.Serialize(s),DateTimeOffset.UtcNow.ToString("O"),s.priority == "interactive" ? 0 : 1,
            role);
        return GetJob(id);
    }
    private const string JobQuery = """
        SELECT j.id,j.spec,j.state,j.node,j.progress,j.detail,j.created,
          CASE WHEN j.state='queued' THEN 1+(SELECT count(*) FROM jobs k WHERE k.state='queued' AND k.role=j.role
          AND (k.priority<j.priority OR (k.priority=j.priority AND k.rowid<j.rowid))) END,t.id,t.origin
        FROM jobs j LEFT JOIN tracks t ON t.id=CASE WHEN j.state='done' AND json_extract(j.spec,'$.assign_only')=1
            THEN json_extract(j.spec,'$.source') ELSE j.id END
        """;
    private static Job ToJob(SqliteDataReader r) => new(r.GetString(0),JsonSerializer.Deserialize<JobSpec>(r.GetString(1))!,
        r.GetString(2),r.IsDBNull(3)?null:r.GetString(3),r.GetInt32(4),r.GetString(5),r.GetString(6),
        r.IsDBNull(7)?null:r.GetInt32(7),r.IsDBNull(8)?null:r.GetString(8),r.IsDBNull(9)?null:JsonNode.Parse(r.GetString(9)));
    public Job GetJob(string id) { using var db = Open(); Reap(db);
        return Read(db,JobQuery+" WHERE j.id=$p0",ToJob,id).SingleOrDefault() ?? throw new KeyNotFoundException(); }
    public List<Job> Jobs() { using var db = Open(); Reap(db); return Read(db,JobQuery+" ORDER BY j.rowid DESC",ToJob); }
    public object Revisions() { using var db=Open(); Reap(db); return Read(db,"SELECT name,version FROM revisions",r=>(r.GetString(0),r.GetInt64(1))).ToDictionary(p=>p.Item1,p=>p.Item2); }
    public Job Cancel(string id) { using var db = Open();
        Run(db,"UPDATE jobs SET state='cancelled',lease=NULL WHERE id=$p0 AND state IN ('queued','running')",id); return GetJob(id); }
    public Claim? Poll(NodeReport report, bool claim)
    {
        if (report.node is not ("A" or "B")) throw new ArgumentException("Unknown node");
        using var db = Open(); using var tx = db.BeginTransaction();
        Reap(db);
        Run(db,"INSERT OR REPLACE INTO nodes VALUES($p0,$p1,unixepoch())",report.node,JsonSerializer.Serialize(report));
        Claim? result = null;
        if (claim && report.ready && report.free_mib >= 8192 &&
            Value(db,"SELECT id FROM jobs WHERE node=$p0 AND state='running'",report.node) == null)
        {
            var next = Read(db,"""
                SELECT id,spec FROM jobs WHERE state='queued' AND role=$p0
                AND json_extract(spec,'$.type') IN (SELECT value FROM json_each($p1))
                ORDER BY priority,rowid LIMIT 1
                """, r => new Claim(r.GetString(0),Id(),JsonSerializer.Deserialize<JobSpec>(r.GetString(1))!),
                report.node,JsonSerializer.Serialize(report.types)).SingleOrDefault();
            if (next != null) { Run(db,"UPDATE jobs SET state='running',node=$p0,lease=$p1,expires=unixepoch()+90,attempts=attempts+1,detail='' WHERE id=$p2",report.node,next.lease,next.id); result=next; }
        }
        tx.Commit(); return result;
    }
    public object Nodes()
    {
        using var db = Open(); Reap(db);
        return Read(db,"""
            WITH roles(id) AS (VALUES('A'),('B'))
            SELECT roles.id,n.report,coalesce(n.seen>=unixepoch()-45,0),j.id FROM roles
            LEFT JOIN nodes n ON n.id=roles.id LEFT JOIN jobs j ON j.node=roles.id AND j.state='running'
            """,r=>new {node=r.GetString(0),report=r.IsDBNull(1)?null:JsonNode.Parse(r.GetString(1)),
                online=r.GetBoolean(2),job=r.IsDBNull(3)?null:r.GetString(3)});
    }
    public bool Pending(string node)
    {
        using var db=Open(); Reap(db);
        return Value(db,"SELECT 1 FROM jobs WHERE role=$p0 AND state='queued' LIMIT 1",node)!=null;
    }
    static void Owned(SqliteConnection db,string id,string lease)
    {
        if (Value(db,"SELECT 1 FROM jobs WHERE id=$p0 AND lease=$p1 AND state='running' AND expires>=unixepoch()",id,lease)==null)
            throw new InvalidOperationException("この作業の担当期限が終了しました。");
    }
    public void Progress(string id,Update u)
    {
        using var db=Open(); using var tx=db.BeginTransaction(); Owned(db,id,u.lease);
        if (u.state is not ("running" or "queued" or "failed")) throw new ArgumentException("Invalid state");
        Run(db,"""
            UPDATE jobs SET state=$p0,progress=$p1,detail=$p2,expires=unixepoch()+90,
            attempts=CASE WHEN $p0='queued' THEN max(0,attempts-1) ELSE attempts END,
            node=CASE WHEN $p0='queued' THEN NULL ELSE node END,lease=CASE WHEN $p0='running' THEN lease END WHERE id=$p3
            """,u.state,Math.Clamp(u.progress,0,99),u.detail,id);
        tx.Commit();
    }
    public void CheckOwner(string id,string lease) { using var db=Open(); Owned(db,id,lease); }
    public void Complete(string id,string lease,string stage,JsonObject origin)
    {
        using var db=Open(); using var tx=db.BeginTransaction(); Owned(db,id,lease);
        var s=JsonSerializer.Deserialize<JobSpec>((string)Value(db,"SELECT spec FROM jobs WHERE id=$p0",id)!)!;
        string relative=Path.Combine("生成音源","TheoriaCast",id,"take-"+lease);
        string dest=Path.Combine(Root,relative); Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        string title=s.theme.Length>0?s.theme:"編集した曲";
        Run(db,"INSERT INTO tracks(id,title,folder,origin) VALUES($p0,$p1,$p2,$p3)",id,title,relative,origin.ToJsonString());
        Run(db,"UPDATE jobs SET state='done',progress=100,detail='',lease=NULL WHERE id=$p0",id);
        if (!string.IsNullOrWhiteSpace(s.work)) Run(db,"INSERT OR REPLACE INTO works VALUES($p0,$p1)",s.work,id);
        Directory.Move(stage,dest);
        try { tx.Commit(); } catch { Directory.Move(dest,stage); throw; }
    }
    private Track ToTrack(SqliteDataReader r) => new(r.GetString(0),r.GetString(1),"/tracks/"+r.GetString(0)+"/audio",
        Path.Combine(Root,r.GetString(2),"master.wav"),JsonNode.Parse(r.GetString(3))!,r.GetString(4),r.GetBoolean(5));
    public List<Track> Tracks() { using var db=Open();return Read(db,"SELECT * FROM tracks ORDER BY rowid DESC",ToTrack); }
    public Track GetTrack(string id) { using var db=Open();return Read(db,"SELECT * FROM tracks WHERE id=$p0",ToTrack,id).SingleOrDefault() ?? throw new KeyNotFoundException(); }
    public CompositionDocument Generation(string id)
    {
        var path=Path.Combine(Path.GetDirectoryName(GetTrack(id).file_path)!,"generation.json");
        if (!File.Exists(path)) throw new KeyNotFoundException("This track has no frozen generation.");
        return CompositionFiles.Parse(File.ReadAllText(path));
    }
    public Track SetReview(string id,Review r,string reviewer="human")
    {
        if (r.status is not (null or "pending" or "adopted" or "rejected")) throw new ArgumentException("Invalid review");
        using var db=Open();
        using(var tx=db.BeginTransaction())
        {
            var track=Read(db,"SELECT * FROM tracks WHERE id=$p0",ToTrack,id).SingleOrDefault() ?? throw new KeyNotFoundException();
            string status=r.status??track.status;
            bool favorite=r.favorite??track.favorite;
            var review=track.provenance["review"]?.DeepClone() ?? new JsonObject();
            if(r.status!=null || review["status"]==null)
            {
                review["status"]=status=="adopted"?"approved":status;
                review["reviewer"]=status=="pending"?null:reviewer;
                review["reviewed_at"]=status=="pending"?null:DateTimeOffset.UtcNow.ToString("O");
            }
            review["favorite"]=favorite;
            track.provenance["review"]=review;
            Run(db,"UPDATE tracks SET status=$p0,favorite=$p1,origin=$p2 WHERE id=$p3",status,favorite,track.provenance.ToJsonString(),id);
            Run(db,"INSERT OR IGNORE INTO pending_exports VALUES($p0)",id);
            tx.Commit();
        }
        FlushReviews();
        return GetTrack(id);
    }
    private void FlushReviews()
    {
        using var db=Open(); using var tx=db.BeginTransaction();
        foreach(var track in Read(db,"SELECT t.* FROM tracks t JOIN pending_exports p ON p.track=t.id",ToTrack))
        {
            string dir=Path.GetDirectoryName(track.file_path)!;
            foreach(var file in Directory.EnumerateFiles(dir,"*.json").Where(f=>f.EndsWith(".wav.json")||f.EndsWith(".mp3.json")))
            {
                var side=JsonNode.Parse(File.ReadAllText(file))!;
                side["review"]=track.provenance["review"]?.DeepClone();
                File.WriteAllText(file+".new",side.ToJsonString(new JsonSerializerOptions{WriteIndented=true}));
                File.Move(file+".new",file,true);
            }
            Run(db,"DELETE FROM pending_exports WHERE track=$p0",track.id);
        }
        tx.Commit();
    }
    public object Works() { using var db=Open(); return Read(db,"SELECT name,track FROM works ORDER BY name",r=>new {name=r.GetString(0),track=r.GetString(1)}); }
    public void Assign(string id,string lease)
    {
        using var db=Open(); using var tx=db.BeginTransaction(); Owned(db,id,lease);
        var spec=JsonSerializer.Deserialize<JobSpec>((string)Value(db,"SELECT spec FROM jobs WHERE id=$p0",id)!)!;
        if(!spec.assign_only || spec.source==null || spec.work==null) throw new ArgumentException("使用曲の予約がありません。");
        Run(db,"INSERT OR REPLACE INTO works VALUES($p0,$p1)",spec.work,spec.source);
        Run(db,"UPDATE jobs SET state='done',progress=100,detail='',lease=NULL WHERE id=$p0",id);
        tx.Commit();
    }
}
