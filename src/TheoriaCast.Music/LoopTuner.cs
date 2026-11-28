namespace TheoriaCast.Music;

public sealed record SeamWindow(int first,float[][] channels);
public sealed record SeamInput(int frames,int start,int end,SeamWindow head,SeamWindow tail);
public sealed record TunedLoop(double start,double end,int crossfade_ms,double duration,double period_delta_ms);

public static class LoopTuner
{
    public const int Rate=6000;
    public const int Radius=960;

    public static TunedLoop Tune(SeamInput input)
    {
        if(input.start<0 || input.start>=input.end || input.end>input.frames || input.frames>Rate*86400 ||
            !Valid(input.head,input.frames) || !Valid(input.tail,input.frames) || input.head.channels.Length!=input.tail.channels.Length)
            throw new ArgumentException("ループの範囲と波形を確認してください。");
        int size=input.end-input.start,aBest=0,bBest=0,fBest=0;
        double best=double.PositiveInfinity;
        void Score(int a,int b,int fade)
        {
            int h=a-input.head.first,t=b-fade-input.tail.first;
            if(h<0||t<0||h+fade>input.head.channels[0].Length||t+fade>input.tail.channels[0].Length||b-a<=2*fade)return;
            double d=0,power=0,slope=0,slopePower=0;
            int stride=Math.Max(1,fade/64);
            for(int ch=0;ch<input.head.channels.Length;ch++){
                var x=input.head.channels[ch];var y=input.tail.channels[ch];
                for(int k=1;k<fade;k+=stride){
                    double v=x[h+k],w=y[t+k],dx=v-x[h+k-1],dy=w-y[t+k-1];
                    d+=(v-w)*(v-w);power+=v*v+w*w;
                    slope+=(dx-dy)*(dx-dy);slopePower+=dx*dx+dy*dy;
                }
            }
            var lengthError=Math.Abs((b-a-fade)-size);
            var cost=d/(power+1e-9)+.15*slope/(slopePower+1e-9)+.6*lengthError/Rate/.1+
                .025*(Math.Abs(a-input.start)+Math.Abs(b-input.end))/(Rate*.1)+.005*fade/(Rate*.1);
            if(cost<best){best=cost;aBest=a;bBest=b;fBest=fade;}
        }
        foreach(int ms in new[]{10,15,20,30,40,60,80,100}){
            int fade=ms*Rate/1000;
            foreach(int shift in new[]{0,-fade/2,-fade})
                for(int dx=-72;dx<=72;dx+=12){
                    int a=input.start+shift+dx;
                    for(int dy=-72;dy<=72;dy+=12){Score(a,input.end+dy,fade);Score(a,a+size+fade+dy,fade);}
                }
        }
        if(fBest==0)throw new ArgumentException("自動調整には20 msより長い選択範囲が必要です。");
        int left=aBest,right=bBest,overlap=fBest;
        for(int dx=-10;dx<=10;dx++)for(int dy=-10;dy<=10;dy++)Score(left+dx,right+dy,overlap);
        return new(aBest/(double)Rate,bBest/(double)Rate,fBest*1000/Rate,(bBest-aBest-fBest)/(double)Rate,
            ((bBest-aBest-fBest)-size)*1000d/Rate);
    }
    private static bool Valid(SeamWindow? w,int frames)=>w is {first:>=0,channels.Length:>=1 and <=2} &&
        w.channels.All(c=>c is {Length:>0 and <=1922} && c.Length==w.channels[0].Length && w.first<=frames-c.Length && c.All(float.IsFinite));
}
