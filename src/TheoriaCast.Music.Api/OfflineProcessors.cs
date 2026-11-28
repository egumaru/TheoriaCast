namespace TheoriaCast.Music.Api;

// Each render owns its filter memories, delay lines and detector envelopes.
internal abstract class OfflineProcessor
{
    protected const int Rate = StaticMixRenderer.Rate;
    internal virtual int TailFrames => 0;
    internal abstract void Process(ref double left, ref double right);
    internal static double Linear(double db) => Math.Pow(10, db / 20);
    internal static (double Left,double Right) Balance(double db, double pan, bool invert = false)
    {
        double gain = Linear(db) * (invert ? -1 : 1);
        return (gain * Math.Min(1,1-pan), gain * Math.Min(1,1+pan));
    }
    internal static OfflineProcessor Create(MixProcessor item, ProjectLane lane)
    {
        var p = item.parameters;
        return item.type switch
        {
            "lane_mixer" => new BalanceProcessor(Balance(lane.gain_db,lane.pan,lane.invert_polarity)),
            "gain_pan" => new BalanceProcessor(Balance(p["gain_db"],p["pan"])),
            "hpf" or "eq" => new StereoFilter(Biquad.Design(item.type,p)),
            "compressor" => new Compressor(p["threshold_db"],p["ratio"],p["attack_ms"],p["release_ms"]),
            "limiter" => new Limiter(p["ceiling_db"]),
            "reverb" => new Reverb(p),
            _ => throw new InvalidDataException("Unknown stored processor: " + item.type)
        };
    }
    private sealed class BalanceProcessor((double Left,double Right) factors) : OfflineProcessor
    {
        internal override void Process(ref double left, ref double right)
        { left *= factors.Left; right *= factors.Right; }
    }
    private sealed class StereoFilter(Biquad.Coefficients c) : OfflineProcessor
    {
        private readonly Biquad l = new(c), r = new(c);
        internal override void Process(ref double left, ref double right)
        { left = l.Step(left); right = r.Step(right); }
    }
    // Stereo-linked peak detector, hard knee, smoothed gain reduction in dB.
    private sealed class Compressor(double threshold, double ratio, double attackMs, double releaseMs) : OfflineProcessor
    {
        private readonly double attack = Math.Exp(-1000 / (Rate * attackMs));
        private readonly double release = Math.Exp(-1000 / (Rate * releaseMs));
        private double reduction;
        internal override void Process(ref double left, ref double right)
        {
            double level = Math.Max(Math.Abs(left),Math.Abs(right));
            double target = level > 0 ? Math.Max(0,20*Math.Log10(level)-threshold)*(1-1/ratio) : 0;
            double coefficient = target > reduction ? attack : release;
            reduction = target + coefficient * (reduction-target);
            double gain = Linear(-reduction);
            left *= gain; right *= gain;
        }
    }
    // Zero-lookahead stereo-linked sample-peak limiter. Round the upper bound inward to PCM24.
    private sealed class Limiter(double ceilingDb) : OfflineProcessor
    {
        private readonly double ceiling = Math.Floor(Linear(ceilingDb)*8388608)/8388608;
        internal override void Process(ref double left, ref double right)
        {
            double peak = Math.Max(Math.Abs(left),Math.Abs(right));
            if (peak <= ceiling) return;
            double gain = ceiling/peak;
            left *= gain; right *= gain;
        }
    }
    private sealed class Comb(int length, double decay)
    {
        private readonly double[] delay = new double[length];
        private readonly double feedback = Math.Pow(0.001,length/(Rate*decay));
        private int position;
        internal double Step(double input)
        {
            double echo = delay[position];
            delay[position] = input + echo*feedback;
            if (++position == delay.Length) position = 0;
            return echo;
        }
    }
    private sealed class Room
    {
        private readonly double[] predelay;
        private readonly Comb[] combs;
        private int position;
        internal Room(int pre, double size, double decay, int spread)
        {
            predelay = new double[pre];
            int[] delays = [1423, 1601, 1867, 2053];
            combs = delays.Select(d => new Comb((int)Math.Round((d+spread)*(0.5+size)),decay)).ToArray();
        }
        internal double Step(double input)
        {
            if (predelay.Length > 0)
            {
                double delayed = predelay[position];
                predelay[position] = input;
                if (++position == predelay.Length) position = 0;
                input = delayed;
            }
            double sum = 0;
            foreach (var comb in combs) sum += comb.Step(input);
            return sum / combs.Length;
        }
    }
    private sealed class Reverb : OfflineProcessor
    {
        private readonly Room l, r;
        private readonly double wet, dry;
        internal override int TailFrames { get; }
        internal Reverb(Dictionary<string,double> p)
        {
            wet = p["wet"]; dry = p["dry"];
            double size = p["room_size"], decay = p["decay_s"];
            int pre = (int)Math.Round(p["predelay_ms"]*Rate/1000);
            l = new(pre,size,decay,0); r = new(pre,size,decay,31);
            // Carry delayed first arrival and 90 dB of decay beyond the last input.
            TailFrames = wet == 0 ? 0 : pre + (int)Math.Ceiling(2084*(0.5+size) + 1.5*decay*Rate);
        }
        internal override void Process(ref double left, ref double right)
        {
            if (wet == 0) { left *= dry; right *= dry; return; }
            double reverbedLeft = l.Step(left), reverbedRight = r.Step(right);
            left = dry*left + wet*reverbedLeft;
            right = dry*right + wet*reverbedRight;
        }
    }
}

internal sealed class Biquad(Biquad.Coefficients c)
{
    internal readonly record struct Coefficients(double B0,double B1,double B2,double A1,double A2);
    private double z1, z2;
    internal double Step(double input)
    {
        double output = c.B0*input + z1;
        z1 = c.B1*input - c.A1*output + z2;
        z2 = c.B2*input - c.A2*output;
        return output;
    }
    internal static Coefficients Design(string type, Dictionary<string,double> p)
    {
        double w = 2*Math.PI*p["frequency_hz"]/StaticMixRenderer.Rate;
        double cos = Math.Cos(w), alpha = Math.Sin(w)/(2*p["q"]);
        if (type == "hpf")
        {
            double a0 = 1+alpha, b0 = (1+cos)/(2*a0);
            return new(b0,-2*b0,b0,-2*cos/a0,(1-alpha)/a0);
        }
        double a = Math.Pow(10,p["gain_db"]/40), den = 1+alpha/a;
        return new((1+alpha*a)/den,-2*cos/den,(1-alpha*a)/den,-2*cos/den,(1-alpha/a)/den);
    }
}
