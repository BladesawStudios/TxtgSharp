using System.Buffers.Binary;
using System.IO.Compression;

namespace TxtgSharp.Cli;

/// <summary>
/// Minimal PNG decoder, the counterpart to <see cref="PngWriter"/>. Handles the 8-bit
/// non-interlaced greyscale, RGB, palette and alpha colour types, which is everything the
/// writer emits and everything an image editor produces by default.
/// </summary>
internal static class PngReader
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static (byte[] Rgba, int Width, int Height) ReadRgba(string path)
    {
        byte[] file = File.ReadAllBytes(path);
        if (file.Length < 8 || !file.AsSpan(0, 8).SequenceEqual(Signature))
            throw new InvalidDataException($"{Path.GetFileName(path)} is not a PNG.");

        int width = 0, height = 0, bitDepth = 0, colourType = 0;
        byte[] palette = [];
        byte[] paletteAlpha = [];
        using MemoryStream idat = new();

        int cursor = 8;
        while (cursor + 8 <= file.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(cursor));
            string type = System.Text.Encoding.ASCII.GetString(file, cursor + 4, 4);
            ReadOnlySpan<byte> chunk = file.AsSpan(cursor + 8, length);
            cursor += 12 + length;   // length, type, data, CRC

            switch (type)
            {
                case "IHDR":
                    width = BinaryPrimitives.ReadInt32BigEndian(chunk);
                    height = BinaryPrimitives.ReadInt32BigEndian(chunk[4..]);
                    bitDepth = chunk[8];
                    colourType = chunk[9];
                    if (chunk[12] != 0)
                        throw new NotSupportedException("Interlaced PNGs are not supported.");
                    if (bitDepth != 8)
                        throw new NotSupportedException($"Only 8-bit PNGs are supported, this one is {bitDepth}-bit.");
                    break;

                case "PLTE": palette = chunk.ToArray(); break;
                case "tRNS": paletteAlpha = chunk.ToArray(); break;
                case "IDAT": idat.Write(chunk); break;
                case "IEND": cursor = file.Length; break;
            }
        }

        if (width <= 0 || height <= 0)
            throw new InvalidDataException("PNG has no usable IHDR.");

        int channels = colourType switch
        {
            0 => 1,   // greyscale
            2 => 3,   // RGB
            3 => 1,   // palette index
            4 => 2,   // greyscale + alpha
            6 => 4,   // RGBA
            _ => throw new NotSupportedException($"Unsupported PNG colour type {colourType}.")
        };

        idat.Position = 0;
        using ZLibStream zlib = new(idat, CompressionMode.Decompress);
        using MemoryStream inflated = new();
        zlib.CopyTo(inflated);

        byte[] raw = inflated.GetBuffer();
        int stride = width * channels;
        if (inflated.Length < (long)height * (stride + 1))
            throw new InvalidDataException("PNG pixel data is short.");

        byte[] rows = Unfilter(raw, width, height, channels);
        return (Expand(rows, width, height, colourType, channels, palette, paletteAlpha), width, height);
    }

    /// <summary>
    /// Undoes the per-scanline filters, which predict each byte from its left and upper
    /// neighbours. Filters work on whole bytes at this bit depth, so the left neighbour is
    /// simply <paramref name="channels"/> bytes back.
    /// </summary>
    private static byte[] Unfilter(byte[] raw, int width, int height, int channels)
    {
        int stride = width * channels;
        byte[] rows = new byte[height * stride];

        for (int y = 0; y < height; y++)
        {
            int source = y * (stride + 1);
            byte filter = raw[source];
            int destination = y * stride;
            int above = destination - stride;

            for (int x = 0; x < stride; x++)
            {
                byte value = raw[source + 1 + x];
                byte left = x >= channels ? rows[destination + x - channels] : (byte)0;
                byte up = y > 0 ? rows[above + x] : (byte)0;
                byte upLeft = y > 0 && x >= channels ? rows[above + x - channels] : (byte)0;

                rows[destination + x] = filter switch
                {
                    0 => value,
                    1 => (byte)(value + left),
                    2 => (byte)(value + up),
                    3 => (byte)(value + (left + up) / 2),
                    4 => (byte)(value + Paeth(left, up, upLeft)),
                    _ => throw new InvalidDataException($"Unknown PNG filter type {filter} on row {y}.")
                };
            }
        }

        return rows;
    }

    private static byte Paeth(byte a, byte b, byte c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static byte[] Expand(
        byte[] rows, int width, int height, int colourType, int channels, byte[] palette, byte[] paletteAlpha)
    {
        byte[] rgba = new byte[width * height * 4];

        for (int i = 0, pixels = width * height; i < pixels; i++)
        {
            int source = i * channels;
            int destination = i * 4;

            switch (colourType)
            {
                case 0:
                case 4:
                    rgba[destination] = rgba[destination + 1] = rgba[destination + 2] = rows[source];
                    rgba[destination + 3] = colourType == 4 ? rows[source + 1] : (byte)255;
                    break;

                case 2:
                case 6:
                    rgba[destination] = rows[source];
                    rgba[destination + 1] = rows[source + 1];
                    rgba[destination + 2] = rows[source + 2];
                    rgba[destination + 3] = colourType == 6 ? rows[source + 3] : (byte)255;
                    break;

                case 3:
                    int index = rows[source];
                    if (index * 3 + 2 >= palette.Length)
                        throw new InvalidDataException($"Palette index {index} is out of range.");
                    rgba[destination] = palette[index * 3];
                    rgba[destination + 1] = palette[index * 3 + 1];
                    rgba[destination + 2] = palette[index * 3 + 2];
                    rgba[destination + 3] = index < paletteAlpha.Length ? paletteAlpha[index] : (byte)255;
                    break;
            }
        }

        return rgba;
    }
}
