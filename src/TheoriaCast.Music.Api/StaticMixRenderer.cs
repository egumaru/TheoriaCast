using System.Buffers.Binary;

namespace TheoriaCast.Music.Api;

// Version 1: 48 kHz stereo PCM24, linear stereo balance, one final quantization.
internal static class StaticMixRenderer
{
    internal const int Rate = 48000;
    internal sealed record Analysis(long frames, double peak_before_clip, long clipped_samples,
        MixMeter.Peaks peak_dbfs, double? integrated_lufs);

    internal static (long frames,int rate) Inspect(Stream stream)
    {
        using var source=new Wave(stream,headerOnly:true);
        return (source.NativeFrames,source.NativeRate);
    }

    private sealed record Input(Wave Source,long Start,long End,long Offset,long Period,long FadeIn,long FadeOut,double Left,double Right);
    private sealed record Lane(List<Input> Inputs, OfflineProcessor[] Effects, bool Muted, long End);
    internal static Analysis Render(MixManifest manifest, Func<ProjectClipSource, Stream> open, Stream destination, CancellationToken ct)
    {
        var inputs = new List<Input>();
        var lanes = new List<Lane>();
        try {
            bool solo = manifest.tracks.Any(lane => lane.solo);
            foreach (var lane in manifest.tracks) {
                ct.ThrowIfCancellationRequested();
                bool muted = lane.mute || solo && !lane.solo;
                var clips = new List<Input>();
                // A mixer-only lane keeps the original per-clip accumulation and rounding order.
                OfflineProcessor[] effects = !muted && lane.dsp?.Any(p=>p.type!="lane_mixer")==true
                    ? lane.dsp.Select(p=>OfflineProcessor.Create(p,lane)).ToArray() : [];
                var balance = effects.Length==0 ? OfflineProcessor.Balance(lane.gain_db,lane.pan,lane.invert_polarity) : (1d,1d);
                foreach(var clip in lane.clips??[new("",new(lane.take,sha256:lane.sha256))]) {
                    var stream=open(clip.source);
                    try {
                        var range=MusicStore.RangeOf(clip.source);
                        var source=new Wave(stream,range);
                        long period=(clip.out_sample??source.OutputFrames)-clip.in_sample;
                        if(period<=0 || clip.in_sample<0 || period>source.OutputFrames-clip.in_sample)
                            throw new InvalidDataException("Clip range has changed.");
                        long length=clip.length_samples??checked(period*clip.repeat_count);
                        long start=lane.start_sample+clip.start_sample;
                        source.Periodic=length>period && range!=null;
                        var input = new Input(source,start,start+length,clip.in_sample,period,clip.fade_in_samples,clip.fade_out_samples,
                            balance.Item1,balance.Item2);
                        inputs.Add(input);
                        clips.Add(input);
                    } catch { stream.Dispose();throw; }
                }
                if (clips.Count>0) lanes.Add(new(clips,effects,muted,clips.Max(c=>c.End)+effects.Sum(p=>p.TailFrames)));
            }
            long frames = lanes.Max(lane => lane.End);
            if (frames > (uint.MaxValue - 36L)/6) throw new InvalidDataException("WAV exceeds the RIFF size limit.");
            using var writer = new BinaryWriter(destination, System.Text.Encoding.UTF8, true);
            writer.Write("RIFF"u8); writer.Write((uint)(36+frames*6)); writer.Write("WAVEfmt "u8);
            writer.Write(16); writer.Write((ushort)1); writer.Write((ushort)2); writer.Write(Rate); writer.Write(Rate*6);
            writer.Write((ushort)6); writer.Write((ushort)24); writer.Write("data"u8); writer.Write((uint)(frames*6));
            const int block = 4096;
            var mix = new double[block*2]; var pcm = new byte[block*6];
            var laneBuffer = new double[block*2];
            var meter = new MixMeter();
            double master = Math.Pow(10, manifest.master_gain_db/20), peak = 0;
            long clipped = 0;
            for (long start = 0; start < frames; start += block) {
                ct.ThrowIfCancellationRequested();
                int count = (int)Math.Min(block, frames-start);
                Array.Clear(mix);
                foreach (var lane in lanes) {
                    if (lane.Muted || start >= lane.End) continue;
                    bool processed = lane.Effects.Length != 0;
                    var output = processed ? laneBuffer : mix;
                    if (processed) Array.Clear(laneBuffer);
                    foreach (var input in lane.Inputs) {
                        for (long at = Math.Max(start,input.Start); at < Math.Min(start+count,input.End); at++) {
                            long relative=at-input.Start;
                            var (l,r) = input.Source.Stereo(input.Offset+relative%input.Period);
                            int i = (int)(at-start)*2;
                            double envelope=1;
                            if(relative<input.FadeIn)envelope*=relative/(double)Math.Max(1,input.FadeIn-1);
                            long remaining=input.End-1-at;
                            if(remaining<input.FadeOut)envelope*=remaining/(double)Math.Max(1,input.FadeOut-1);
                            output[i] += l*input.Left*envelope; output[i+1] += r*input.Right*envelope;
                        }
                    }
                    if (processed) {
                        int samples = 2*(int)Math.Min(count,lane.End-start);
                        for (int i=0;i<samples;i+=2) {
                            double l=laneBuffer[i], r=laneBuffer[i+1];
                            foreach (var effect in lane.Effects) effect.Process(ref l,ref r);
                            mix[i]+=l; mix[i+1]+=r;
                        }
                    }
                }
                int leftPcm=0;
                for(int i=0; i<count*2; i++) {
                    double sample = mix[i]*master;
                    if (!double.IsFinite(sample)) throw new InvalidDataException("Source contains non-finite audio samples.");
                    peak = Math.Max(peak, Math.Abs(sample));
                    if(sample < -1 || sample > 1) clipped++;
                    int value=(int)Math.Clamp(Math.Round(Math.Clamp(sample,-1,1)*8388608),-8388608,8388607);
                    pcm[i*3]=(byte)value; pcm[i*3+1]=(byte)(value>>8); pcm[i*3+2]=(byte)(value>>16);
                    if ((i&1)==0) leftPcm=value; else meter.Observe(leftPcm,value);
                }
                writer.Write(pcm,0,count*6);
            }
            return new(frames,peak,clipped,meter.PeakDbfs,meter.IntegratedLufs());
        } finally { foreach(var input in inputs) input.Source.Dispose(); }
    }

