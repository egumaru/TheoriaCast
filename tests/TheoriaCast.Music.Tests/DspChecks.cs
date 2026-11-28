using TheoriaCast.Music;
using TheoriaCast.Music.Api;

internal static class DspChecks
{
    internal static void Check()
    {
        const int rate = StaticMixRenderer.Rate;
        static void Require(bool condition,string name)
        {
            if (!condition) throw new InvalidOperationException("DSP: " + name);
        }
        static OfflineProcessor Effect(string type, Dictionary<string,double> parameters) =>
            OfflineProcessor.Create(MixDsp.Pin([new("test",type,parameters)])![1], new ProjectLane("s","take"));
        static double Response(OfflineProcessor effect,double hz)
        {
            double energy=0, reference=0;
            for (int i=0;i<rate;i++)
            {
                double sample = 0.125*Math.Sin(2*Math.PI*hz*i/rate);
                double left=sample,right=sample;
                effect.Process(ref left,ref right);
                if (i >= rate/2) { energy += left*left; reference += sample*sample; }
            }
            return 10*Math.Log10(energy/reference);
        }
        double hpf = Response(Effect("hpf",new(){["frequency_hz"]=200}),20);
        double eq = Response(Effect("eq",new(){["frequency_hz"]=1000,["gain_db"]=6}),1000);
        double gain = Response(Effect("gain_pan",new(){["gain_db"]=-6}),1000);
        Require(Math.Abs(hpf+40)<0.1 && Math.Abs(eq-6)<0.01 && Math.Abs(gain+6)<0.001,"frequency and gain responses");
        var limit = Effect("limiter",new(){["ceiling_db"]=-3});
        double l=2,r=-1; limit.Process(ref l,ref r);
        double peak = 20*Math.Log10(l);
        Require(peak<=-3 && peak>-3.001 && r==-0.5*l,"linked sample ceiling");
        var comp = Effect("compressor",new(){["threshold_db"]=-20,["ratio"]=4,["attack_ms"]=1});
        for (int i=0;i<rate;i++) { l=1;r=0.5;comp.Process(ref l,ref r); }
        Require(Math.Abs(20*Math.Log10(l)+15)<0.01 && l==2*r,"compressor steady state");
        var room = Effect("reverb",new(){["wet"]=1,["dry"]=0,["predelay_ms"]=50});
        int first = -1;
        for (int i=0;i<room.TailFrames;i++)
        {
            l=i==0?1:0; r=l; room.Process(ref l,ref r);
            if (l!=0 && first<0) first=i;
        }
        Require(first==2400+1423,"wet impulse delay");
        var meter = new MixMeter();
        for (int i=0;i<rate;i++) meter.Observe(0,0);
        Require(meter.IntegratedLufs()==null && meter.PeakDbfs.overall==null,"silence");
        for (int i=0;i<rate*3;i++)
        {
            int value=(int)Math.Round(Math.Sin(2*Math.PI*1000*i/rate)*0.1*8388608);
            meter.Observe(value,value);
        }
        var tone = new MixMeter();
        for (int i=0;i<rate*3;i++)
        {
            int v=(int)Math.Round(Math.Sin(2*Math.PI*1000*i/rate)*0.1*8388608);
            tone.Observe(v,v);
        }
        Require(Math.Abs(tone.IntegratedLufs()!.Value+20)<0.1 && Math.Abs(tone.PeakDbfs.left!.Value+20)<0.001,
            "stereo 1 kHz sine absolute level");
        Require(meter.IntegratedLufs() < tone.IntegratedLufs(),"partial-energy windows across silence");
        Console.WriteLine($"PASS DSP: 20 Hz HPF {hpf:F5} dB, EQ {eq:F5} dB, gain {gain:F5} dB, ceiling {peak:F6} dBFS, stereo tone {tone.IntegratedLufs():F4} LUFS, first wet impulse {first} frames");
    }
}
