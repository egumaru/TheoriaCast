using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIKernel.Dtos.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using TheoriaCast.Music;
using TheoriaCast.Music.Api;
using TheoriaCast.Music.Mcp;

static void Require(bool ok,string message) { if (!ok) throw new Exception(message); }
static void Invalid(Action action)
{
    try { action(); }
    catch (ArgumentException) { return; }
    throw new Exception("Expected invalid input.");
}
static byte[] Audio()
{
    using var buffer=new MemoryStream();
    using var w=new BinaryWriter(buffer);
    w.Write("RIFF"u8); w.Write(44); w.Write("WAVEfmt "u8); w.Write(16);
    w.Write((short)1);w.Write((short)1);w.Write(4);w.Write(8);w.Write((short)2);w.Write((short)16);
    w.Write("data"u8);w.Write(8);foreach (short v in new short[]{0,16384,-32768,8192})w.Write(v);
    return buffer.ToArray();
}

var root=Directory.CreateTempSubdirectory("theoria-store-test-");
try
{
    var types=new[]{"generate_bgm","edit","mix"};
    var node=new NodeReport("A",true,10000,"idle",types);
    var store=new MusicStore(root.FullName,new Dictionary<string,string[]>{{"A",types}});
    var job=new JobSpec("generate_bgm","Test","",seconds:5);
    Invalid(()=>store.Submit(new("generate_vocal","Test","song")));
    var id=store.Submit(job).id;
    Require(store.Poll(node with {free_mib=1000},true)==null,"VRAM gate");
    for (int n=0;n<3;n++)
    {
        var claim=store.Poll(node,true)!;
        Require(claim.id==id && store.Poll(node,true)==null,"exclusive claim");
        if(n==0)
        {
            store.Progress(id,new(claim.lease,"queued"));
            claim=store.Poll(node,true)!;
        }
        using var db=new SqliteConnection("Data Source="+Path.Combine(root.FullName,"music.sqlite"));
        db.Open();using var cmd=db.CreateCommand();
        cmd.CommandText="UPDATE jobs SET expires=0 WHERE id=$id";cmd.Parameters.AddWithValue("$id",id);cmd.ExecuteNonQuery();
        Require(store.GetJob(id).state==(n==2?"failed":"queued"),"bounded lease retry");
    }
    var cancel=store.Submit(job); var obsolete=store.Poll(node,true)!;
    Require(store.Cancel(cancel.id).state=="cancelled","cancel");
    try { store.CheckOwner(cancel.id,obsolete.lease); throw new Exception("stale claim"); } catch(InvalidOperationException) {}

    var bad=store.Submit(job);var badClaim=store.Poll(node,true)!;
    using var archive=new MemoryStream();
    using(var zip=new ZipArchive(archive,ZipArchiveMode.Create,true))
        for(var n=0;n<2;n++) { using var entry=zip.CreateEntry("master.wav").Open();entry.WriteByte(1); }
    archive.Position=0;
    var endpoints=new MusicEndpoints(store,NullLogger<MusicEndpoints>.Instance);
    var response=await endpoints.InvokeAsync("finish",new ApiInvocation(archive,new Dictionary<string,string>{{"id",bad.id}},
        new Dictionary<string,string>{{"lease",badClaim.lease}},null,new RequestIdentity("worker",new HashSet<string>{"worker"})),default);
    Require(response.StatusCode==400 && store.GetJob(bad.id).state=="failed","malformed delivery terminal result");

    var saved=store.Submit(job);var lease=store.Poll(node,true)!;
    string stage=Directory.CreateDirectory(Path.Combine(root.FullName,"take")).FullName;
    var wav=Path.Combine(stage,"master.wav");File.WriteAllBytes(wav,Audio());
    var wave=await Waveform.CachedAsync(wav,0,null,default);
    Require(wave.Duration==1 && wave.Peaks.SequenceEqual(new[]{0f,.5f,1f,.25f}),"PCM samples");
    Require(ReferenceEquals(wave,await Waveform.CachedAsync(wav,0,null,default)),"cached waveform");
    Require((await Waveform.CachedAsync(wav,.5,1,default)).Peaks.SequenceEqual(new[]{1f,.25f}),"waveform interval");
    var origin=new JsonObject{["actual_seconds"]=1,["generation"]=JsonSerializer.SerializeToNode(job)};
    File.WriteAllText(wav+".json",origin.ToJsonString());store.Complete(saved.id,lease.lease,stage,origin);
    var track=store.GetTrack(saved.id);
    Require(!JsonSerializer.Serialize(track).Contains("file_path"),"private track path");
    Invalid(()=>store.Submit(new("edit",source:track.id,end:2)));
    Invalid(()=>store.Submit(new("edit",source:track.id,end:1,fade_out:2)));
    Invalid(()=>store.Submit(new("mix",clips:[new(track.id),new(track.id)],crossfade:1)));
    var mcp=new MusicTools(store);
    mcp.SetReview(track.id,new("adopted"));
    Require((string?)store.GetTrack(track.id).provenance["review"]?["reviewer"]=="codex","MCP attribution");
    store.SetReview(track.id,new("rejected"));
    Require((string?)store.GetTrack(track.id).provenance["review"]?["reviewer"]=="human","GUI attribution");

    var doc=new CompositionDocument("TheoriaCast.Music",1,job with {prompt_md="A\r\nB",seed=0},
        new FrozenTake("test","1",new(),new(),new string('a',64)));
    using var packed=CompositionFiles.Pack(doc);
    var imported=JsonSerializer.SerializeToNode(await CompositionFiles.Import(packed,default))!;
    Require((string?)imported["draft"]?["job"]?["prompt_md"]=="A\r\nB" && imported["final"]?["take"]!=null,"ZIP round trip");
    Invalid(()=>(doc with {version=2}).Check());
    Console.WriteLine("PASS: scheduling, ownership, cancellation, terminal delivery error, review attribution, edit bounds, PCM waveform, cache and frozen recipe");
}
finally {SqliteConnection.ClearAllPools();root.Delete(true);}
