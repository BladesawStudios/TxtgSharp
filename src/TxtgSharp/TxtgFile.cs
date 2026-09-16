using System.Buffers.Binary;

namespace TxtgSharp;

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

    public int SwizzledSize { get; internal set; }

    internal uint IndexEntry { get; set; }

    internal uint Flags { get; set; } = DefaultFlags;

    internal const uint DefaultFlags = 6;

    public int DataLength => TxtgSwizzle.LinearSize(Width, Height, _block);

    public bool IsModified { get; private set; }

    public byte[] Data
    {
        get
        {
            byte[]? data = Volatile.Read(ref _data);
            if (data is not null)
                return data;

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

        int size = SwizzledSize > 0 ? SwizzledSize : TxtgSwizzle.SwizzledSize(Width, Height, _block);

        byte[]? seed = _source is null ? null : TxtgFile.Decompress(_source, SwizzledSize);
        return _swizzled = TxtgSwizzle.Swizzle(data, Width, Height, _block, size, seed);
    }

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

public sealed class TxtgFile
{
    private const int HeaderSize = 0x50;
    private const ushort ExpectedVersion = 0x11;
    private static readonly byte[] Magic = "6PK0"u8.ToArray();

    private byte[] _header = [];

    public int Width { get; private init; }
    public int Height { get; private init; }

    public int LayerCount { get; private init; }

    public int MipCount { get; private init; }

    public TxtgFormat Format { get; private init; }

    public int RawFormatCode { get; private init; }

    public TxtgBlockInfo BlockInfo { get; private init; }

    public IReadOnlyList<TxtgSurface> Surfaces { get; private init; } = [];

    public string FormatName
    {
        get
        {
            string family = Format switch
            {
                TxtgFormat.Bc1Unorm or TxtgFormat.Bc1UnormSrgb => "BC1",
                TxtgFormat.Bc3UnormSrgb => "BC3",
                TxtgFormat.Bc4Unorm => "BC4",
                TxtgFormat.Bc5Unorm => "BC5",
                TxtgFormat.Bc7Unorm => "BC7",
                TxtgFormat.R8Unorm => "R8",
                TxtgFormat.R8G8Unorm => "R8G8",
                TxtgFormat.R8G8B8A8Unorm => "R8G8B8A8",
                _ when Format.IsAstc() => $"ASTC {BlockInfo.Width}x{BlockInfo.Height}",
                _ => Format.ToString()
            };

            return $"{family} {(Format.IsSrgb() ? "sRGB" : "unorm")}";
        }
    }

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

        TxtgFormat format = TxtgFormats.FromRawCode(rawFormat);
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

    public IEnumerable<TxtgSurface> LayersOfMip(int mip) =>
        Surfaces.Where(s => s.MipLevel == mip).OrderBy(s => s.ArrayIndex);

    public TxtgSurface? Surface(int layer, int mip) =>
        Surfaces.FirstOrDefault(s => s.ArrayIndex == layer && s.MipLevel == mip);

    public byte[] ToBytes(int compressionLevel = 12)
    {
        if (_header.Length != HeaderSize)
            throw new InvalidOperationException("No header to write; build the container with Create.");

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

    public static TxtgFile Create(
        int width, int height, TxtgFormat format, IReadOnlyList<TxtgSurfaceData> surfaces,
        TxtgBlockInfo? blockInfo = null) =>
        Build(null, width, height, format, surfaces, blockInfo);

    public static TxtgFile CreateFrom(
        TxtgFile template, int width, int height, TxtgFormat format, IReadOnlyList<TxtgSurfaceData> surfaces,
        TxtgBlockInfo? blockInfo = null)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (template._header.Length != HeaderSize)
            throw new ArgumentException("Template has no header to borrow.", nameof(template));

        return Build(template._header, width, height, format, surfaces, blockInfo);
    }

    private static TxtgFile Build(
        byte[]? templateHeader, int width, int height, TxtgFormat format,
        IReadOnlyList<TxtgSurfaceData> surfaces, TxtgBlockInfo? blockInfo)
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
                IndexEntry = (uint)source.ArrayIndex | (uint)source.MipLevel << 16 | 1u << 24,
                Flags = TxtgSurface.DefaultFlags,
                Data = source.Data
            };
            built.Add(surface);
        }

        int rawCode = format.ToRawCode();

        return new TxtgFile
        {
            _header = BuildHeader(templateHeader, width, height, layers, mips, rawCode, block),
            Width = width,
            Height = height,
            LayerCount = layers,
            MipCount = mips,
            Format = format,
            RawFormatCode = rawCode,
            BlockInfo = block,
            Surfaces = built
        };
    }

    private static byte[] BuildHeader(
        byte[]? template, int width, int height, int layers, int mips, int rawFormat, TxtgBlockInfo block)
    {
        byte[] header = new byte[HeaderSize];
        if (template is not null)
            template.CopyTo(header, 0);
        else
            WriteDefaultHeaderFields(header);

        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0), HeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(2), ExpectedVersion);
        Magic.CopyTo(header, 4);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(10), (ushort)height);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(12), (ushort)layers);
        header[14] = (byte)mips;
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x3C), (ushort)rawFormat);
        BinaryPrimitives.WriteUInt32LittleEndian(
            header.AsSpan(0x44), TxtgFormats.ToSetting2(block.Width, block.Height));

        return header;
    }

    private static void WriteDefaultHeaderFields(byte[] header)
    {
        header[15] = 2;
        header[0x10] = 1;

        header[0x18] = 0;
        header[0x19] = 1;
        header[0x1A] = 2;
        header[0x1B] = 3;

        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x3E), 768);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x40), 0x42900000);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x48), 0x02000200);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x4C), 0x00010502);
    }

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

public readonly record struct TxtgSurfaceData(int ArrayIndex, int MipLevel, byte[] Data);
