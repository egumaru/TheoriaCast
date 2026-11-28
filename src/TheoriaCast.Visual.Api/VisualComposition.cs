using System.Globalization;
using OpenCvSharp;

namespace TheoriaCast.Visual.Api;

internal static partial class VisualImages
{
    public static Rect SafeArea(int width,int height,MarginOffsets margin)
    {
        if (new[]{margin.Left,margin.Top,margin.Right,margin.Bottom}.Any(x=>!double.IsFinite(x)||x is <0 or >=1))
            throw new ArgumentException("余白は0以上1未満の比率で指定してください。");
        int left=(int)Math.Ceiling(width*margin.Left), right=(int)Math.Ceiling(width*margin.Right);
        int top=(int)Math.Ceiling(height*margin.Top), bottom=(int)Math.Ceiling(height*margin.Bottom);
        if (left+right>=width || top+bottom>=height)
            throw new ArgumentException("余白を除いた領域がありません。");
        return new(left,top,width-left-right,height-top-bottom);
    }

    public static Mat Pad(Mat source,EditSettings settings)
    {
        int width=settings.Width!.Value, height=settings.Height!.Value;
        var area=SafeArea(width,height,settings.MarginOffsets!);
        double scale=Math.Min(area.Width/(double)source.Width,area.Height/(double)source.Height);
        int innerWidth=Math.Clamp((int)Math.Round(source.Width*scale),1,area.Width);
        int innerHeight=Math.Clamp((int)Math.Round(source.Height*scale),1,area.Height);
        var placement=new Rect(area.X+(area.Width-innerWidth)/2,area.Y+(area.Height-innerHeight)/2,innerWidth,innerHeight);
        using var rgba=ToBgra(source);
        using var foreground=new Mat();
        Cv2.Resize(rgba,foreground,new Size(innerWidth,innerHeight),
            interpolation:scale<=1?InterpolationFlags.Area:InterpolationFlags.Linear);
        var fill=settings.PadStyle=="solid"?HexColor(settings.PadColor!):EdgeColor(rgba);
        using var canvas=new Mat(height,width,MatType.CV_8UC4,fill);
        if (settings.PadStyle=="blur")
        {
            // Blur a small cover image so CPU work remains bounded for full-resolution exports.
            double ratio=Math.Min(1,128.0/Math.Max(width,height));
            using var cover=Crop(rgba,Math.Max(1,(int)Math.Round(width*ratio)),Math.Max(1,(int)Math.Round(height*ratio)));
            using var blurred=new Mat();
            Cv2.GaussianBlur(cover,blurred,new Size(0,0),Math.Max(1,Math.Min(cover.Width,cover.Height)/14.0));
            using var enlarged=new Mat();
            Cv2.Resize(blurred,enlarged,new Size(width,height),interpolation:InterpolationFlags.Linear);
            using var background=Composite(enlarged,canvas);
            background.CopyTo(canvas);
        }
        using var region=new Mat(canvas,placement);
        if (source.Channels()==4)
        {
            using var merged=Composite(foreground,region);
            merged.CopyTo(region);
        }
        else foreground.CopyTo(region);
        return canvas.Clone();
    }

    private static Scalar HexColor(string text)
    {
        var hex=int.Parse(text.AsSpan(1),NumberStyles.AllowHexSpecifier,CultureInfo.InvariantCulture);
        return new(hex&255,(hex>>8)&255,hex>>16,255);
    }

    private static Scalar EdgeColor(Mat image)
    {
        double blue=0,green=0,red=0,alpha=0;
        void Add(int row,int column)
        {
            var p=image.At<Vec4b>(row,column);
            blue+=p.Item0*p.Item3; green+=p.Item1*p.Item3; red+=p.Item2*p.Item3; alpha+=p.Item3;
        }
        var width=image.Width;
        var height=image.Height;
        for(int x=0;x<width;x++)
        {
            Add(0,x);
            if(height>1) Add(height-1,x);
        }
        for(int y=1;y<height-1;y++)
        {
            Add(y,0);
            if(width>1) Add(y,width-1);
        }
        return alpha>0?new(Math.Round(blue/alpha),Math.Round(green/alpha),Math.Round(red/alpha),255):HexColor("#f5f2eb");
    }

