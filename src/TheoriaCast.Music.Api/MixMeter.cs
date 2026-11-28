namespace TheoriaCast.Music.Api;

// ITU-R BS.1770, stereo at 48 kHz. Meter the samples actually written to the WAV.
internal sealed class MixMeter
{
    private sealed class KWeighting
    {
        private readonly Biquad shelf = new(new(1.53512485958697,-2.69169618940638,1.19839281085285,
            -1.69065929318241,0.73248077421585));
        private readonly Biquad highpass = new(new(1,-2,1,-1.99004745483398,0.99007225036621));
        internal double Step(double sample) => highpass.Step(shelf.Step(sample));
    }
    private readonly KWeighting left = new(), right = new();
    private double peakLeft, peakRight, energy;
    private int inHop, hopIndex;
    private long hops;
    private const int Hop = StaticMixRenderer.Rate/10;
    private readonly double[] lastFour = new double[4];
    private readonly List<double> blocks = [];
    internal void Observe(int l, int r)
    {
        double a = l/8388608.0, b = r/8388608.0;
        peakLeft = Math.Max(peakLeft,Math.Abs(a)); peakRight = Math.Max(peakRight,Math.Abs(b));
        a = left.Step(a); b = right.Step(b);
        energy += a*a + b*b;
        if (++inHop < Hop) return;
        inHop = 0;
        lastFour[hopIndex] = energy;
        hopIndex = (hopIndex+1)%4;
        energy = 0;
        if (++hops < 4) return;
        double mean = lastFour.Sum()/(4*Hop);
        if (mean > Math.Pow(10,(-70+0.691)/10)) blocks.Add(mean);
    }
    internal sealed record Peaks(double? left, double? right, double? overall);
    private static double? Db(double peak) => peak == 0 ? null : 20*Math.Log10(peak);
    internal Peaks PeakDbfs => new(Db(peakLeft),Db(peakRight),Db(Math.Max(peakLeft,peakRight)));
    internal double? IntegratedLufs()
    {
        if (blocks.Count == 0) return null;
        double relative = blocks.Average()/10;
        return -0.691+10*Math.Log10(blocks.Where(b=>b>relative).Average());
    }
}