    private sealed class Wave : IDisposable
    {
        private readonly Stream file;
        private readonly byte[] buffer;
        private readonly int format, channels, rate, bits, align;
        private readonly long data, frames;
        private long first = -1, last = -1;
        private const int Radius=16, Phases=512;
        private readonly double[][]? filter;
        private readonly MusicStore.NativeRange? selection;
        private readonly float[]? head;
        internal bool Periodic {get;set;}
        internal long NativeFrames => selection==null?frames:selection.end_frame-selection.start_frame-selection.fade_frames;
        internal int NativeRate => rate;
        internal long OutputFrames => (NativeFrames*Rate+rate-1)/rate;

        internal Wave(Stream file,MusicStore.NativeRange? range=null,bool headerOnly=false)
        {
            this.file=file;selection=range;
            using var reader=new BinaryReader(file,System.Text.Encoding.UTF8,true);
            if(reader.ReadUInt32()!=0x46464952) throw Invalid();
            reader.ReadUInt32();
            if(reader.ReadUInt32()!=0x45564157) throw Invalid();
            while(file.Position+8<=file.Length) {
                uint tag=reader.ReadUInt32(), size=reader.ReadUInt32();
                long next=file.Position+size;
                if(next>file.Length) throw Invalid();
                if(tag==0x20746d66 && size>=16) {
                    format=reader.ReadUInt16(); channels=reader.ReadUInt16(); rate=reader.ReadInt32();
                    reader.ReadInt32(); align=reader.ReadUInt16(); bits=reader.ReadUInt16();
                    if(format==0xfffe && size>=40) { file.Position+=8; format=reader.ReadUInt16(); }
                } else if(tag==0x61746164) {
                    if(channels is not (1 or 2) || rate<=0 || align!=channels*(bits/8) ||
                        !(format==1 && bits is 8 or 16 or 24 or 32 || format==3 && bits is 32 or 64) ||
                        size<align || size%align!=0) throw Invalid();
                    data=file.Position; frames=size/align; buffer=new byte[8192*align];
                    if(!headerOnly && rate!=Rate) filter=BuildFilter(Math.Min(1,Rate/(double)rate));
                    if(range!=null){
                        if(range.start_frame<0 || range.end_frame>frames || range.fade_frames<0 ||
                           range.fade_frames>rate || range.end_frame-range.start_frame<=2L*range.fade_frames)
                            throw new InvalidDataException("Invalid frozen short clip selection.");
                        head=new float[checked(range.fade_frames*channels)];
                        for(int i=0;i<head.Length;i++)head[i]=(float)RawSample(range.start_frame+i/channels,i%channels);
                    }
                    return;
                }
                file.Position=next+(size&1);
            }
            throw Invalid();
        }
        private static InvalidDataException Invalid()=>new("Expected a mono or stereo PCM/IEEE-float WAV.");
        private static double[][] BuildFilter(double cutoff)
        {
            var table=new double[Phases][];
            for(int phase=0;phase<Phases;phase++) {
                var taps=table[phase]=new double[Radius*2];
                for(int i=0;i<taps.Length;i++) {
                    double x=i-Radius+1-phase/(double)Phases;
                    double sinc=x==0?cutoff:Math.Sin(Math.PI*x*cutoff)/(Math.PI*x);
                    taps[i]=sinc*(.5+.5*Math.Cos(Math.PI*x/Radius));
                }
                double sum=taps.Sum();
                for(int i=0;i<taps.Length;i++) taps[i]/=sum;
            }
            return table;
        }
        private double RawSample(long at,int ch)
        {
            if(at<0 || at>=frames) return 0;
            if(at<first || at>=last) {
                first=Math.Max(0,at-2*Radius); last=Math.Min(frames,first+buffer.Length/align);
                file.Position=data+first*align;
                file.ReadExactly(buffer.AsSpan(0,(int)(last-first)*align));
            }
            var b=buffer.AsSpan((int)(at-first)*align+ch*(bits/8));
            if(format==3) return bits==32?BinaryPrimitives.ReadSingleLittleEndian(b):BinaryPrimitives.ReadDoubleLittleEndian(b);
            return bits switch {8=>(b[0]-128)/128.0,16=>BinaryPrimitives.ReadInt16LittleEndian(b)/32768.0,
                24=>((b[0]<<8)|(b[1]<<16)|(b[2]<<24))/2147483648.0,_=>BinaryPrimitives.ReadInt32LittleEndian(b)/2147483648.0};
        }
        private double Sample(long at,int ch)
        {
            if(selection is not {} range)return RawSample(at,ch);
            long length=NativeFrames;
            if(at<0 || at>=length){
                if(!Periodic)return 0;
                at=((at%length)+length)%length;
            }
            int fade=range.fade_frames;
            float value=(float)RawSample(range.start_frame+fade+at,ch);
            if(at>=length-fade){
                int i=(int)(at-(length-fade));
                value=ShortClipSeam.Blend(value,head![i*channels+ch],i,fade);
            }
            // Match the native PCM24 samples written by the existing ShortClipAudio WaveFileWriter.
            return (unchecked((int)(2147483648f*value))>>8)/8388608.0;
        }
        internal (double,double) Stereo(long at)
        {
            if(filter==null) { double left=Sample(at,0); return (left,channels==1?left:Sample(at,1)); }
            long scaled=at*rate, frame=scaled/Rate;
            var taps=filter[(int)((scaled%Rate)*Phases/Rate)];
            double l=0,r=0;
            for(int i=0;i<taps.Length;i++) {
                long index=frame+i-Radius+1;
                l+=Sample(index,0)*taps[i];
                if(channels==2) r+=Sample(index,1)*taps[i];
            }
            return (l,channels==1?l:r);
        }
        public void Dispose()=>file.Dispose();
    }
}
