using System.Buffers.Binary;

namespace TxtgSharp;

/// <summary>
/// One texture surface: a single mip level of a single array layer, stored independently in
/// the container. <see cref="Data"/> is deswizzled but still block compressed, so it can go
/// straight to a compressed GPU upload where the format is supported.
/// </summary>
/// <remarks>
/// A surface keeps the zstd frame it was read from, so a container that is written back
/// unchanged reproduces the original bytes without recompressing anything. Assigning
/// <see cref="Data"/> drops that shortcut for this surface and nothing else.
/// </remarks>
public sealed class TxtgSurface
{
    private readonly TxtgBlockInfo _block;
    private byte[]? _source;
    private byte[]? _encoded;
    private byte[]? _swizzled;
    private byte[]? _data;

    internal TxtgSurface(int arrayIndex, int mipLevel, int width, int height, TxtgBlockInfo block)
    {
        ArrayIndex = arrayIndex;
        MipLevel = mipLevel;
        Width = width;
        Height = height;
        _block = block;
    }

    public int ArrayIndex { get; }
    public int MipLevel { get; }
    public int Width { get; }
    public int Height { get; }

    /// <summary>
    /// Size of the swizzled payload before deswizzling; diagnostic for layout checks. Zero
    /// until the payload is unwrapped, for the rare frame that does not declare its size.
    /// </summary>
    public int SwizzledSize { get; internal set; }

    /// <summary>Raw index-table entry, preserved so a repack keeps the original table.</summary>
    internal uint IndexEntry { get; set; }

    /// <summary>The word that follows each compressed size; 6 in every retail container.</summary>
    internal uint Flags { get; set; } = DefaultFlags;

    internal const uint DefaultFlags = 6;

    /// <summary>Length <see cref="Data"/> must have: one tightly packed row of blocks per block row.</summary>
    public int DataLength => TxtgSwizzle.LinearSize(Width, Height, _block);

    /// <summary>True once <see cref="Data"/> has been assigned, so saving must re-encode this surface.</summary>
    public bool IsModified { get; private set; }

    /// <summary>
    /// Deswizzled, still block compressed. Deswizzling happens on first access and the
    /// swizzled payload is released afterwards, so a caller that only wants one mip does not
    /// pay for the rest of the container.
    /// </summary>
    /// <remarks>
    /// Assigning replaces the surface's contents; the value must be <see cref="DataLength"/>
    /// bytes of blocks in the container's own format, laid out left to right, top to bottom.
    /// Reads are safe to race, assignment is not.
    /// </remarks>
    public byte[] Data
    {
        get
        {
            byte[]? data = Volatile.Read(ref _data);
            if (data is not null)
                return data;

            // A race just deswizzles twice to the same bytes; the first result published wins.
            data = TxtgSwizzle.Deswizzle(Swizzled(), Width, Height, _block);
            byte[]? won = Interlocked.CompareExchange(ref _data, data, null);
            if (won is not null)
                return won;

            _swizzled = null;
            return data;
        }

        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value.Length != DataLength)
                throw new ArgumentException(
                    $"Surface layer {ArrayIndex} mip {MipLevel} is {Width}x{Height} and needs " +
                    $"{DataLength} bytes, got {value.Length}.", nameof(value));

