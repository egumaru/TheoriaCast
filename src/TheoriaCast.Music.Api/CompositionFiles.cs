using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace TheoriaCast.Music.Api;

internal static class CompositionFiles
{
    private const int Limit = 4 << 20;
    private static readonly UTF8Encoding Utf8 = new(false,true);
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented=true };
    internal static readonly string[] Materials = ["prompt.md","lyrics.txt","score.abc","singing.txt","generation.json"];

    internal static CompositionDocument Parse(string text) =>
        (JsonSerializer.Deserialize<CompositionDocument>(text.TrimStart('\uFEFF'),Options)
         ?? throw new InvalidDataException("空の作曲ファイルです。")).Check();

    internal static async Task<object> Import(Stream input,CancellationToken ct)
    {
        using var data = new MemoryStream();
        var buffer = new byte[32768];
        int read;
        while ((read=await input.ReadAsync(buffer,ct))!=0)
        {
            if (data.Length+read>Limit) throw new InvalidDataException("作曲ファイルは4 MiBまでです。");
            data.Write(buffer,0,read);
        }
        data.Position=0;
        var entries=new Dictionary<string,string>(StringComparer.Ordinal);
        if (data.Length>=4 && data.GetBuffer()[0]=='P' && data.GetBuffer()[1]=='K')
        {
            using var zip=new ZipArchive(data,ZipArchiveMode.Read);
            long size=0;
            foreach (var item in zip.Entries)
            {
                if ((item.FullName!="composition.json" && !Materials.Contains(item.FullName)) ||
                    entries.ContainsKey(item.FullName) || item.Length>Limit-size)
                    throw new InvalidDataException("作曲セットのファイル名または容量を確認してください。");
                size+=item.Length;
                using var stream=item.Open();
                var bytes=new byte[(int)item.Length];
                await stream.ReadExactlyAsync(bytes,ct);
                if (stream.ReadByte()!=-1) throw new InvalidDataException("作曲セットの容量が一致しません。");
                entries.Add(item.FullName,Utf8.GetString(bytes).TrimStart('\uFEFF'));
            }
        }
        else
        {
            var doc=Parse(Utf8.GetString(data.ToArray()));
            return new {draft=doc with {take=null}, final=doc.take==null?null:doc};
        }
        var final=entries.TryGetValue("generation.json",out var rendered)?Parse(rendered):null;
        if (final!=null && final.take==null) throw new InvalidDataException("確定した生成引数がありません。");
        var draft=entries.TryGetValue("composition.json",out var form)?Parse(form):final;
        if (draft==null) throw new InvalidDataException("作曲条件の JSON がありません。");
        var s=draft.job;
        var materials=s with {prompt_md=entries.GetValueOrDefault("prompt.md")??s.prompt_md,
            lyrics=entries.GetValueOrDefault("lyrics.txt")??s.lyrics,score_abc=entries.GetValueOrDefault("score.abc")??s.score_abc};
        return new {draft=(draft with {job=materials,take=null}).Check(),final};
    }

    internal static Stream Pack(CompositionDocument doc,string? directory=null)
    {
        doc.Check();
        var output=new MemoryStream();
        using (var zip=new ZipArchive(output,ZipArchiveMode.Create,leaveOpen:true))
        {
            void Add(string name,string? value)
            {
                if (value==null) return;
                using var writer=new StreamWriter(zip.CreateEntry(name).Open(),new UTF8Encoding(false));
                writer.Write(value);
            }
            Add("composition.json",JsonSerializer.Serialize(doc with {take=null},Options));
            foreach (var name in Materials)
            {
                var path=directory==null?null:Path.Combine(directory,name);
                if (path!=null && File.Exists(path)) zip.CreateEntryFromFile(path,name);
                else Add(name,name switch {
                    "generation.json" when doc.take!=null => JsonSerializer.Serialize(doc,Options),
                    "prompt.md" => doc.job.prompt_md, "lyrics.txt" when doc.job.type=="generate_vocal" => doc.job.lyrics,
                    "score.abc" => doc.job.score_abc, _ => null
                });
            }
        }
        output.Position=0;
        return output;
    }

    internal static Stream TrackSet(Track track, Job job)
    {
        var dir=Path.GetDirectoryName(track.file_path)!;
        var path=Path.Combine(dir,"generation.json");
        if (File.Exists(path)) return Pack(Parse(File.ReadAllText(path)),dir);
        var spec=job.spec;
        if (spec.type=="regen_reading_fix") spec=spec with {type="generate_vocal",source=null};
        return Pack(new("TheoriaCast.Music",1,spec with {replay=null}),dir);
    }
}
