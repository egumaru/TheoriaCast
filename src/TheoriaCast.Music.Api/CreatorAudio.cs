using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TheoriaCast.Music.Api;

public sealed class CreatorAudio
{
    private const int Rate=48000;
    private readonly string ffmpeg;
    public CreatorAudio(string ffmpeg) { this.ffmpeg=ffmpeg; }

    internal static void CheckLoop(LoopSelection? value,double duration)
    {
        if (value is null || !double.IsFinite(value.start) || !double.IsFinite(value.end) ||
            value.start < 0 || value.end > duration || value.crossfade_ms is <10 or >100 ||
            value.end-value.start <= value.crossfade_ms*.002)
            throw new ArgumentException("曲内のループ範囲と 10〜100 ms のクロスフェードを指定してください。");
    }

    public async Task<TunedLoop> Tune(string input,LoopSelection selection,double duration,CancellationToken ct)
    {
        const int rate=LoopTuner.Rate;
        int frames=(int)Math.Floor(duration*rate),start=(int)Math.Round(selection.start*rate),end=Math.Min(frames,(int)Math.Round(selection.end*rate));
        async Task<SeamWindow> Window(int center){
            int first=Math.Max(0,center-LoopTuner.Radius),count=Math.Min(frames,center+LoopTuner.Radius)-first;
            using var output=new MemoryStream();
            await Run(["-ss",Number(first/(double)rate),"-i",input,"-t",Number(count/(double)rate),"-ac","2","-ar","6000","-f","f32le","pipe:1"],output,ct);
            int n=Math.Min(count,(int)output.Length/8);var samples=new float[n*2];Buffer.BlockCopy(output.GetBuffer(),0,samples,0,n*8);
            float[][] channels=[new float[n],new float[n]];
            for(int i=0;i<n;i++){channels[0][i]=samples[i*2];channels[1][i]=samples[i*2+1];}
            return new(first,channels);
        }
        return LoopTuner.Tune(new(frames,start,end,await Window(start),await Window(end)));
    }

    internal async Task<JsonObject> Render(ExportJob job,Track track,string directory,CancellationToken ct)
    {
        string master=track.file_path;
        await using (var wave=File.OpenRead(master))
            if (Convert.ToHexStringLower(await SHA256.HashDataAsync(wave,ct))!=job.source_sha256)
                throw new InvalidDataException("Source hash mismatch.");
        JsonObject? loopDetails=null;
        if (job.request.loop is {} loop)
        {
            var start=Frames(loop.start);
            var end=Frames(loop.end);
            var fade=Frames(loop.crossfade_ms/1000.0);
            var wave=Path.Combine(directory,"loop.wav");
            var filter=$"aresample={Rate},asplit=3[body][tail][head];"+
                $"[body]atrim=start_sample={start+fade}:end_sample={end-fade},asetpts=PTS-STARTPTS[b];"+
                $"[tail]atrim=start_sample={end-fade}:end_sample={end},asetpts=PTS-STARTPTS[t];"+
                $"[head]atrim=start_sample={start}:end_sample={start+fade},asetpts=PTS-STARTPTS[h];"+
                $"[t][h]acrossfade=ns={fade}:c1=qsin:c2=qsin[s];[b][s]concat=n=2:v=0:a=1,alimiter=limit=1:level=false:latency=true[out]";
            await Run(["-i",master,"-filter_complex",filter,"-map","[out]","-ar",Rate.ToString(CultureInfo.InvariantCulture),
                "-c:a","pcm_s24le",wave],null,ct);
            var length=WaveLoopTags.Add(wave,Rate);
            await Encode(wave,Path.Combine(directory,"loop.ogg"),["-c:a","libvorbis","-q:a","6",
                "-metadata","LOOPSTART=0","-metadata","LOOPLENGTH="+length.ToString(CultureInfo.InvariantCulture)],ct);

            loopDetails=new(){["input_start_sample"]=start,["input_end_sample"]=end,["crossfade_samples"]=fade,
                ["curve"]="equal_power_qsin",["sample_rate"]=Rate,["LOOPSTART"]=0,["LOOPLENGTH"]=length};
        }
        var hashes=new JsonObject();
        foreach (var name in new[]{"loop.wav","loop.ogg"})
        {
            await using var file=File.OpenRead(Path.Combine(directory,name));
            hashes[name]=Convert.ToHexStringLower(await SHA256.HashDataAsync(file,ct));
        }
        var version=(await Run(["-version"],null,ct,true)).Split('\n')[0].Trim();
        var recipe=JsonSerializer.SerializeToNode(new {id=job.id, source=track.id, source_sha256=job.source_sha256,
            conditions=new {runtime=new {ffmpeg=version},parameters=new {loop=loopDetails}}, verification=new {files=hashes}})!.AsObject();
        await File.WriteAllTextAsync(Path.Combine(directory,"provenance.json"),recipe.ToJsonString(),ct);
        using var zip=ZipFile.Open(Path.Combine(directory,"download.zip"),ZipArchiveMode.Create);
        foreach (var name in new[]{"loop.wav","loop.ogg","provenance.json"})
            zip.CreateEntryFromFile(Path.Combine(directory,name),name,CompressionLevel.NoCompression);
        return recipe;
    }

    private Task<string> Encode(string input,string output,string[] options,CancellationToken ct) =>
        Run(["-i",input,"-map","0:a:0",.. options,output],null,ct);

    private async Task<string> Run(string[] args,Stream? output,CancellationToken ct,bool version=false)
    {
        var info=WorkerNode.Command([ffmpeg,"-hide_banner","-nostdin","-v","error","-threads","2",..args]);
        info.RedirectStandardError=info.RedirectStandardOutput=true;
        using var process=Process.Start(info)??throw new IOException("FFmpeg could not start.");
        try
        {
            var stderr=process.StandardError.ReadToEndAsync(ct);
            var stdout=version?process.StandardOutput.ReadToEndAsync(ct):null;
            var copy=stdout==null?process.StandardOutput.BaseStream.CopyToAsync(output??Stream.Null,ct):Task.CompletedTask;
            await Task.WhenAll(copy,process.WaitForExitAsync(ct));
            var text=await stderr;
            if (process.ExitCode!=0) throw new InvalidDataException("FFmpeg が音声を書き出せませんでした。");
            return stdout==null?text:await stdout;
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree:true);await process.WaitForExitAsync(); }
        }
    }

    private static string Number(double value)=>value.ToString("0.########",CultureInfo.InvariantCulture);
    private static long Frames(double value)=>(long)Math.Round(value*Rate);
}
