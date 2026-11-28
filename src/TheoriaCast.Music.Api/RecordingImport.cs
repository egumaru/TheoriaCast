using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TheoriaCast.Music.Api;

public sealed partial class MusicStore
{
    private const int ChunkLimit = 384 * 1024;
    private const int AudioLimit = 512 * 1024 * 1024;
    private const int MaxChunks = (AudioLimit + ChunkLimit - 1) / ChunkLimit;
    private string ChunkFolder => Path.Combine(Root,"import-chunks");
    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);

    public async Task<StagedChunk> StageAudio(AudioChunk chunk,CancellationToken ct)
    {
        if (chunk?.wav is not { Length: > 0 and <= ChunkLimit } bytes)
            throw new InvalidDataException("A WAV chunk must contain 1 to 393216 bytes.");
        var digest=Convert.ToHexStringLower(SHA256.HashData(bytes));
        var folder=Directory.CreateDirectory(ChunkFolder);
        Delivery.CleanPartialFiles(folder);
        var destination=Path.Combine(folder.FullName,digest+".part");
        var temp=Path.Combine(folder.FullName,Id()+".part");
        try
        {
            await File.WriteAllBytesAsync(temp,bytes,ct);
            // Rename a fully written chunk. Parallel retries publish the same bytes.
            File.Move(temp,destination,true);
        }
        finally { File.Delete(temp); }
        return new(digest);
    }

    public async Task<ImportedTrack> ImportRecording(RecordingImport input,CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input?.title) || input.title.Length>200 || input.chunks is not {Length:>0 and <=MaxChunks} || input.chunks.Any(h=>!IsHash(h)))
            throw new InvalidDataException("Supply a title and ordered WAV chunks.");
        var stage=Directory.CreateDirectory(Path.Combine(Root,"incoming",Id())).FullName;
        try
        {
            var master=Path.Combine(stage,"master.wav");
            await Assemble(master,null,input.chunks,ct);
            var wave=await Waveform.ReadAsync(master,0,null,ct);
            using var stream=File.OpenRead(master);
            var digest=Convert.ToHexStringLower(await SHA256.HashDataAsync(stream,ct));
            stream.Close();
            var origin=JsonSerializer.SerializeToNode(new {schema_version=1,file="master.wav",sha256=digest,
                actual_seconds=wave.Duration,created_at=DateTimeOffset.UtcNow,processing=new[]{new {operation="recording_import"}},
                review=new {status="pending"}})!.AsObject();
            await File.WriteAllTextAsync(master+".json",origin.ToJsonString(),ct);
            return RegisterImport(stage,input.title.Trim(),origin,digest);
        }
        finally {if (Directory.Exists(stage)) Directory.Delete(stage,true);}
    }

    private async Task Assemble(string path,byte[]? wav,string[]? chunks,CancellationToken ct)
    {
        await using var file=File.Create(path);
        if (wav!=null) { await file.WriteAsync(wav,ct); return; }
        foreach (var digest in chunks!)
        {
            var part=Path.Combine(ChunkFolder,digest.ToLowerInvariant()+".part");
            try
            {
                using var block=new FileStream(part,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete);
                if (file.Length+block.Length>AudioLimit) throw new InvalidDataException("WAV exceeds 512 MiB.");
                await block.CopyToAsync(file,ct);
            }
            catch (IOException e) when (e is FileNotFoundException or DirectoryNotFoundException)
            { throw new InvalidDataException("A staged WAV chunk is missing or has expired. Stage it again."); }
        }
    }

    private ImportedTrack RegisterImport(string stage,string title,JsonObject origin,string hash)
    {
        using var db=Open();
        using var tx=db.BeginTransaction(deferred:false);
        if (Value(db,"SELECT id FROM tracks WHERE lower(json_extract(origin,'$.sha256'))=$p0 ORDER BY rowid LIMIT 1",hash) is string existing)
        {
            tx.Commit();
            return new(existing,hash,false);
        }
        var id=Id();
        var relative=Path.Combine("生成音源","TheoriaCast",id,"recording");
        var destination=Path.Combine(Root,relative);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        Run(db,"INSERT INTO tracks(id,title,folder,origin) VALUES($p0,$p1,$p2,$p3)",id,title,relative,origin.ToJsonString());
        Directory.Move(stage,destination);
        try { tx.Commit(); }
        catch { Directory.Move(destination,stage); throw; }
        return new(id,hash,true);
    }
}
