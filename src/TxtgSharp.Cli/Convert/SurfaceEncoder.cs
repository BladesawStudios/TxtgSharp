using AstcSharp;
using AstcSharp.Core;
using BCnEncoder.Decoder;
using BCnEncoder.Encoder;
using BCnEncoder.Encoder.Options;
using BCnEncoder.Shared;
using CommunityToolkit.HighPerformance;
namespace TxtgSharp.Cli;

internal enum AlphaKind
{
    Opaque,
    Binary,
    Smooth
}

internal static class SurfaceEncoder
{
    public static AlphaKind AlphaOf(byte[] rgba)
    {
        bool anyTransparent = false;

        for (int i = 3; i < rgba.Length; i += 4)
        {
            if (rgba[i] == 255) continue;
            if (rgba[i] != 0) return AlphaKind.Smooth;
            anyTransparent = true;
        }

        return anyTransparent ? AlphaKind.Binary : AlphaKind.Opaque;
    }

    // BC1 stores one bit of alpha per texel, picked per block, and costs nothing extra to use.
    public static AlphaKind AlphaCapacityOf(TargetFormat target) => target.Encoder switch
    {
        EncoderKind.Raw => target.Block.BytesPerBlock == 4 ? AlphaKind.Smooth : AlphaKind.Opaque,
        EncoderKind.Astc => AlphaKind.Smooth,
        _ => target.Bcn switch
        {
            CompressionFormat.Bc1 or CompressionFormat.Bc1WithAlpha => AlphaKind.Binary,
            CompressionFormat.Bc4 or CompressionFormat.Bc5 => AlphaKind.Opaque,
            _ => AlphaKind.Smooth
        }
    };

    public static byte[] Encode(
        byte[] rgba, int width, int height, TargetFormat target, CompressionQuality quality,
        bool encoderMayParallelise = true)
    {
        byte[] blocks = target.Encoder switch
        {
            EncoderKind.Bcn => EncodeBcn(rgba, width, height, target, quality, encoderMayParallelise),
            EncoderKind.Astc => EncodeAstc(rgba, width, height, target),
            EncoderKind.Raw => EncodeRaw(rgba, width, height, target),
            _ => throw new NotSupportedException($"No encoder for {target.Name}.")
        };

        int expected = ExpectedLength(width, height, target.Block);
        if (blocks.Length == expected)
            return blocks;

        if (blocks.Length > expected)
            return blocks.AsSpan(0, expected).ToArray();

        throw new InvalidDataException(
            $"{target.Name} encoder produced {blocks.Length} bytes for {width}x{height}, expected {expected}.");
    }

    public static byte[] Decode(byte[] blocks, int width, int height, string ddsFormat)
    {
        CompressionFormat format = ddsFormat switch
        {
            "bc1" => CompressionFormat.Bc1,
            "bc2" => CompressionFormat.Bc2,
            "bc3" => CompressionFormat.Bc3,
            "bc4" => CompressionFormat.Bc4,
            "bc5" => CompressionFormat.Bc5,
            "bc7" => CompressionFormat.Bc7,
            _ => throw new NotSupportedException($"Cannot decode DDS format {ddsFormat}.")
        };

        BcDecoder decoder = new();
        ColorRgba32[] pixels = decoder.DecodeRaw(blocks, width, height, format);

        byte[] rgba = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length && i * 4 + 3 < rgba.Length; i++)
        {
            rgba[i * 4] = pixels[i].r;
            rgba[i * 4 + 1] = pixels[i].g;
            rgba[i * 4 + 2] = pixels[i].b;
            rgba[i * 4 + 3] = pixels[i].a;
        }

        return rgba;
    }

    public static int ExpectedLength(int width, int height, TxtgBlockInfo block) =>
        (width + block.Width - 1) / block.Width
        * ((height + block.Height - 1) / block.Height)
        * block.BytesPerBlock;

    private static byte[] EncodeBcn(
        byte[] rgba, int width, int height, TargetFormat target, CompressionQuality quality,
        bool mayParallelise)
    {
        BcEncoder encoder = new();

        // Plain Bc1 throws alpha away; the alpha variant is the same 8 bytes a block, so it is
        // only ever the right choice once the source has something to keep.
        encoder.OutputOptions.Format =
            target.Bcn == CompressionFormat.Bc1 && AlphaOf(rgba) != AlphaKind.Opaque
                ? CompressionFormat.Bc1WithAlpha
                : target.Bcn;

        encoder.OutputOptions.Quality = quality;

        encoder.Options.IsParallel = mayParallelise;

        encoder.OutputOptions.GenerateMipMaps = false;
        encoder.OutputOptions.MaxMipMapLevel = 1;

        ColorRgba32[] pixels = new ColorRgba32[width * height];
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = new ColorRgba32(rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2], rgba[i * 4 + 3]);

        return encoder.EncodeToRawBytes(pixels.AsMemory().AsMemory2D(height, width), 0, out _, out _);
    }

    private static byte[] EncodeAstc(byte[] rgba, int width, int height, TargetFormat target)
    {
        Footprint footprint = FootprintOf(target.Block);
        using MemoryStream source = new(rgba);
        using MemoryStream destination = new();
        AstcEncoder.CompressImage(source, destination, width, height, footprint);
        return destination.ToArray();
    }

    private static byte[] EncodeRaw(byte[] rgba, int width, int height, TargetFormat target)
    {
        int channels = target.Block.BytesPerBlock;
        byte[] result = new byte[width * height * channels];

        for (int i = 0, pixels = width * height; i < pixels; i++)
            for (int c = 0; c < channels; c++)
                result[i * channels + c] = rgba[i * 4 + c];

        return result;
    }

    public static Footprint FootprintOf(TxtgBlockInfo block) => (block.Width, block.Height) switch
    {
        (4, 4) => Footprint.FromFootprintType(FootprintType.Footprint4x4),
        (5, 4) => Footprint.FromFootprintType(FootprintType.Footprint5x4),
        (5, 5) => Footprint.FromFootprintType(FootprintType.Footprint5x5),
        (6, 5) => Footprint.FromFootprintType(FootprintType.Footprint6x5),
        (6, 6) => Footprint.FromFootprintType(FootprintType.Footprint6x6),
        (8, 5) => Footprint.FromFootprintType(FootprintType.Footprint8x5),
        (8, 6) => Footprint.FromFootprintType(FootprintType.Footprint8x6),
        (8, 8) => Footprint.FromFootprintType(FootprintType.Footprint8x8),
        (10, 5) => Footprint.FromFootprintType(FootprintType.Footprint10x5),
        (10, 6) => Footprint.FromFootprintType(FootprintType.Footprint10x6),
        (10, 8) => Footprint.FromFootprintType(FootprintType.Footprint10x8),
        (10, 10) => Footprint.FromFootprintType(FootprintType.Footprint10x10),
        (12, 10) => Footprint.FromFootprintType(FootprintType.Footprint12x10),
        (12, 12) => Footprint.FromFootprintType(FootprintType.Footprint12x12),
        _ => throw new NotSupportedException($"No ASTC footprint for {block.Width}x{block.Height} blocks.")
    };
}
