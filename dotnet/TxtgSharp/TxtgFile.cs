using System.Buffers.Binary;

namespace TxtgSharp;

/// <summary>
/// One texture surface: a single mip level of a single array layer, stored independently in
/// the container. <see cref="Data"/> is deswizzled but still block compressed, so it can go
/// straight to a compressed GPU upload where the format is supported.
/// </summary>
public sealed class TxtgSurface
{
    public required int ArrayIndex { get; init; }
    public required int MipLevel { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required byte[] Data { get; init; }

    /// <summary>Size of the swizzled payload before deswizzling; diagnostic for layout checks.</summary>
    public required int SwizzledSize { get; init; }
}

/// <summary>
/// Reader for the Tears of the Kingdom <c>TexToGo</c> (<c>.txtg</c>) texture container.
/// </summary>
/// <remarks>
/// Layout: a fixed 0x50 header, then an index table of
/// <c>mipCount * layerCount</c> entries, then the matching compressed-size table, then the
/// zstd payloads back to back. Each payload holds one swizzled surface.
/// </remarks>
public sealed class TxtgFile
{
    private const int HeaderSize = 0x50;
    private const ushort ExpectedVersion = 0x11;
    private static readonly byte[] Magic = "6PK0"u8.ToArray();

    public int Width { get; private init; }
    public int Height { get; private init; }

    /// <summary>Number of array layers.</summary>
    public int LayerCount { get; private init; }

    public int MipCount { get; private init; }
    public TxtgFormat Format { get; private init; }
    public int RawFormatCode { get; private init; }
    public IReadOnlyList<TxtgSurface> Surfaces { get; private init; } = [];

    public static TxtgFile FromFile(string path) => FromBytes(File.ReadAllBytes(path));

    public static TxtgFile FromBytes(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize)
            throw new InvalidDataException($"Too small to be a txtg file ({data.Length} bytes).");

        if (!data[4..8].SequenceEqual(Magic))
            throw new InvalidDataException("Bad magic, expected '6PK0'.");

        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(data[2..]);
        if (version != ExpectedVersion)
            throw new InvalidDataException($"Unsupported txtg version 0x{version:X2}, expected 0x{ExpectedVersion:X2}.");

        int width = BinaryPrimitives.ReadUInt16LittleEndian(data[8..]);
        int height = BinaryPrimitives.ReadUInt16LittleEndian(data[10..]);
        int layers = BinaryPrimitives.ReadUInt16LittleEndian(data[12..]);
        int mips = data[14];

        int rawFormat = BinaryPrimitives.ReadUInt16LittleEndian(data[0x3C..]);
        uint setting2 = BinaryPrimitives.ReadUInt32LittleEndian(data[0x44..]);

        // The declared format does not distinguish some ASTC block sizes; the settings word
        // does. Terrain arrays declare 0x101 (8x5) but are really 8x8.
        int effectiveFormat = setting2 switch
        {
            32628 => 0x101,
            32631 => 0x102,
            _ => rawFormat
        };

        TxtgFormat format = TxtgFormats.FromRawCode(effectiveFormat);
        if (format == TxtgFormat.Unknown)
            throw new InvalidDataException($"Unknown txtg format code 0x{effectiveFormat:X4}.");

        int surfaceCount = checked(layers * mips);
        var entries = new (int Layer, int Mip)[surfaceCount];
        var sizes = new int[surfaceCount];

        int cursor = HeaderSize;
        for (int i = 0; i < surfaceCount; i++)
        {
            entries[i] = (BinaryPrimitives.ReadUInt16LittleEndian(data[cursor..]), data[cursor + 2]);
            cursor += 4;
        }

        for (int i = 0; i < surfaceCount; i++)
        {
            sizes[i] = BinaryPrimitives.ReadInt32LittleEndian(data[cursor..]);
            cursor += 8;   // compressed size, then a constant we do not need
        }

