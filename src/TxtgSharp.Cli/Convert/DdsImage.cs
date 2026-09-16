using System.Buffers.Binary;
namespace TxtgSharp.Cli;

internal sealed class DdsImage
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int MipCount { get; init; }
    public required int ArraySize { get; init; }

    public required string FormatName { get; init; }

    public required bool IsSrgb { get; init; }

    public TxtgBlockInfo? Block { get; init; }

    public required byte[][][] Surfaces { get; init; }

    public bool IsCompressed => Block is not null;

    private const uint Magic = 0x20534444;
    private const uint FourCcFlag = 0x4;
    private const uint RgbFlag = 0x40;
    private const uint AlphaPixelsFlag = 0x1;

    public static DdsImage Read(string path)
    {
        byte[] file = File.ReadAllBytes(path);
        if (file.Length < 128 || BinaryPrimitives.ReadUInt32LittleEndian(file) != Magic)
            throw new InvalidDataException($"{Path.GetFileName(path)} is not a DDS file.");

        int height = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(0x0C));
        int width = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(0x10));
        int mips = Math.Max(1, BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(0x1C)));

        uint pixelFlags = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(0x50));
        uint fourCc = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(0x54));
        int rgbBitCount = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(0x58));
        uint maskR = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(0x5C));
        uint maskG = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(0x60));
        uint maskB = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(0x64));

        int dataStart = 128;
        int arraySize = 1;
        bool srgb = false;
        string name;

        if ((pixelFlags & FourCcFlag) != 0 && fourCc == 0x30315844)
        {
            if (file.Length < 148)
                throw new InvalidDataException("DDS claims a DX10 header but is too short for one.");

            int dxgi = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(128));
            arraySize = Math.Max(1, BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(128 + 12)));
            dataStart = 148;
            name = DxgiName(dxgi);
            srgb = dxgi is 29 or 88 or 72 or 75 or 78 or 99;
        }
        else if ((pixelFlags & FourCcFlag) != 0)
        {
            name = FourCcName(fourCc);
        }
        else if ((pixelFlags & RgbFlag) != 0 && rgbBitCount == 32)
        {
            name = maskR == 0x00FF0000 && maskB == 0x000000FF ? "bgra8" : "rgba8";
        }
        else if (rgbBitCount == 16 && maskG != 0)
        {
            name = "rg8";
        }
        else if (rgbBitCount == 8)
        {
            name = "r8";
        }
        else
        {
            throw new NotSupportedException(
                $"Unsupported DDS pixel format (flags 0x{pixelFlags:X}, {rgbBitCount} bpp, fourCC 0x{fourCc:X8}).");
        }

        TxtgBlockInfo? block = TargetFormat.BlockOf(name);
        var surfaces = new byte[arraySize][][];
        int cursor = dataStart;

        for (int layer = 0; layer < arraySize; layer++)
        {
            surfaces[layer] = new byte[mips][];
            for (int mip = 0; mip < mips; mip++)
            {
                int mipWidth = Math.Max(1, width >> mip);
                int mipHeight = Math.Max(1, height >> mip);
                int size = SurfaceSize(mipWidth, mipHeight, block, rgbBitCount);

                if (cursor + size > file.Length)
                    throw new InvalidDataException(
                        $"DDS is short: layer {layer} mip {mip} wants {size} bytes, only " +
                        $"{file.Length - cursor} remain. The header may claim more mips than the file holds.");

                surfaces[layer][mip] = file.AsSpan(cursor, size).ToArray();
                cursor += size;
            }
        }

        return new DdsImage
        {
            Width = width,
            Height = height,
            MipCount = mips,
            ArraySize = arraySize,
            FormatName = name,
            IsSrgb = srgb,
            Block = block,
            Surfaces = surfaces
        };
    }

    private static int SurfaceSize(int width, int height, TxtgBlockInfo? block, int rgbBitCount)
    {
        if (block is { } b)
            return (width + b.Width - 1) / b.Width * ((height + b.Height - 1) / b.Height) * b.BytesPerBlock;

        return width * height * Math.Max(1, rgbBitCount / 8);
    }

    public byte[] ToRgba(int layer, int mip)
    {
        byte[] source = Surfaces[layer][mip];
        int width = Math.Max(1, Width >> mip), height = Math.Max(1, Height >> mip);
        byte[] rgba = new byte[width * height * 4];

        for (int i = 0, pixels = width * height; i < pixels; i++)
        {
            int d = i * 4;
            switch (FormatName)
            {
                case "rgba8":
                    source.AsSpan(i * 4, 4).CopyTo(rgba.AsSpan(d));
                    break;
                case "bgra8":
                    rgba[d] = source[i * 4 + 2];
                    rgba[d + 1] = source[i * 4 + 1];
                    rgba[d + 2] = source[i * 4];
                    rgba[d + 3] = source[i * 4 + 3];
                    break;
                case "rg8":
                    rgba[d] = source[i * 2];
                    rgba[d + 1] = source[i * 2 + 1];
                    rgba[d + 3] = 255;
                    break;
                case "r8":
                    rgba[d] = rgba[d + 1] = rgba[d + 2] = source[i];
                    rgba[d + 3] = 255;
                    break;
                default:
                    throw new NotSupportedException($"{FormatName} is not an uncompressed DDS format.");
            }
        }

        return rgba;
    }

    private static string FourCcName(uint fourCc) => fourCc switch
    {
        0x31545844 => "bc1",
        0x33545844 => "bc2",
        0x35545844 => "bc3",
        0x55344342 or 0x31495441 => "bc4",
        0x55354342 or 0x32495441 => "bc5",
        _ => throw new NotSupportedException($"Unsupported DDS fourCC 0x{fourCc:X8}.")
    };

    private static string DxgiName(int dxgi) => dxgi switch
    {
        28 or 29 => "rgba8",
        87 or 88 => "bgra8",
        61 => "r8",
        49 => "rg8",
        70 or 71 or 72 => "bc1",
        73 or 74 or 75 => "bc2",
        76 or 77 or 78 => "bc3",
        79 or 80 => "bc4",
        82 or 83 => "bc5",
        97 or 98 or 99 => "bc7",
        _ => throw new NotSupportedException($"Unsupported DXGI format {dxgi}.")
    };
}
