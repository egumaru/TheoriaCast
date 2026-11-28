namespace TheoriaCast.Music;

public static class ShortClipSeam
{
    public static float Blend(float tail,float head,int i,int frames)
    {
        double angle=(frames==1?.5:i/(double)(frames-1))*Math.PI/2;
        return (float)Math.Clamp(tail*Math.Cos(angle)+head*Math.Sin(angle),-1,1);
    }
}