            _data = value;
            _swizzled = null;
            _encoded = null;
            IsModified = true;
        }
    }

    internal void SetSource(byte[] compressed, int swizzledSize)
    {
        _source = compressed;
        SwizzledSize = swizzledSize;
    }

    /// <summary>
    /// The surface as the container stores it: block-linear, before deswizzling. Useful for a
    /// tool that writes its own container, or to check a re-swizzle against the original.
    /// </summary>
    public byte[] Swizzled()
    {
        if (_swizzled is not null)
            return _swizzled;

        if (!IsModified)
        {
            _swizzled = TxtgFile.Decompress(
                _source ?? throw new InvalidOperationException(
                    $"Surface layer {ArrayIndex} mip {MipLevel} has no payload."),
                SwizzledSize);

            SwizzledSize = _swizzled.Length;
            return _swizzled;
        }

        byte[] data = _data!;

        // A replaced surface keeps the length the original had. Retail payloads are trimmed to
        // the last byte the image occupies, and a freshly computed size lands on that same
        // value, so this only matters for containers that trimmed further still.
        int size = SwizzledSize > 0 ? SwizzledSize : TxtgSwizzle.SwizzledSize(Width, Height, _block);

        // Where a surface's height does not fill its last block row, the leftover rows are
        // padding the image never samples - and TotK ships them populated rather than zeroed.
        // Swizzling over the original payload leaves them as they were, so replacing a surface
        // changes only the bytes the image actually occupies.
        byte[]? seed = _source is null ? null : TxtgFile.Decompress(_source, SwizzledSize);
        return _swizzled = TxtgSwizzle.Swizzle(data, Width, Height, _block, size, seed);
    }

    /// <summary>
    /// The zstd frame to write. An untouched surface hands back the frame it was read from,
    /// so only surfaces the caller actually replaced cost a swizzle and a compression pass.
    /// </summary>
    internal byte[] Compressed(ZstdSharp.Compressor compressor)
    {
        if (!IsModified && _source is not null)
            return _source;

        if (_encoded is not null)
            return _encoded;

        byte[] swizzled = Swizzled();
        SwizzledSize = swizzled.Length;
        return _encoded = compressor.Wrap(swizzled).ToArray();
    }
}

