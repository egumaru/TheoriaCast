using Microsoft.Extensions.Caching.Memory;
using System.Buffers.Binary;

namespace TheoriaCast.Music.Api;

internal sealed record Waveform(double Duration, float[] Peaks, double Start, double End)
{
    private static readonly MemoryCache Cache = new(new MemoryCacheOptions { SizeLimit=2_000_000 });
    public static async Task<Waveform> CachedAsync(string path,double start,double? end,CancellationToken ct)
    {
        var info=new FileInfo(path);
        var key=(path,info.Length,info.LastWriteTimeUtc.Ticks,start,end);
        if (Cache.TryGetValue(key,out Waveform? cached) && cached!=null) return cached;
        var wave=await ReadAsync(path,start,end,ct);
        return Cache.Set(key,wave,new MemoryCacheEntryOptions {Size=wave.Peaks.Length+16,SlidingExpiration=TimeSpan.FromMinutes(15)});
    }

    // Keep only enough peaks to draw an overview. Masters can be hundreds of MB.
    public static async Task<Waveform> ReadAsync(string path, double start, double? end, CancellationToken ct)
    {
        using var file = File.OpenRead(path);
        using var reader = new BinaryReader(file);
        if (reader.ReadUInt32() != 0x46464952) throw Unsupported(); // RIFF
        reader.ReadUInt32();
        if (reader.ReadUInt32() != 0x45564157) throw Unsupported(); // WAVE
        int format = 0, channels = 0, rate = 0, align = 0, bits = 0;
        while (file.Position + 8 <= file.Length)
        {
            var type = reader.ReadUInt32();
            var bytes = reader.ReadUInt32();
            long next = file.Position + bytes;
            if (next > file.Length) throw Unsupported();
            if (type == 0x20746d66 && bytes >= 16) // fmt
            {
                format = reader.ReadUInt16();
                channels = reader.ReadUInt16();
                rate = reader.ReadInt32();
                reader.ReadUInt32();
                align = reader.ReadUInt16();
                bits = reader.ReadUInt16();
                if (format == 0xfffe && bytes >= 40)
                {
                    file.Position += 8;
                    format = reader.ReadUInt16(); // WAVEFORMATEXTENSIBLE subformat
                }
            }
            else if (type == 0x61746164) // data
            {
                if (!(format == 1 && bits is 8 or 16 or 24 or 32 || format == 3 && bits is 32 or 64)
                    || channels < 1 || align != channels * (bits / 8) || rate <= 0 || bytes < align)
                    throw Unsupported();
                long frames = bytes / align;
                double duration = frames / (double)rate;
                double finish = end ?? duration;
                if (start < 0 || start >= duration || finish <= start || finish > duration)
                    throw new ArgumentException("曲の中の表示範囲を指定してください。");
                long firstFrame = Math.Min(frames-1,(long)Math.Floor(start * rate));
                long lastFrame = Math.Clamp((long)Math.Ceiling(finish * rate),firstFrame+1,frames);
                long selectedFrames = lastFrame - firstFrame;
                file.Position += firstFrame * align;
                var peaks = new float[Math.Min(selectedFrames, 1800)];
                int batch = Math.Max(1, 65536 / align);
                var buffer = new byte[batch * align];
                for (long first = 0; first < selectedFrames; first += batch)
                {
                    int count = (int)Math.Min(batch, selectedFrames - first);
                    await file.ReadExactlyAsync(buffer.AsMemory(0,count*align),ct);
                    for (int n = 0; n < count; n++)
                    {
                        int bin = (int)((first+n) * peaks.Length / selectedFrames);
                        for (int ch = 0; ch < channels; ch++)
                        {
                            float magnitude = Magnitude(buffer.AsSpan(n*align + ch*(bits/8)), format, bits);
                            if (magnitude > peaks[bin]) peaks[bin] = Math.Min(1, magnitude);
                        }
                    }
                }
                for (int i = 0; i < peaks.Length; i++) peaks[i] = MathF.Round(peaks[i],4);
                return new(duration, peaks, firstFrame / (double)rate, lastFrame / (double)rate);
            }
            file.Position = next + (bytes & 1);
        }
        throw Unsupported();
    }

    private static InvalidDataException Unsupported() => new("この音声の波形を読み取れません。");

    private static float Magnitude(ReadOnlySpan<byte> data, int format, int bits)
    {
        if (format == 3) return (float)Math.Abs(bits == 32 ? BinaryPrimitives.ReadSingleLittleEndian(data)
                                                          : BinaryPrimitives.ReadDoubleLittleEndian(data));
        return Math.Abs(bits switch {
            8 => (data[0]-128) / 128f,
            16 => BinaryPrimitives.ReadInt16LittleEndian(data) / 32768f,
            24 => ((data[0]<<8) | (data[1]<<16) | (data[2]<<24)) / 2147483648f,
            _ => BinaryPrimitives.ReadInt32LittleEndian(data) / 2147483648f
        });
    }
}
