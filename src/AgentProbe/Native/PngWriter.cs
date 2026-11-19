using System.Buffers.Binary;
using System.IO.Compression;

namespace AgentProbe.Native;

/// <summary>Minimal PNG encoder (8-bit RGB) so screenshots need no imaging library.</summary>
internal static class PngWriter
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>Encodes top-down 32-bit BGRA pixels as an RGB PNG.</summary>
    public static void Write(Stream output, ReadOnlySpan<byte> bgra, int width, int height)
    {
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
        header[8] = 8; // bit depth
        header[9] = 2; // truecolor
        Chunk(output, "IHDR", header);

        using var data = new MemoryStream();
        using (var z = new ZLibStream(data, CompressionLevel.Optimal, leaveOpen: true))
        {
            var row = new byte[1 + (width * 3)];
            for (var y = 0; y < height; y++)
            {
                var src = bgra.Slice(y * width * 4, width * 4);
                for (int x = 0, o = 1; x < width; x++, o += 3)
                {
                    row[o] = src[(x * 4) + 2];
                    row[o + 1] = src[(x * 4) + 1];
                    row[o + 2] = src[x * 4];
                }

                z.Write(row);
            }
        }

        Chunk(output, "IDAT", data.GetBuffer().AsSpan(0, (int)data.Length));
        Chunk(output, "IEND", []);
    }

    private static void Chunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(word, data.Length);
        output.Write(word);
        Span<byte> tag = [(byte)type[0], (byte)type[1], (byte)type[2], (byte)type[3]];
        output.Write(tag);
        output.Write(data);
        BinaryPrimitives.WriteUInt32BigEndian(word, Crc(data, Crc(tag, 0xFFFFFFFF)) ^ 0xFFFFFFFF);
        output.Write(word);
    }

    private static uint Crc(ReadOnlySpan<byte> data, uint crc)
    {
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[n] = c;
        }

        return table;
    }
}