    public static Mat MatchColor(Mat source,byte[] referenceBytes,double strength)
    {
        using var reference=Decode(referenceBytes);
        if(strength==0) return source.Clone();
        using var src=ToLab(source,out var sourceAlpha);
        using var target=ToLab(reference,out var targetAlpha);
        using (sourceAlpha)
        using (targetAlpha)
        {
            if(!src.GetArray(out Vec3f[] pixels) || !target.GetArray(out Vec3f[] referencePixels) ||
                !sourceAlpha.GetArray(out byte[] alpha) || !targetAlpha.GetArray(out byte[] refAlpha))
                throw new InvalidDataException("色合わせに使う画素を取得できません。");
            var original=LabStatistics.Measure(pixels,alpha);
            var desired=LabStatistics.Measure(referencePixels,refAlpha);
            var l=desired.MapFrom(original,0);
            var a=desired.MapFrom(original,1);
            var b=desired.MapFrom(original,2);
            var mid=Math.Clamp(l(original.Median),.01,99.99);
            var midpoint=Math.Clamp(desired.Median,.01,99.99);
            float Blend(double start,double end,double min,double max) =>
                (float)(start+(Math.Clamp(end,min,max)-start)*strength);
            for(int index=0;index<pixels.Length;index++)
            {
                if(alpha[index]==0) continue;
                var pixel=pixels[index];
                var linear=Math.Clamp(l(pixel.Item0),0,100);
                var tone=linear<mid?linear*midpoint/mid:midpoint+(linear-mid)*(100-midpoint)/(100-mid);
                pixels[index]=new(Blend(pixel.Item0,tone,0,100),
                    Blend(pixel.Item1,a(pixel.Item1),-128,127),Blend(pixel.Item2,b(pixel.Item2),-128,127));
            }
            src.SetArray(pixels);
            using var bgr=new Mat();
            using var bytes=new Mat();
            Cv2.CvtColor(src,bgr,ColorConversionCodes.Lab2BGR);
            bgr.ConvertTo(bytes,MatType.CV_8UC3,255);
            var output=ToBgra(bytes);
            Cv2.InsertChannel(sourceAlpha,output,3);
            return output;
        }
    }

    private static Mat ToLab(Mat image,out Mat alpha)
    {
        using var bgra=ToBgra(image);
        using var bgr=new Mat();
        using var floats=new Mat();
        alpha=new Mat();
        Cv2.ExtractChannel(bgra,alpha,3);
        Cv2.CvtColor(bgra,bgr,ColorConversionCodes.BGRA2BGR);
        bgr.ConvertTo(floats,MatType.CV_32FC3,1.0/255);
        var lab=new Mat();
        Cv2.CvtColor(floats,lab,ColorConversionCodes.BGR2Lab);
        return lab;
    }

    private sealed record LabStatistics(double[] Mean,double[] Deviation,double Median)
    {
        public static LabStatistics Measure(Vec3f[] pixels,byte[] opacity)
        {
            var sums=new double[3];
            var squares=new double[3];
            var luminance=new long[101];
            long count=0;
            void Add(int index,double value,int weight)
            {
                sums[index]+=value*weight;
                squares[index]+=value*value*weight;
            }
            for(int index=0;index<pixels.Length;index++)
            {
                var color=pixels[index];
                int weight=opacity[index];
                count+=weight;
                Add(0,color.Item0,weight); Add(1,color.Item1,weight); Add(2,color.Item2,weight);
                luminance[Math.Clamp((int)Math.Round(color.Item0),0,100)]+=weight;
            }
            if(count==0) throw new InvalidDataException("透明でない画素が色合わせに必要です。");
            long cumulative=0;
            int median=0;
            while((cumulative+=luminance[median])<(count+1)/2) median++;
            var means=sums.Select(x=>x/count).ToArray();
            var deviations=squares.Select((x,i)=>Math.Sqrt(Math.Max(0,x/count-means[i]*means[i]))).ToArray();
            return new(means,deviations,median);
        }

        public Func<double,double> MapFrom(LabStatistics original,int channel)
        {
            var k=Math.Clamp(Deviation[channel]/Math.Max(.01,original.Deviation[channel]),0,8);
            return sample=>Mean[channel]+k*(sample-original.Mean[channel]);
        }
    }
}
