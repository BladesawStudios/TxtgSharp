using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace TxtgSharp;

/// <summary>
/// One texture surface: a single mip level of a single array layer, stored independently in
/// the container. <see cref="Data"/> is deswizzled but still block compressed, so it can go
/// straight to a compressed GPU upload where the format is supported.
/// </summary>
public sealed class TxtgSurface
{
    private readonly TxtgBlockInfo _block;
    private byte[]? _swizzled;
    private byte[]? _data;

    internal TxtgSurface(int arrayIndex, int mipLevel, int width, int height, byte[] swizzled, TxtgBlockInfo block)
    {
        ArrayIndex = arrayIndex;
        MipLevel = mipLevel;
        Width = width;
        Height = height;
        SwizzledSize = swizzled.Length;
        _swizzled = swizzled;
        _block = block;
    }

    public int ArrayIndex { get; }
    public int MipLevel { get; }
    public int Width { get; }
    public int Height { get; }

    /// <summary>Size of the swizzled payload before deswizzling; diagnostic for layout checks.</summary>
    public int SwizzledSize { get; }

    /// <summary>
    /// Deswizzled, still block compressed. Deswizzling happens on first access and the
    /// swizzled payload is released afterwards, so a caller that only wants one mip does not
    /// pay for the rest of the container.
    /// </summary>
    public byte[] Data
    {
        get
        {
            byte[]? data = Volatile.Read(ref _data);
            if (data is not null)
                return data;

            // A race just deswizzles twice to the same bytes; the first result published wins.
            data = TxtgFile.Deswizzle(_swizzled!, Width, Height, _block);
            byte[]? won = Interlocked.CompareExchange(ref _data, data, null);
            if (won is not null)
                return won;

            _swizzled = null;
            return data;
        }
    }
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

            // Unwrap(src) allocates its own buffer and hands back a span over it, so taking
            // the array form here would copy the whole payload a second time.
            ReadOnlySpan<byte> frame = data.Slice(cursor, compressedSize);
            byte[] swizzled = new byte[ZstdSharp.Decompressor.GetDecompressedSize(frame)];
            decompressor.Unwrap(frame, swizzled);
            cursor += compressedSize;

            surfaces.Add(new TxtgSurface(layer, mip, mipWidth, mipHeight, swizzled, block));
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
    /// <remarks>
    /// Walks one GOB at a time. A GOB is 512 contiguous source bytes spanning 8 rows, so this
    /// order keeps reads local instead of striding the whole surface once per row. Within a
    /// GOB row the transfer unit is a 16-byte sector: every term of the source address either
    /// selects a sector or is <c>xBytes % 16</c>, so an aligned 16-byte run of row bytes is
    /// contiguous on both sides whatever the format's block size.
    /// </remarks>
    internal static byte[] Deswizzle(byte[] source, int width, int height, TxtgBlockInfo block)
    {
        int widthInBlocks = DivRoundUp(width, block.Width);
        int heightInBlocks = DivRoundUp(height, block.Height);
        int bpp = block.BytesPerBlock;

        int blockHeight = BlockHeight(heightInBlocks);
        int gobsPerRow = DivRoundUp(widthInBlocks * bpp, 64);
        int gobColumnBytes = 512 * blockHeight;
        int blockRowBytes = gobColumnBytes * gobsPerRow;
        int blockRows = 8 * blockHeight;
        int rowBytes = widthInBlocks * bpp;

        byte[] result = new byte[rowBytes * heightInBlocks];

        ref byte sourceBase = ref MemoryMarshal.GetReference(source.AsSpan());
        ref byte resultBase = ref MemoryMarshal.GetReference(result.AsSpan());

        for (int gobY = 0; gobY < heightInBlocks; gobY += 8)
        {
            int gobRowBase = (gobY / blockRows) * blockRowBytes + (gobY % blockRows / 8) * 512;

            for (int gobX = 0; gobX < gobsPerRow; gobX++)
            {
                int gobBase = gobRowBase + gobX * gobColumnBytes;
                int gobXBytes = gobX * 64;

                int rows = Math.Min(8, heightInBlocks - gobY);
                for (int y = 0; y < rows; y++)
                {
                    int rowBase = gobBase + (y >> 1) * 64 + (y & 1) * 16;
                    int destinationRow = (gobY + y) * rowBytes;

                    for (int sector = 0; sector < 4; sector++)
                    {
                        int xBytes = gobXBytes + sector * 16;
                        if (xBytes >= rowBytes) break;

                        int offset = rowBase + (sector >> 1) * 256 + (sector & 1) * 32;
                        if (offset < 0 || offset >= source.Length) continue;

                        int length = Math.Min(16, rowBytes - xBytes);
                        length = Math.Min(length, source.Length - offset);

                        if (length == 16)
                            Unsafe.WriteUnaligned(
                                ref Unsafe.Add(ref resultBase, (nint)(destinationRow + xBytes)),
                                Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref sourceBase, (nint)offset)));
                        else
                            Buffer.BlockCopy(source, offset, result, destinationRow + xBytes, length);
                    }
                }
            }
        }

        return result;
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