/// <summary>
/// Reader and writer for the Tears of the Kingdom <c>TexToGo</c> (<c>.txtg</c>) texture container.
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

    private byte[] _header = [];

    public int Width { get; private init; }
    public int Height { get; private init; }

    /// <summary>Number of array layers.</summary>
    public int LayerCount { get; private init; }

    public int MipCount { get; private init; }

    /// <summary>The declared format family and colour space. For geometry use <see cref="BlockInfo"/>.</summary>
    public TxtgFormat Format { get; private init; }

    /// <summary>The format code exactly as the header declares it, and exactly as it is written back.</summary>
    public int RawFormatCode { get; private init; }

    /// <summary>
    /// Block geometry the container's surfaces actually use. The footprint comes from the
    /// header's settings word rather than the format code, which does not distinguish ASTC
    /// block sizes - see <see cref="TxtgFormats.TryGetFootprint"/>.
    /// </summary>
    public TxtgBlockInfo BlockInfo { get; private init; }

    /// <summary>Surfaces in container order, which is the order they are written back in.</summary>
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

        // The declared code does not distinguish ASTC block sizes, so it can name a footprint
        // the surfaces do not use - terrain arrays declare 0x101 (8x5) but are really 8x8.
        // The label gets nudged onto the right member; the code itself is left alone so it
        // survives a write back.
        int labelFormat = setting2 switch
        {
            32628 => 0x101,
            32631 => 0x102,
            _ => rawFormat
        };

        TxtgFormat format = TxtgFormats.FromRawCode(labelFormat);
        if (format == TxtgFormat.Unknown)
            throw new InvalidDataException($"Unknown txtg format code 0x{rawFormat:X4}.");

        TxtgBlockInfo block = format.BlockInfo();
        if (TxtgFormats.TryGetFootprint(setting2, out int footprintWidth, out int footprintHeight))
            block = block with { Width = footprintWidth, Height = footprintHeight };

        int surfaceCount = checked(layers * mips);
        var entries = new uint[surfaceCount];
        var sizes = new int[surfaceCount];
        var flags = new uint[surfaceCount];

        int cursor = HeaderSize;
        for (int i = 0; i < surfaceCount; i++)
        {
            entries[i] = BinaryPrimitives.ReadUInt32LittleEndian(data[cursor..]);
            cursor += 4;
        }

        for (int i = 0; i < surfaceCount; i++)
        {
            sizes[i] = BinaryPrimitives.ReadInt32LittleEndian(data[cursor..]);
            flags[i] = BinaryPrimitives.ReadUInt32LittleEndian(data[(cursor + 4)..]);
            cursor += 8;
        }

        var surfaces = new List<TxtgSurface>(surfaceCount);

        for (int i = 0; i < surfaceCount; i++)
        {
            int compressedSize = sizes[i];
            if (compressedSize <= 0 || cursor + compressedSize > data.Length) break;

            int layer = (int)(entries[i] & 0xFFFF);
            int mip = (int)(entries[i] >> 16 & 0xFF);
            int mipWidth = Math.Max(1, width >> mip);
            int mipHeight = Math.Max(1, height >> mip);

            ReadOnlySpan<byte> frame = data.Slice(cursor, compressedSize);
            cursor += compressedSize;

            TxtgSurface surface = new(layer, mip, mipWidth, mipHeight, block)
            {
                IndexEntry = entries[i],
                Flags = flags[i]
            };

            // Deswizzling is deferred, so keep the frame and read its declared size rather than
            // decompressing every surface up front. Every retail frame declares one; a frame
            // written by some other tool need not, and then the layout says how big it is.
            ulong declared = ZstdSharp.Decompressor.GetDecompressedSize(frame);
            surface.SetSource(frame.ToArray(), declared is > 0 and <= int.MaxValue ? (int)declared : 0);
            surfaces.Add(surface);
        }

        return new TxtgFile
        {
            _header = data[..HeaderSize].ToArray(),
            Width = width,
            Height = height,
            LayerCount = layers,
            MipCount = mips,
            Format = format,
            RawFormatCode = rawFormat,
            BlockInfo = block,
            Surfaces = surfaces
        };
    }

    /// <summary>Surfaces for one mip level, ordered by array layer - the shape an array upload wants.</summary>
    public IEnumerable<TxtgSurface> LayersOfMip(int mip) =>
        Surfaces.Where(s => s.MipLevel == mip).OrderBy(s => s.ArrayIndex);

    /// <summary>The surface for one layer and mip, or null if the container does not carry it.</summary>
    public TxtgSurface? Surface(int layer, int mip) =>
        Surfaces.FirstOrDefault(s => s.ArrayIndex == layer && s.MipLevel == mip);

    // ---------------------------------------------------------------- writing

    /// <summary>
    /// Serialises the container. Surfaces left untouched are written back as the exact zstd
    /// frames they were read from, so a read-then-write round trip of a retail file reproduces
    /// it byte for byte; replaced surfaces are re-swizzled and recompressed.
    /// </summary>
    public byte[] ToBytes(int compressionLevel = 12)
    {
        if (_header.Length != HeaderSize)
            throw new InvalidOperationException("No header to write; build the container with Create.");

        // The header goes out exactly as it came in, digest and sampler settings and all - none
        // of the fields it holds can change once a container is loaded. A truncated file is the
        // one case where the surfaces no longer agree with it, and writing that back would
        // produce a header promising rows the table does not have.
        if (Surfaces.Count != LayerCount * MipCount)
            throw new InvalidOperationException(
                $"Header declares {LayerCount * MipCount} surfaces but only {Surfaces.Count} are present; " +
                "the source was truncated and cannot be written back.");

        using ZstdSharp.Compressor compressor = new(compressionLevel);
        var frames = new byte[Surfaces.Count][];
        for (int i = 0; i < Surfaces.Count; i++)
            frames[i] = Surfaces[i].Compressed(compressor);

        int tableSize = Surfaces.Count * 12;
        int total = HeaderSize + tableSize + frames.Sum(f => f.Length);
        byte[] result = new byte[total];

        _header.CopyTo(result, 0);

        int indexCursor = HeaderSize;
        int sizeCursor = HeaderSize + Surfaces.Count * 4;
        int payloadCursor = HeaderSize + tableSize;

        for (int i = 0; i < Surfaces.Count; i++)
        {
            TxtgSurface surface = Surfaces[i];
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(indexCursor), surface.IndexEntry);
            indexCursor += 4;

            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(sizeCursor), frames[i].Length);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(sizeCursor + 4), surface.Flags);
            sizeCursor += 8;

            frames[i].CopyTo(result, payloadCursor);
            payloadCursor += frames[i].Length;
        }

        return result;
    }

    public void Save(string path, int compressionLevel = 12) => File.WriteAllBytes(path, ToBytes(compressionLevel));

    /// <summary>
    /// Builds a container from linear, block-compressed surfaces. Surfaces are written in the
    /// order given; retail files run layer-major, with every mip of a layer before the next.
    /// </summary>
    /// <remarks>
    /// A handful of header words - the 32-byte digest at 0x1C, the sampler settings at 0x40 and
    /// 0x4D - carry values this library cannot derive, and get the value retail files use most
    /// often. Editing a container read with <see cref="FromFile"/> keeps the real ones and is the
    /// safer route whenever an original exists.
    /// </remarks>
    public static TxtgFile Create(
        int width, int height, TxtgFormat format, IReadOnlyList<TxtgSurfaceData> surfaces,
        TxtgBlockInfo? blockInfo = null)
    {
        ArgumentNullException.ThrowIfNull(surfaces);
        if (surfaces.Count == 0)
            throw new ArgumentException("Need at least one surface.", nameof(surfaces));

        TxtgBlockInfo block = blockInfo ?? format.BlockInfo();
        int layers = surfaces.Max(s => s.ArrayIndex) + 1;
        int mips = surfaces.Max(s => s.MipLevel) + 1;

        if (surfaces.Count != layers * mips)
            throw new ArgumentException(
                $"Expected {layers * mips} surfaces for {layers} layers of {mips} mips, got {surfaces.Count}.",
                nameof(surfaces));

        var built = new List<TxtgSurface>(surfaces.Count);
        foreach (TxtgSurfaceData source in surfaces)
        {
            int mipWidth = Math.Max(1, width >> source.MipLevel);
            int mipHeight = Math.Max(1, height >> source.MipLevel);

            TxtgSurface surface = new(source.ArrayIndex, source.MipLevel, mipWidth, mipHeight, block)
            {
                // Layer in the low half, mip next, then the per-entry constant retail files use.
                IndexEntry = (uint)source.ArrayIndex | (uint)source.MipLevel << 16 | 1u << 24,
                Flags = TxtgSurface.DefaultFlags,
                Data = source.Data
            };
            built.Add(surface);
        }

        return new TxtgFile
        {
            _header = DefaultHeader(width, height, layers, mips, format.ToRawCode(), block),
            Width = width,
            Height = height,
            LayerCount = layers,
            MipCount = mips,
            Format = format,
            RawFormatCode = format.ToRawCode(),
            BlockInfo = block,
            Surfaces = built
        };
    }

    /// <summary>
    /// A 0x50 header carrying the fields this library understands, with the rest set to the
    /// values every retail container agrees on (or, where they disagree, the commonest).
    /// </summary>
    private static byte[] DefaultHeader(int width, int height, int layers, int mips, int rawFormat, TxtgBlockInfo block)
    {
        byte[] header = new byte[HeaderSize];

        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0), HeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(2), ExpectedVersion);
        Magic.CopyTo(header, 4);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(10), (ushort)height);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(12), (ushort)layers);
        header[14] = (byte)mips;
        header[15] = 2;
        header[0x10] = 1;

        // Channel swizzle: identity RGBA.
        header[0x18] = 0;
        header[0x19] = 1;
        header[0x1A] = 2;
        header[0x1B] = 3;

        // 0x1C..0x3C is a 32-byte digest left zeroed; no retail file zeroes it, but nothing
        // observed reads it back either.
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x3C), (ushort)rawFormat);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x3E), 768);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x40), 0x42900000);
        BinaryPrimitives.WriteUInt32LittleEndian(
            header.AsSpan(0x44), TxtgFormats.ToSetting2(block.Width, block.Height));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x48), 0x02000200);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x4C), 0x00010502);

        return header;
    }

    /// <summary>
    /// Unwraps one surface payload. A frame that declares its content size is decompressed
    /// straight into a buffer of that size; one that does not - which no retail container
    /// produces, but another tool's writer might - falls back to letting zstd size it.
    /// </summary>
    internal static byte[] Decompress(byte[] frame, int size)
    {
        using ZstdSharp.Decompressor decompressor = new();
        if (size <= 0)
            return decompressor.Unwrap(frame).ToArray();

        byte[] result = new byte[size];
        decompressor.Unwrap(frame, result);
        return result;
    }
}

/// <summary>One surface handed to <see cref="TxtgFile.Create"/>: linear, still block compressed.</summary>
public readonly record struct TxtgSurfaceData(int ArrayIndex, int MipLevel, byte[] Data);
