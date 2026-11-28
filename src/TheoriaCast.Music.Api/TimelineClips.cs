using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TheoriaCast.Music.Api;

public sealed partial class MusicStore
{
    private const string ClipSchema="theoriacast.timeline-mix/v2";
    private const long MaxMixFrames=(uint.MaxValue-36L)/6;
    private static IEnumerable<ProjectClipSource> Sources(MixManifest project)=>project.tracks.SelectMany(lane=>
        lane.clips==null ? new[]{new ProjectClipSource(lane.take,sha256:lane.sha256)} : lane.clips.Select(c=>c.source));
    private static List<ProjectClip> EditableClips(ProjectLane lane)=>lane.clips?.ToList()??
        [new ProjectClip("original",new(lane.take,sha256:lane.sha256))];

    private MixManifest EditClip(MixManifest manifest,MixProjectCommand command)
    {
        var edit=command.clip??throw new ArgumentException("Supply clip {lane,id}.");
        if(string.IsNullOrWhiteSpace(edit.lane)||string.IsNullOrWhiteSpace(edit.id))throw new ArgumentException("Missing lane or clip ID.");
        var lanes=manifest.tracks.ToList();
        int laneIndex=lanes.FindIndex(l=>l.id==edit.lane);
        if(laneIndex<0) {
            if(command.action!="place_clip" || edit.source==null)throw new KeyNotFoundException("Destination lane not found.");
            laneIndex=lanes.Count;lanes.Add(new(edit.lane,edit.source.take,clips:[]));
        }
        var lane=lanes[laneIndex];
        // A v1 lane implicitly plays its entire take. Preserve it when the lane gains clips.
        var clips=EditableClips(lane);
        int index=clips.FindIndex(c=>c.id==edit.id);
        if(command.action=="place_clip") {
            if(index>=0 || edit.source==null)throw new ArgumentException("Supply a source and a new clip ID in this lane.");
            clips.Add(new(edit.id,edit.source,edit.start_sample??0,edit.in_sample??0,edit.out_sample,
                edit.fade_in_samples??0,edit.fade_out_samples??0,edit.repeat_count??1,edit.length_samples));
        } else {
            if(index<0)throw new KeyNotFoundException("Clip not found in this lane.");
            var c=clips[index];
            switch(command.action) {
                case "move_clip" when edit.to_lane!=null && edit.to_lane!=lane.id:
                    if(string.IsNullOrWhiteSpace(edit.to_lane))throw new ArgumentException("Missing destination lane ID.");
                    int destIndex=lanes.FindIndex(l=>l.id==edit.to_lane);
                    if(destIndex<0){destIndex=lanes.Count;lanes.Add(new(edit.to_lane,c.source.take,clips:[]));}
                    var dest=lanes[destIndex];
                    var other=EditableClips(dest);
                    if(other.Any(item=>item.id==c.id))throw new ArgumentException("Clip ID already exists on the destination lane.");
                    other.Add(c with {start_sample=edit.start_sample??(lane.start_sample+c.start_sample-dest.start_sample)});
                    lanes[destIndex]=dest with {clips=other.ToArray()};
                    clips.RemoveAt(index);break;
                case "move_clip" when edit.start_sample.HasValue: clips[index]=c with {start_sample=edit.start_sample.Value};break;
                case "trim_clip" when edit.in_sample.HasValue || edit.out_sample.HasValue:
                    clips[index]=c with {in_sample=edit.in_sample??c.in_sample,out_sample=edit.out_sample??c.out_sample};break;
                case "fade_clip" when edit.fade_in_samples.HasValue || edit.fade_out_samples.HasValue:
                    clips[index]=c with {fade_in_samples=edit.fade_in_samples??c.fade_in_samples,fade_out_samples=edit.fade_out_samples??c.fade_out_samples};break;
                case "loop_clip" when edit.repeat_count.HasValue || edit.length_samples.HasValue:
                    if(edit.repeat_count.HasValue && edit.length_samples.HasValue)throw new ArgumentException("Choose a repetition count or an output length.");
                    if(edit.loop_job!=null)c=c with {source=new(c.source.take,loop_job:edit.loop_job),in_sample=0,out_sample=null};
                    clips[index]=c with {repeat_count=edit.repeat_count??1,length_samples=edit.length_samples};break;
                case "delete_clip":clips.RemoveAt(index);break;
                default:throw new ArgumentException("Supply the fields for the requested clip action.");
            }
        }
        lanes[laneIndex]=lane with {clips=clips.ToArray()};
        return Pin(manifest with {schema=manifest.schema==DspSchema?DspSchema:ClipSchema,tracks=lanes.ToArray()});
    }

    internal sealed record NativeRange(long start_frame,long end_frame,int fade_frames);
    private sealed record ShortEdit(string source_sha256,NativeRange range);
    private sealed record ShortSource(string schema,Guid server,string track,int sample_rate,ShortEdit edit);
    internal static NativeRange? RangeOf(ProjectClipSource source)=>source.short_recipe?["edit"]?["range"]?.Deserialize<NativeRange>();