        TxtgBlockInfo block = format.BlockInfo();
        var surfaces = new List<TxtgSurface>(surfaceCount);
        using ZstdSharp.Decompressor decompressor = new();

        for (int i = 0; i < surfaceCount; i++)
        {
            int compressedSize = sizes[i];
            if (compressedSize <= 0 || cursor + compressedSize > data.Length) break;

            var (layer, mip) = entries[i];
            int mipWidth = Math.Max(1, width >> mip);
            int mipHeight = Math.Max(1, height >> mip);

            byte[] swizzled = decompressor.Unwrap(data.Slice(cursor, compressedSize)).ToArray();
            cursor += compressedSize;

            surfaces.Add(new TxtgSurface
            {
                ArrayIndex = layer,
                MipLevel = mip,
                Width = mipWidth,
                Height = mipHeight,
                SwizzledSize = swizzled.Length,
                Data = Deswizzle(swizzled, mipWidth, mipHeight, block)
            });
        }

        return new TxtgFile
        {
            Width = width,
            Height = height,
            LayerCount = layers,
            MipCount = mips,
            Format = format,
            RawFormatCode = effectiveFormat,
            Surfaces = surfaces
        };
    }

    /// <summary>Surfaces for one mip level, ordered by array layer - the shape an array upload wants.</summary>
    public IEnumerable<TxtgSurface> LayersOfMip(int mip) =>
        Surfaces.Where(s => s.MipLevel == mip).OrderBy(s => s.ArrayIndex);

    // ------------------------------------------------------------- swizzling

    /// <summary>
    /// Converts Tegra block-linear layout to linear. Texels are grouped into 64x8-byte GOBs;
    /// a "block" stacks <c>blockHeight</c> GOBs vertically, and blocks run in column-major
    /// order across the image.
    /// </summary>
    private static byte[] Deswizzle(byte[] source, int width, int height, TxtgBlockInfo block)
    {
        int widthInBlocks = DivRoundUp(width, block.Width);
        int heightInBlocks = DivRoundUp(height, block.Height);
        int bpp = block.BytesPerBlock;

        int blockHeight = BlockHeight(heightInBlocks);
        int gobsPerRow = DivRoundUp(widthInBlocks * bpp, 64);

        byte[] result = new byte[widthInBlocks * heightInBlocks * bpp];

        for (int y = 0; y < heightInBlocks; y++)
        {
            for (int x = 0; x < widthInBlocks; x++)
            {
                int offset = SwizzledOffset(x, y, gobsPerRow, bpp, blockHeight);
                int destination = (y * widthInBlocks + x) * bpp;

                if (offset < 0 || offset + bpp > source.Length) continue;
                Buffer.BlockCopy(source, offset, result, destination, bpp);
            }
        }

        return result;
    }

    private static int SwizzledOffset(int x, int y, int gobsPerRow, int bytesPerBlock, int blockHeight)
    {
        int xBytes = x * bytesPerBlock;

        // Which block-of-GOBs the texel falls in, then where inside that GOB.
        int gob = (y / (8 * blockHeight)) * 512 * blockHeight * gobsPerRow
                + (xBytes / 64) * 512 * blockHeight
                + (y % (8 * blockHeight) / 8) * 512;

        return gob
            + (xBytes % 64 / 32) * 256
            + (y % 8 / 2) * 64
            + (xBytes % 32 / 16) * 32
            + (y % 2) * 16
            + xBytes % 16;
    }

    /// <summary>GOBs stacked per block, as a power of two capped at 16.</summary>
    private static int BlockHeight(int heightInBlocks)
    {
        int value = PowerOfTwoAtLeast(DivRoundUp(heightInBlocks, 8));
        return Math.Clamp(value, 1, 16);
    }

    private static int DivRoundUp(int value, int divisor) => (value + divisor - 1) / divisor;

    private static int PowerOfTwoAtLeast(int value)
    {
        int result = 1;
        while (result < value) result <<= 1;
        return result;
    }
}
