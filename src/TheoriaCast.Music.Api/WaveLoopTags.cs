using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace TheoriaCast.Music.Api;

internal static class WaveLoopTags
{
    public static uint Add(string path,int rate)
    {
        using var wave=File.Open(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None);
        using var reader=new BinaryReader(wave,Encoding.ASCII,leaveOpen:true);
        using var writer=new BinaryWriter(wave,Encoding.ASCII,leaveOpen:true);
        if (reader.ReadUInt32()!=0x46464952) throw new InvalidDataException("ループ WAV の形式が無効です。");
        wave.Position=12;
        ushort align=0;
        uint bytes=0;
        while (wave.Position+8<=wave.Length)
        {
            var type=reader.ReadUInt32();
            var length=reader.ReadUInt32();
            long next=wave.Position+length+(length&1);
            if (next>wave.Length) throw new InvalidDataException("ループ WAV の長さが無効です。");
            if (type==0x20746d66 && length>=16) { wave.Position+=12;align=reader.ReadUInt16(); }
            if (type==0x61746164) bytes=length;
            wave.Position=next;
        }
        if (align==0 || bytes<align || bytes%align!=0) throw new InvalidDataException("ループ WAV のフレーム数が無効です。");
        uint count=bytes/align;
        wave.Position=wave.Length;
        writer.Write("smpl"u8);writer.Write(60u);
        foreach (var field in new uint[]{0,0,(uint)(1_000_000_000/rate),60,0,0,0,1,0,0,0,0,count-1,0,0})
            writer.Write(field);
        var tags=Id3(count);
        writer.Write("id3 "u8);writer.Write((uint)tags.Length);writer.Write(tags);
        if ((tags.Length&1)==1) writer.Write((byte)0);
        var riff=checked((uint)(wave.Length-8));
        wave.Position=4;
        writer.Write(riff);
        return count;
    }

    private static byte[] Id3(uint frames)
    {
        using var body=new MemoryStream();
        void Tag(string key,string value)
        {
            var data=Encoding.ASCII.GetBytes("\0"+key+"\0"+value);
            body.Write("TXXX"u8);
            Span<byte> length=stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(length,data.Length);
            body.Write(length);body.Write([0,0]);body.Write(data);
        }
        Tag("LOOPSTART","0");
        Tag("LOOPLENGTH",frames.ToString(CultureInfo.InvariantCulture));
        using var id3=new MemoryStream();
        int count=(int)body.Length;
        id3.Write("ID3"u8);
        id3.Write([3,0,0,(byte)((count>>21)&127),(byte)((count>>14)&127),(byte)((count>>7)&127),(byte)(count&127)]);
        body.WriteTo(id3);
        return id3.ToArray();
    }
}
