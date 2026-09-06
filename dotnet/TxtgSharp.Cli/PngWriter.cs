using System.IO.Compression;

namespace TxtgSharp.Cli;

/// <summary>
/// Minimal RGBA8 PNG encoder. Hand-rolled so the verification tool needs no imaging
/// dependency - PNG is just zlib-compressed scanlines wrapped in CRC'd chunks.
/// </summary>
internal static class PngWriter
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static void WriteRgba(string path, ReadOnlySpan<byte> rgba, int width, int height)
    {
        if (rgba.Length < width * height * 4)
            throw new ArgumentException($"Need {width * height * 4} bytes, got {rgba.Length}.", nameof(rgba));

        using FileStream file = File.Create(path);
        file.Write(Signature);

        Span<byte> ihdr = stackalloc byte[13];
        WriteBigEndian(ihdr, width);
        WriteBigEndian(ihdr[4..], height);
        ihdr[8] = 8;    // bit depth
        ihdr[9] = 6;    // colour type: RGBA
        ihdr[10] = 0;   // deflate
        ihdr[11] = 0;   // adaptive filtering
        ihdr[12] = 0;   // no interlace
        WriteChunk(file, "IHDR"u8, ihdr);

        // Each scanline is prefixed with its filter type; 0 means "none".
        byte[] raw = new byte[height * (1 + width * 4)];
        int stride = width * 4;
        for (int y = 0; y < height; y++)
        {
            int dst = y * (1 + stride);
            raw[dst] = 0;
            rgba.Slice(y * stride, stride).CopyTo(raw.AsSpan(dst + 1));
        }

        using MemoryStream deflated = new();
        using (ZLibStream zlib = new(deflated, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        WriteChunk(file, "IDAT"u8, deflated.ToArray());
        WriteChunk(file, "IEND"u8, []);
    }

    private static void WriteChunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        WriteBigEndian(length, data.Length);
        stream.Write(length);
        stream.Write(type);
        stream.Write(data);

        uint crc = Crc32(type, data);
        Span<byte> crcBytes = stackalloc byte[4];
        WriteBigEndian(crcBytes, (int)crc);
        stream.Write(crcBytes);
    }

    private static void WriteBigEndian(Span<byte> target, int value)
    {
        target[0] = (byte)(value >> 24);
        target[1] = (byte)(value >> 16);
        target[2] = (byte)(value >> 8);
        target[3] = (byte)value;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        uint[] table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Crc32(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        uint c = 0xFFFFFFFFu;
        foreach (byte v in a) c = CrcTable[(c ^ v) & 0xFF] ^ (c >> 8);
        foreach (byte v in b) c = CrcTable[(c ^ v) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
