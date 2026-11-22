using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TheoriaCast.Music.Api;
public static class Delivery
{
    internal const int PartBytes = 8 << 20;
    internal const long MaxArchiveBytes = 513L << 20; // uncompressed files plus ZIP headers

    public static async Task<bool> ReceivePart(string id,string lease,long offset,long length,Stream body,MusicStore db,CancellationToken ct)
    {
        db.CheckOwner(id,lease);
        if (length <= PartBytes || length > MaxArchiveBytes || offset < 0 || offset >= length || offset % PartBytes != 0)
            throw new InvalidDataException("音声の転送位置またはサイズが無効です。");
        var bytes = new byte[(int)Math.Min(PartBytes,length-offset)];
        try { await body.ReadExactlyAsync(bytes,ct); }
        catch (EndOfStreamException) { throw new InvalidDataException("音声の転送が途中で終わりました。"); }
        if (await body.ReadAsync(new byte[1],ct) != 0) throw new InvalidDataException("転送サイズが一致しません。");
        var dir = Directory.CreateDirectory(Path.Combine(db.Root,"partial"));
        if (offset == 0) CleanPartialFiles(dir);
        // CheckOwner accepts only the generated job and lease IDs held in SQLite.
        var path = Path.Combine(dir.FullName,id+"-"+lease+".part");
        bool completed = false;
        try
        {
            await using var file = new FileStream(path,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
            if (file.Length < offset) throw new InvalidOperationException("音声の転送順序を確認してください。");
            file.Position = offset;
            await file.WriteAsync(bytes,ct);
            if (offset+bytes.Length < length) return false;
            file.SetLength(length);
            file.Position = 0;
            await Receive(id,lease,file,db,ct);
            completed = true;
            return true;
        }
        finally { if (completed) File.Delete(path); }
    }

    internal static void CleanPartialFiles(DirectoryInfo dir)
    {
        if (!dir.Exists) return;
        var oldest = DateTime.UtcNow.AddHours(-1);
        foreach (var file in dir.EnumerateFiles("*.part"))
        {
            try { if (file.LastWriteTimeUtc < oldest) file.Delete(); }
            catch (IOException) { } // An active upload owns the file.
        }
    }

    static readonly HashSet<string> Allowed=["raw.wav","master.wav","preview.mp3","master.wav.json","score.abc","lyrics.txt","singing.txt","lyrics.vtt","prompt.md","generation.json"];
    public static async Task Receive(string id,string lease,Stream body,MusicStore db,CancellationToken ct)
    {
        db.CheckOwner(id,lease);
        string stage=Path.Combine(db.Root,"incoming",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            var zipPath=Path.Combine(stage,"upload.zip");
            await using (var file=File.Create(zipPath)) await body.CopyToAsync(file,ct);
            using(var zip=ZipFile.OpenRead(zipPath))
            {
                long total=0;
                var names=new HashSet<string>(StringComparer.Ordinal);
                foreach(var entry in zip.Entries)
                {
                    if(!Allowed.Contains(entry.FullName) || !names.Add(entry.FullName) || entry.Length<=0 || entry.Length>(512L<<20)-total)
                        throw new InvalidDataException("成果物の形式または容量を確認してください。");
                    total+=entry.Length;
                    entry.ExtractToFile(Path.Combine(stage,entry.FullName));
                }
            }
            File.Delete(zipPath);
            var master=Path.Combine(stage,"master.wav");
            var meta=master+".json";
            if(!File.Exists(master)||!File.Exists(meta)||!File.Exists(Path.Combine(stage,"preview.mp3"))) throw new InvalidDataException("音声または出自がありません。");
            var origin=JsonNode.Parse(await File.ReadAllTextAsync(meta,ct)) as JsonObject ?? throw new InvalidDataException("出自が無効です。");
            foreach(var field in new[]{"model","model_version","license","params","machine","gpu","created_at","processing","operator"})
                if(origin[field]==null) throw new InvalidDataException("出自に必要な項目 "+field);
            if (origin["processing"] is not JsonArray || origin["singing_check"] is not (null or JsonObject))
                throw new InvalidDataException("Invalid provenance structure.");
            origin.Remove("replay_verified");
            var job=db.GetJob(id);
            if((job.spec.type is "generate_vocal" or "regen_reading_fix") && origin["singing_check"]?["passed"]==null)
                throw new InvalidDataException("歌唱照合がありません。");
            origin["job_id"]=id; origin["node"]=job.node; origin["file"]="master.wav";
            origin["sha256"]=Hash(master);
            var files=CompositionFiles.Materials.Where(name=>File.Exists(Path.Combine(stage,name))).ToArray();
            origin["materials"]=JsonSerializer.SerializeToNode(files);
            if (files.Contains("generation.json") || job.spec.replay!=null)
            {
                var recipe=Path.Combine(stage,"generation.json");
                if (!File.Exists(recipe) || new FileInfo(recipe).Length>(4<<20)) throw new InvalidDataException("再現用 JSON が無効です。");
                var take=CompositionFiles.Parse(await File.ReadAllTextAsync(recipe,ct)).take;
                if (take==null || take.master_sha256!=(string?)origin["sha256"] || (job.spec.replay is {} expected &&
                    !JsonNode.DeepEquals(JsonSerializer.SerializeToNode(take),JsonSerializer.SerializeToNode(expected))))
                    throw new InvalidDataException("生成 WAV または確定引数が再現用 JSON と一致しません。");
                if (job.spec.replay!=null) origin["replay_verified"]=true;
            }
            var options=new JsonSerializerOptions{WriteIndented=true};
            await File.WriteAllTextAsync(meta,origin.ToJsonString(options),ct);
            foreach(var name in new[]{"preview.mp3","raw.wav"})
            {
                var path=Path.Combine(stage,name); if(!File.Exists(path)) continue;
                var side=origin.DeepClone();
                side["file"]=name; side["sha256"]=Hash(path);
                if(name=="preview.mp3") side["processing"]!.AsArray().Add(new JsonObject{["operation"]="MP3 encode",["at"]=DateTimeOffset.UtcNow.ToString("O"),["params"]=new JsonObject{["quality"]=2},["parent_sha256"]=origin["sha256"]!.DeepClone()});
                else
                {
                    side["processing"]=new JsonArray();
                    if(side["raw_seconds"] is JsonNode rawDuration)
                    {
                        side["actual_seconds"]=rawDuration.DeepClone();
                        if(side["measurements"] is JsonObject measured) measured["audio_seconds"]=rawDuration.DeepClone();
                    }
                }
                await File.WriteAllTextAsync(path+".json",side.ToJsonString(options),ct);
            }
            await File.WriteAllTextAsync(Path.Combine(stage,"job.json"),JsonSerializer.Serialize(job.spec,options),ct);
            db.Complete(id,lease,stage,origin);
        }
        finally { if(Directory.Exists(stage)) Directory.Delete(stage,true); }
    }
    static string Hash(string file) { using var stream=File.OpenRead(file);return Convert.ToHexStringLower(SHA256.HashData(stream)); }
}
