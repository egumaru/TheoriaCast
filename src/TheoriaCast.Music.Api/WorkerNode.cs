using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TheoriaCast.Music.Api;

internal sealed record WorkerNode(string node, string api, string gpu, string scratch, string @lock,
    string[] pause_files, string[] types, string[]? render_command = null, string? python = null,
    string? renderer = null, string? vocal_profile = null, string[]? busy_command = null,
    bool requires_legacy_interlock = false, int min_free_mib = 8192, int max_used_mib = 4000,
    int poll_seconds = 5, string? token_file = null)
{
    public static WorkerNode Load(string path)
    {
        var c=PrivateJson.Read<WorkerNode>(path);
        if (c.node is not ("A" or "B") || string.IsNullOrWhiteSpace(c.gpu) || !Path.IsPathFullyQualified(c.scratch) ||
            !Path.IsPathFullyQualified(c.@lock) || c.pause_files is not {Length:>0} || c.types is not {Length:>0} ||
            c.poll_seconds is <1 or >300)
            throw new InvalidDataException("ノードの GPU、作業場所、ロック、担当する予約を確認してください。");
        return c;
    }

    public string[] Renderer(string config)
    {
        if (render_command is {Length:>0}) return render_command;
        var script=renderer ?? Path.Combine(Path.GetDirectoryName(config)!,"worker","render.py");
        if (!string.IsNullOrWhiteSpace(python) && File.Exists(script)) return [python,script];
        throw new InvalidDataException("準備済みの作曲ツールを render_command に指定してください。");
    }

    public static ProcessStartInfo Command(IReadOnlyList<string> argv)
    {
        var info=new ProcessStartInfo(argv[0]) { UseShellExecute=false,CreateNoWindow=true };
        foreach (var arg in argv.Skip(1)) info.ArgumentList.Add(arg);
        return info;
    }

    private static async Task<(int Code,string Output)> Inspect(string[] args,CancellationToken ct)
    {
        var info=Command(args);
        info.RedirectStandardOutput=true;
        using var process=Process.Start(info) ?? throw new IOException("機器の状況を取得できません。");
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var output=process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return (process.ExitCode,await output);
        }
        finally { if (!process.HasExited) process.Kill(true); }
    }

    internal async Task<string> QueryGpu(string group,string fields,CancellationToken ct)
    {
        var (code,output)=await Inspect(["nvidia-smi","-i",gpu,"--query-"+group+"="+fields,"--format=csv,noheader,nounits"],ct);
        return code==0 ? output.Trim() : throw new IOException("GPU を確認できません。");
    }
    private static bool IsRenderer(int pid,int? rendererPid)
    {
        if (rendererPid is null) return false;
        while (pid>1)
        {
            if (pid==rendererPid) return true;
            try {
                var stat=File.ReadAllText($"/proc/{pid}/stat");
                var fields=stat[(stat.LastIndexOf(')')+2)..].Split(' ');
                if (!int.TryParse(fields[1],out pid)) break;
            } catch (IOException) { break; }
        }
        return false;
    }
    public async Task<string?> Priority(string stop,int? own,CancellationToken ct)
    {
        if (File.Exists(stop) || pause_files.Any(File.Exists)) return "本来の作業を優先しています";
        if (requires_legacy_interlock && busy_command is not {Length:>0}) return "既存ウォッチャーの排他設定を待っています";
        if (busy_command is {Length:>0} && (await Inspect(busy_command,ct)).Code!=0) return "既存の生成または検証が進行中です";
        if (OperatingSystem.IsLinux())
            foreach (var line in (await QueryGpu("compute-apps","pid",ct)).Split('\n'))
                if (int.TryParse(line,out int pid) && !IsRenderer(pid,own)) return "ほかの作業が機器を使用中です";
        return null;
    }
    public async Task<NodeReport> Report(string stop,bool owned,CancellationToken ct)
    {
        var numbers=(await QueryGpu("gpu","memory.free,memory.used,utilization.gpu",ct)).Split(',').Select(int.Parse).ToArray();
        string? reason=await Priority(stop,null,ct);
        if (!owned) reason??="ほかの作曲が進行中です";
        if (numbers[0]<Math.Max(8192,min_free_mib) || numbers[1]>max_used_mib || numbers[2]>=20) reason??="機器の空きを待っています";
        var full=Path.GetFullPath(scratch);
        var drive=DriveInfo.GetDrives().Where(d=>Path.GetRelativePath(d.Name,full) is var r &&
            !Path.IsPathRooted(r) && r!=".." && !r.StartsWith(".."+Path.DirectorySeparatorChar))
            .MaxBy(d=>d.Name.Length);
        if (drive==null || drive.AvailableFreeSpace < (10L<<30)) reason??="保存場所の空きを待っています";
        return new(node,reason==null,numbers[0],reason??"空いています",types);
    }
}

internal static class GpuLease
{
    [DllImport("libc",SetLastError=true)] private static extern int flock(SafeFileHandle fd,int operation);

    public static FileStream? TryAcquire(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        FileStream? stream=null;
        try
        {
            stream=new FileStream(path,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.ReadWrite);
            if (OperatingSystem.IsWindows()) stream.Lock(0,1);
            else if (OperatingSystem.IsLinux()) {
                if (flock(stream.SafeFileHandle,2|4)!=0) { stream.Dispose(); return null; }
            } else throw new PlatformNotSupportedException();
            if (stream.Length==0) { stream.WriteByte(0);stream.Flush(); }
            return stream;
        }
        catch (IOException) { stream?.Dispose(); return null; }
        catch { stream?.Dispose(); throw; }
    }
}
