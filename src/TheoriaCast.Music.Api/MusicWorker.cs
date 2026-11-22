using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace TheoriaCast.Music.Api;

internal sealed class MusicWorker(WorkerNode node,string config,HttpClient api)
{
    private readonly string stop=Path.ChangeExtension(config,".stop");
    private readonly string scratch=Path.Combine(node.scratch,"theoria-"+Convert.ToHexStringLower(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(config)))[..16]);

    public static async Task Run(string[] args)
    {
        if (args.Length is <1 or >2) throw new ArgumentException("worker <node.json> [run|status|pause|resume|stop]");
        var config=Path.GetFullPath(args[0]);
        var node=WorkerNode.Load(config);
        var action=args.Length==2?args[1]:"run";
        Directory.CreateDirectory(node.scratch);
        switch (action)
        {
            case "pause": Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(node.pause_files[0]))!);
                File.WriteAllText(node.pause_files[0],"");return;
            case "resume": File.Delete(node.pause_files[0]);return;
            case "stop": File.WriteAllText(Path.ChangeExtension(config,".stop"),"");return;
            case "status":
                using (var lease=GpuLease.TryAcquire(node.@lock))
                    Console.WriteLine(JsonSerializer.Serialize(await node.Report(Path.ChangeExtension(config,".stop"),lease!=null,default)));
                return;
            case "run": break;
            default: throw new ArgumentException("不明なワーカー操作です。");
        }
        using var cts=new CancellationTokenSource();
        ConsoleCancelEventHandler handler=(_,e)=>{e.Cancel=true;cts.Cancel();};
        Console.CancelKeyPress+=handler;
        using var sig=OperatingSystem.IsLinux()?PosixSignalRegistration.Create(PosixSignal.SIGTERM,ctx=>{ctx.Cancel=true;cts.Cancel();}):null;
        try { await ServeFile(config,cts.Token); }
        finally { Console.CancelKeyPress-=handler; }
    }

    public static async Task ServeFile(string config,CancellationToken ct,string? apiOverride=null,string? workerToken=null)
    {
        config=Path.GetFullPath(config);
        var node=WorkerNode.Load(config);
        var token=workerToken ?? Environment.GetEnvironmentVariable("THEORIA_MUSIC_WORKER_TOKEN");
        if (string.IsNullOrWhiteSpace(token) && node.token_file!=null) token=File.ReadAllText(node.token_file).Trim();
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidDataException("ワーカーの token_file を設定してください。");
        _=node.Renderer(config);
        Directory.CreateDirectory(node.scratch);
        using var single=GpuLease.TryAcquire(Path.ChangeExtension(config,".worker.lock")) ??
            throw new InvalidOperationException("このノードのワーカーはすでに稼働しています。");
        using var api=new HttpClient(new SocketsHttpHandler { AllowAutoRedirect=false,UseCookies=false,ConnectTimeout=TimeSpan.FromSeconds(5) })
            {BaseAddress=MusicConnection.BaseUri(apiOverride??node.api),Timeout=TimeSpan.FromSeconds(55)};
        api.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",token);
        var worker=new MusicWorker(node,config,api);
        var work=Directory.CreateDirectory(worker.scratch);
        foreach (var old in work.EnumerateDirectories("take-*"))
            if (Guid.TryParseExact(old.Name[5..],"N",out _) && !old.Attributes.HasFlag(FileAttributes.ReparsePoint)
                && old.LastWriteTimeUtc<DateTime.UtcNow.AddHours(-24))
                try { old.Delete(true); } catch (IOException) { }
        File.Delete(worker.stop);
        try { await worker.Serve(ct); } catch(OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task Serve(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !File.Exists(stop))
        {
            try
            {
                var demand=await api.GetFromJsonAsync<JsonElement>("worker/demand/"+node.node,ct);
                bool pending=demand.GetProperty("pending").GetBoolean();
                var report=await node.Report(stop,true,ct);
                using (var lease=pending && report.ready ? GpuLease.TryAcquire(node.@lock) : null)
                {
                    var claim=lease!=null;
                    if (pending && report.ready && !claim)
                        report=report with {ready=false,detail="ほかの作曲が進行中です"};
                    using var response=await api.PostAsJsonAsync("worker/poll"+(claim?"?claim=true":""),report,ct);
                    response.EnsureSuccessStatusCode();
                    if (claim && await response.Content.ReadFromJsonAsync<Claim>(ct) is {} job)
                        await Execute(job,report,ct);
                }
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                Console.Error.WriteLine($"Worker {node.node}: {e.Message}");
                try {
                    using var response=await api.PostAsJsonAsync("worker/poll",new NodeReport(node.node,false,0,"機器の確認中",node.types),ct);
                } catch (HttpRequestException) { } catch (TaskCanceledException) when (!ct.IsCancellationRequested) { }
            }
            await Task.Delay(TimeSpan.FromSeconds(node.poll_seconds),ct);
        }
    }
    private async Task Update(Claim job,string state,int progress,string detail,CancellationToken ct)
    {
        using var response=await api.PostAsJsonAsync($"worker/{job.id}/progress",new Update(job.lease,state,progress,detail),ct);
        response.EnsureSuccessStatusCode();
    }

    private async Task Execute(Claim job,NodeReport report,CancellationToken ct)
    {
        if (job.spec.assign_only)
        {
            using var assigned=await api.PostAsJsonAsync($"worker/{job.id}/assign?lease={job.lease}",new {},ct);
            assigned.EnsureSuccessStatusCode();return;
        }
        var folder=Directory.CreateDirectory(Path.Combine(scratch,"take-"+Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var profilePath=node.vocal_profile!=null && job.spec.type is ("generate_vocal" or "regen_reading_fix")?node.vocal_profile:config;
            var profile=profilePath==config?node:WorkerNode.Load(profilePath);
            if (profile.gpu!=node.gpu || profile.node!=node.node) throw new InvalidDataException("歌用の GPU 設定を確認してください。");
            var command=profile.Renderer(profilePath);
            await StageInputs(job.spec,folder,ct);
            // The external tool receives the specification, never the lease that commits the result.
            await File.WriteAllTextAsync(Path.Combine(folder,"job.json"),JsonSerializer.Serialize(new {job.id,job.spec}),ct);
            var info=WorkerNode.Command([..command,profilePath,folder]);
            info.WorkingDirectory=folder;
            info.RedirectStandardError=info.RedirectStandardOutput=true;
            info.Environment["CUDA_VISIBLE_DEVICES"]=node.gpu;
            info.Environment["PYTHONHASHSEED"]="0";
            info.Environment["THEORIA_MUSIC_API"]=api.BaseAddress!.AbsoluteUri.TrimEnd('/');
            info.Environment.Remove("THEORIA_MUSIC_WORKER_TOKEN");
            info.Environment["HF_HUB_OFFLINE"]="1";info.Environment["TOKENIZERS_PARALLELISM"]="false";
            foreach (var key in new[]{"OMP_NUM_THREADS","OPENBLAS_NUM_THREADS","MKL_NUM_THREADS"}) info.Environment[key]="2";
            using var child=Process.Start(info) ?? throw new IOException("作曲ツールを起動できませんでした。");
            var output=Drain(child.StandardOutput);
            var errors=Drain(child.StandardError);
            string? priority=null;
            try
            {
                long ack=Stopwatch.GetTimestamp();
                while (!child.HasExited)
                {
                    ct.ThrowIfCancellationRequested();
                    priority=await node.Priority(stop,child.Id,ct);
                    if (priority!=null) break;
                    var (percent,message)=await Progress(folder,ct);
                    try
                    {
                        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);
                        timeout.CancelAfter(TimeSpan.FromSeconds(9));
                        await Update(job,"running",percent,message,timeout.Token);
                        ack=Stopwatch.GetTimestamp();
                        int free=int.Parse(await node.QueryGpu("gpu","memory.free",ct));
                        using var pulse=await api.PostAsJsonAsync("worker/poll",report with {ready=false,free_mib=free,detail=message},timeout.Token);
                        pulse.EnsureSuccessStatusCode();
                    }
                    catch (Exception e) when ((e is HttpRequestException {StatusCode:null} || e is TaskCanceledException) &&
                        !ct.IsCancellationRequested && Stopwatch.GetElapsedTime(ack)<TimeSpan.FromSeconds(50)) { }
                    await Task.Delay(2000,ct);
                }
            }
            finally
            {
                if (!child.HasExited) { child.Kill(entireProcessTree:true); await child.WaitForExitAsync(); }
                await Task.WhenAll(output,errors);
            }
            if (priority!=null) { await Update(job,"queued",0,priority,ct);return; }
            if (child.ExitCode!=0) throw new InvalidDataException("作曲ツールが終了しました。" + (await errors ?? await output));
            await Update(job,"running",98,"音声を保存しています",ct);
            var archive=Path.Combine(folder,"audio.zip");
            ZipFile.CreateFromDirectory(Path.Combine(folder,"deliver"),archive,CompressionLevel.NoCompression,false);
            await Upload(job,archive,ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            using var shortWait=new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await Update(job,"queued",0,"機器の再開を待っています",shortWait.Token); }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { }
            throw;
        }
        catch (Exception e) when (e is InvalidDataException or IOException or System.ComponentModel.Win32Exception or JsonException)
        { await Update(job,"failed",0,e.Message[..Math.Min(600,e.Message.Length)],ct); }
        finally { Directory.Delete(folder,recursive:true); }
    }

    private static async Task<string?> Drain(StreamReader source)
    {
        string? last=null;
        while (await source.ReadLineAsync() is {} line)
            if (!string.IsNullOrWhiteSpace(line)) last=line[..Math.Min(450,line.Length)];
        return last;
    }

    private async Task StageInputs(JobSpec spec,string folder,CancellationToken ct)
    {
        var parents = spec.type switch {
            "mix" => spec.clips!.Select(clip => clip.track),
            "edit" when spec.replacement is {} donor => new[] {spec.source!, donor},
            "edit" or "regen_reading_fix" => new[] {spec.source!},
            _ => Enumerable.Empty<string>()
        };
        foreach (var id in parents.Distinct())
        {
            if (!Guid.TryParseExact(id,"N",out _)) throw new InvalidDataException("元の曲の番号が無効です。");
            var dir = Directory.CreateDirectory(Path.Combine(folder,"inputs")).FullName;
            var route = "worker/tracks/" + id;
            var metadata = await api.GetStringAsync(route,ct);
            await File.WriteAllTextAsync(Path.Combine(dir,id+".json"),metadata,ct);
            if (spec.type == "regen_reading_fix") continue;
            using var audio = await api.GetStreamAsync(route+"/audio",ct);
            await using var output = File.Create(Path.Combine(dir,id+".wav"));
            await audio.CopyToAsync(output,ct);
        }
    }
    private static async Task<(int,string)> Progress(string folder,CancellationToken ct)
    {
        var path=Path.Combine(folder,"progress.json");
        if (!File.Exists(path)) return (1,"準備中");
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        using var doc=await JsonDocument.ParseAsync(stream,cancellationToken:ct);
        return (doc.RootElement[0].GetInt32(),doc.RootElement[1].GetString()??"");
    }
    private async Task Upload(Claim job,string archive,CancellationToken ct)
    {
        using var input = File.OpenRead(archive);
        if (input.Length > Delivery.MaxArchiveBytes) throw new InvalidDataException("成果物の容量が大きすぎます。");
        var buffer = new byte[(int)Math.Min(Delivery.PartBytes,input.Length)];
        for (long offset=0; offset<input.Length; offset+=buffer.Length)
        {
            int count=(int)Math.Min(buffer.Length,input.Length-offset);
            await input.ReadExactlyAsync(buffer.AsMemory(0,count),ct);
            var route=$"worker/{job.id}/finish?lease={job.lease}";
            if (input.Length>buffer.Length)
            {
                await Update(job,"running",98,"音声を保存しています",ct);
                route += $"&offset={offset}&length={input.Length}";
            }
            if (await SendPart(job,route,buffer,count,ct)) return;
        }
    }

    private async Task<bool> SendPart(Claim job,string route,byte[] buffer,int count,CancellationToken ct)
    {
        for (int attempt=1;;attempt++)
        {
            try
            {
                using var data=new ByteArrayContent(buffer,0,count);
                data.Headers.ContentType=new MediaTypeHeaderValue("application/zip");
                using var response=await api.PostAsync(route,data,ct);
                if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge or
                    HttpStatusCode.UnsupportedMediaType or HttpStatusCode.UnprocessableEntity)
                    throw new InvalidDataException("成果物の音声または出自を確認してください。");
                response.EnsureSuccessStatusCode();return response.StatusCode!=HttpStatusCode.Accepted;
            }
            catch(Exception e) when (attempt<3 && !ct.IsCancellationRequested &&
                (e is HttpRequestException {StatusCode:null} || e is TaskCanceledException))
            {
                var saved=await api.GetFromJsonAsync<Job>("worker/jobs/"+job.id,ct);
                if (saved?.state=="done") return true;
                await Update(job,"running",98,"音声を再送しています",ct);
            }
        }
    }
}