    private (long Frames,string Sha) LoopMaterial(ProjectClipSource source)
    {
        var job=GetExport(source.loop_job!);
        if(job.state!="done" || job.request.kind!="loop" || job.request.track!=source.take || job.source_sha256!=source.sha256)
            throw new ArgumentException("Choose a completed loop export of this exact take.");
        var loop=job.recipe?["conditions"]?["parameters"]?["loop"];
        string? sha=(string?)job.recipe?["verification"]?["files"]?["loop.wav"];
        if(loop==null || (int?)loop["sample_rate"]!=StaticMixRenderer.Rate || sha==null || !IsHash(sha) ||
           source.loop_sha256!=null && source.loop_sha256!=sha)throw new InvalidDataException("Loop export identity mismatch.");
        return ((long)loop["LOOPLENGTH"]!,sha);
    }

    private ProjectClip[]? PinClips(ProjectLane lane)
    {
        if(lane.clips==null)return null;
        var ids=new HashSet<string>();
        var pinned=new List<ProjectClip>();
        foreach(var clip in lane.clips) {
            if(clip?.source==null || string.IsNullOrWhiteSpace(clip.id) || !ids.Add(clip.id))
                throw new ArgumentException("Clip IDs must be unique within a lane.");
            var media=clip.source;
            string sha=(string?)GetTrack(media.take).provenance["sha256"]??throw new InvalidDataException("Missing take hash.");
            if(!IsHash(sha) || media.sha256!=null && media.sha256!=sha)throw new InvalidDataException("Clip source hash mismatch.");
            media=media with {sha256=sha};
            long frames;
            if(media.loop_job!=null) {
                if(media.short_recipe!=null)throw new ArgumentException("Choose one clip source.");
                var loop=LoopMaterial(media);frames=loop.Frames;media=media with {loop_sha256=loop.Sha};
            } else {
                if(media.loop_sha256!=null)throw new ArgumentException("A loop hash requires its export job.");
                using var stream=OpenTrackAudio(media.take,CancellationToken.None);
                var info=StaticMixRenderer.Inspect(stream);
                long native=info.frames;
                if(media.short_recipe!=null) {
                    var shortClip=media.short_recipe.Deserialize<ShortSource>();
                    if(shortClip?.edit?.range is not {} range || shortClip.schema!="theoria.short-clip.v1" ||
                       !Guid.TryParse(LocalId,out var server) || shortClip.server!=server ||
                       !Guid.TryParse(shortClip.track,out var track) || track.ToString("N")!=media.take ||
                       shortClip.sample_rate!=info.rate || !string.Equals(shortClip.edit.source_sha256,sha,StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException("Short clip recipe does not match this shelf, take or sample rate.");
                    if(range.start_frame<0 || range.end_frame>native || range.end_frame<=range.start_frame ||
                       range.fade_frames<0 || range.fade_frames>info.rate || 2L*range.fade_frames>=range.end_frame-range.start_frame)
                        throw new ArgumentException("Invalid native short clip selection.");
                    native=range.end_frame-range.start_frame-range.fade_frames;
                }
                frames=(native*StaticMixRenderer.Rate+info.rate-1)/info.rate;
            }
            long end=clip.out_sample??frames;
            if(clip.start_sample<0 || clip.in_sample<0 || end<=clip.in_sample || end>frames || clip.repeat_count<1 ||
               clip.length_samples is <=0 or >MaxMixFrames || clip.length_samples!=null && clip.repeat_count!=1)
                throw new ArgumentException("Check clip bounds and repetition.");
            long period=end-clip.in_sample;
            if(clip.length_samples==null && clip.repeat_count>MaxMixFrames/period)throw new ArgumentException("Clip exceeds RIFF size limit.");
            long length=clip.length_samples??period*clip.repeat_count;
            if(length>period && (media.short_recipe==null && media.loop_job==null || clip.in_sample!=0 || end!=frames))
                throw new ArgumentException("Repeating requires the full short clip or tuned loop period. Supply a loop export for the new edges.");
            if(lane.start_sample>MaxMixFrames-length || clip.start_sample>MaxMixFrames-length-lane.start_sample ||
               clip.fade_in_samples<0 || clip.fade_out_samples<0 || clip.fade_in_samples>length || clip.fade_out_samples>length)
                throw new ArgumentException("Clip duration, position or fades exceed the output range.");
            pinned.Add(clip with {source=media,out_sample=end});
        }
        return pinned.ToArray();
    }
    private Stream OpenClipMedia(ProjectClipSource media,CancellationToken ct)
    {
        if(media.loop_job==null)return OpenTrackAudio(media.take,ct);
        LoopMaterial(media);
        var result=ScratchStream();
        try {
            using var zip=new ZipArchive(ExportFile(media.loop_job),ZipArchiveMode.Read);
            using var wav=(zip.GetEntry("loop.wav")??throw new InvalidDataException("Missing verified loop.wav.")).Open();
            var block=new byte[65536];int n;
            while((n=wav.Read(block))>0){ct.ThrowIfCancellationRequested();result.Write(block,0,n);}
            result.Position=0;return result;
        }catch{result.Dispose();throw;}
    }
}
