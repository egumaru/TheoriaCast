using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using TheoriaCast.Music;
using TheoriaCast.Music.Api;

internal static class ProjectMixChecks
{
    internal static async Task Check(string root)
    {
        DspChecks.Check();
        // Exact signed stereo samples cover routing, integer offsets and the single final quantization.
        static byte[] TinyWave() {
            using var output=new MemoryStream(); using var w=new BinaryWriter(output);
            w.Write("RIFF"u8);w.Write(44);w.Write("WAVEfmt "u8);w.Write(16);
            w.Write((short)1);w.Write((short)1);w.Write(48000);w.Write(96000);w.Write((short)2);w.Write((short)16);
            w.Write("data"u8);w.Write(8);foreach(short x in new short[]{4096,-8192,2048,0})w.Write(x);
            return output.ToArray();
        }
        static void Require(bool ok,string message) { if(!ok)throw new Exception("Project mix: "+message); }
        var store=new MusicStore(root,creatorAudio:new CreatorAudio("unused")){MixKeepLatest=1};
        var chunk=await store.StageAudio(new(TinyWave()),default);
        var take=(await store.ImportRecording(new("test tone",[chunk.sha256]),default)).track;
        var manifest=new MixManifest("static mix",[
            new("left",take,pan:-1,solo:true), new("right",take,start_sample:1,pan:1,solo:true,invert_polarity:true),
            new("muted solo",take,gain_db:12,mute:true,solo:true), new("non-solo",take,gain_db:12)]);
        JsonObject Call(MixProjectCommand input)=>JsonSerializer.SerializeToNode(store.MixProjects(input))!.AsObject();
        var create=Call(new("create",manifest:manifest));
        string project=(string)create["project"]!,head=(string)create["head"]!;
        async Task<Track> Render() {
            head=(string)Call(new("render",project,head))["head"]!;
            await store.RunExport(default);
            var job=store.GetExport(head);Require(job.state=="done",job.error??"job");
            return store.GetTrack(head);
        }
        var one=await Render();
        byte[] pcm=File.ReadAllBytes(one.file_path);
        int[] expected=[4096,0,-8192,-4096,2048,8192,0,-2048,0,0];
        Require(pcm.Length==44+3*expected.Length,"length with sample offset");
        for(int i=0;i<expected.Length;i++) {
            int at=44+i*3;
            int sample=(pcm[at]<<8|pcm[at+1]<<16|pcm[at+2]<<24)>>8;
            Require(sample==expected[i]*256,"signed PCM at "+i);
        }
        var two=await Render();Require(!File.Exists(one.file_path),"first revision evicted");
        // Exercise adoption of a previously evicted test take in this disposable database only.
        store.SetReview(one.id,new("adopted"));
        var three=await Render();
        Require(File.Exists(one.file_path) && File.ReadAllBytes(one.file_path).SequenceEqual(pcm),"adopted audio restored and protected");
        Require(!File.Exists(two.file_path) && File.Exists(three.file_path),"only adopted and newest retained");
        store.SetReview(one.id,new("pending"));
        Require(!File.Exists(one.file_path),"retention recomputed after review");
        var reopened=new MusicStore(root,creatorAudio:new CreatorAudio("unused")){MixKeepLatest=1};
        reopened.RecoverExports();
        using var old=reopened.OpenTrackAudio(two.id,default);
        Require(Convert.ToHexStringLower(SHA256.HashData(old))==(string?)two.provenance["sha256"],"rehydrated after restart");
        Require(!File.Exists(two.file_path),"playback did not pin an evicted revision");
        Console.WriteLine("PASS: project PCM routing, adopted retention, regenerated audio after restart");
        var legacy=Call(new("create",manifest:new("clip edits",[new("one",take,start_sample:3)])));
        project=(string)legacy["project"]!;head=(string)legacy["head"]!;
        JsonObject Edit(string action,ClipEditCommand cmd){
            var version=Call(new(action,project,head,clip:cmd));head=(string)version["head"]!;
            return version["revision"]!["manifest"]!.AsObject();
        }
        var placed=Edit("place_clip",new("one","phrase",new(take),start_sample:5,in_sample:1,out_sample:3));
        Require(placed["tracks"]![0]!["clips"]!.AsArray().Count==2,"legacy whole take preserved");
        var moved=Edit("move_clip",new("one","phrase",to_lane:"two"));
        Require((long)moved["tracks"]![1]!["clips"]![0]!["start_sample"]! ==8,"between-lane move retains absolute position");
        string beforeInvalid=head;
        try{Edit("trim_clip",new("two","phrase",out_sample:5));throw new Exception("invalid trim accepted");}
        catch(ArgumentException){}
        try{Edit("loop_clip",new("two","phrase",repeat_count:2));throw new Exception("unverified seam accepted");}
        catch(ArgumentException){}
        Require((string)Call(new("get",project))["head"]! ==beforeInvalid,"failed edits leave no visible revision");
        var result=await Render();
        var bytes=File.ReadAllBytes(result.file_path);
        Require(bytes.Length==44+10*6,"clip timeline length");
        for(int i=0;i<10;i++){
            int at=44+i*6;
            int value=(bytes[at]<<8|bytes[at+1]<<16|bytes[at+2]<<24)>>8;
            int shown=i switch {3=>4096,4=>-8192,5=>2048,8=>-8192,9=>2048,_=>0};
            Require(value==shown*256,"source and project sample coordinates");
        }
        Edit("delete_clip",new("two","phrase"));
        head=(string)Call(new("rollback",project,head,revision:beforeInvalid))["head"]!;
        var repeat=await Render();
        Require((string?)repeat.provenance["sha256"]==(string?)result.provenance["sha256"],"clip rollback PCM identity");
        Console.WriteLine("PASS: clip placement, trim bounds, cross-lane move, v1 input preservation, rollback");
    }
}
