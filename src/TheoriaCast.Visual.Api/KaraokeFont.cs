using System.Buffers.Binary;
using System.Text;

namespace TheoriaCast.Visual.Api;

internal static class KaraokeFont
{
    // A TTC has multiple faces. Name-table offsets in each face are relative to the start of the file.
    public static bool HasFamily(string file, string family)
    {
        using var stream = File.OpenRead(file);
        Span<byte> header = stackalloc byte[12];
        stream.ReadExactly(header);
        if (!header[..4].SequenceEqual("ttcf"u8)) return Face(0);
        var count = BinaryPrimitives.ReadUInt32BigEndian(header[8..]);
        if (count > 256) return false;
        var offsets = new byte[count * 4];
        stream.ReadExactly(offsets);
        for (var i = 0; i < count; i++)
            if (Face(BinaryPrimitives.ReadUInt32BigEndian(offsets.AsSpan(4 * i)))) return true;
        return false;

        bool Face(long start)
        {
            Span<byte> entry = stackalloc byte[16];
            stream.Position = start;
            stream.ReadExactly(entry[..12]);
            var tables = U16(entry[4..]);
            for (var i = 0; i < tables; i++)
            {
                stream.ReadExactly(entry);
                if (!entry[..4].SequenceEqual("name"u8)) continue;
                var offset = BinaryPrimitives.ReadUInt32BigEndian(entry[8..]);
                var length = BinaryPrimitives.ReadUInt32BigEndian(entry[12..]);
                if (length is < 6 or > 4_194_304 || (long)offset + length > stream.Length) return false;
                stream.Position = offset;
                var block = new byte[length];
                stream.ReadExactly(block);
                return Match(block);
            }
            return false;
        }

        bool Match(ReadOnlySpan<byte> block)
        {
            var count = U16(block[2..]);
            var data = U16(block[4..]);
            if (6 + 12 * count > block.Length) return false;
            for (var i = 0; i < count; i++)
            {
                var record = block.Slice(6 + i * 12, 12);
                if (U16(record[6..]) is not (1 or 16)) continue;
                var platform = U16(record);
                var offset = data + U16(record[10..]);
                var length = U16(record[8..]);
                if (offset + length > block.Length || platform is not (0 or 1 or 3)) continue;
                var name = (platform == 1 ? Encoding.ASCII : Encoding.BigEndianUnicode).GetString(block.Slice(offset, length));
                if (string.Equals(name, family, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }

    private static ushort U16(ReadOnlySpan<byte> value) => BinaryPrimitives.ReadUInt16BigEndian(value);
}
