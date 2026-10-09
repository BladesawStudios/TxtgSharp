using TexSharp;

namespace TxtgSharp;

/// <summary>Decoding and DDS export and import for a <see cref="TxtgFile"/>, through TexSharp.</summary>
public static class TxtgFileTexExtensions
{
    /// <summary>The TexSharp format for the file, or false if TexSharp has no equivalent.</summary>
    public static bool TryGetTextureFormat(this TxtgFile file, out TextureFormat format, out bool srgb)
        => file.Format.TryGetTextureFormat(file.BlockInfo, out format, out srgb);

    /// <summary>One mip of one layer as width * height * 4 bytes of RGBA8.</summary>
    public static byte[] ToRgba8(this TxtgFile file, int layer = 0, int mip = 0)
    {
        TextureFormat format = RequireFormat(file, out _);
        TxtgSurface surface = file.Surface(layer, mip)
            ?? throw new ArgumentOutOfRangeException(nameof(mip), $"No surface for layer {layer} mip {mip}.");
        return TextureDecoder.ToRgba8(format, surface.Data, surface.Width, surface.Height);
    }

    /// <summary>The channel swizzle stored in the header, which says how the texture's channels are meant to be read.</summary>
    public static ChannelMap GetChannelMap(this TxtgFile file)
    {
        var (red, green, blue, alpha) = file.ChannelSelectors;
        return ChannelMap.FromTxtg(red, green, blue, alpha);
    }

    /// <summary>
    /// <see cref="ToRgba8"/> as the texture is meant to be seen: its channel swizzle applied, and for a BC5 normal map
    /// the blue channel rebuilt.
    /// </summary>
    public static byte[] Render(this TxtgFile file, int layer = 0, int mip = 0)
    {
        TextureFormat format = RequireFormat(file, out _);
        TxtgSurface surface = file.Surface(layer, mip)
            ?? throw new ArgumentOutOfRangeException(nameof(mip), $"No surface for layer {layer} mip {mip}.");
        return TextureDecoder.Render(format, surface.Data, surface.Width, surface.Height, file.GetChannelMap());
    }

    /// <summary>A PNG of <see cref="Render"/>.</summary>
    public static byte[] ToPng(this TxtgFile file, int layer = 0, int mip = 0)
    {
        TxtgSurface surface = file.Surface(layer, mip)
            ?? throw new ArgumentOutOfRangeException(nameof(mip), $"No surface for layer {layer} mip {mip}.");
        return PngWriter.Encode(file.Render(layer, mip), surface.Width, surface.Height);
    }

    /// <summary>
    /// All mips of one layer as a DDS. The pixel data is passed through untouched, except with
    /// <paramref name="editable"/>: R8 and RG8 are then expanded to RGBA8, which image editors can open.
    /// <see cref="WithDds"/> collapses them again.
    /// </summary>
    public static DdsImage ToDds(this TxtgFile file, int layer = 0, bool editable = false)
    {
        TextureFormat format = RequireFormat(file, out bool srgb);
        bool expand = editable && PixelFormats.CanRoundTripThroughRgba8(format);

        List<byte[]> mips = [];
        for (int mip = 0; mip < file.MipCount; mip++)
        {
            TxtgSurface surface = file.Surface(layer, mip) ?? throw new InvalidDataException($"Layer {layer} is missing mip {mip}.");
            mips.Add(expand ? PixelFormats.ExpandToRgba8(format, surface.Data, surface.Width * surface.Height) : surface.Data);
        }

        return expand
            ? new DdsImage(file.Width, file.Height, TextureFormat.Rgba8, mips)
            : new DdsImage(file.Width, file.Height, format, mips, srgb);
    }

    /// <summary>
    /// A new file holding a DDS, with the original's header fields kept. The DDS's size and mip count may differ.
    /// A different compression format is allowed and changes the file's format and, for ASTC, its footprint;
    /// the pixels are not converted, the DDS is taken to be in the format it says. An RGBA8 DDS is collapsed back
    /// for the small formats <c>ToDds(editable: true)</c> expands. A DDS in the file's own format keeps the
    /// file's colour space unless the DDS states one.
    /// </summary>
    public static TxtgFile WithDds(this TxtgFile file, DdsImage dds)
    {
        ArgumentNullException.ThrowIfNull(dds);
        TextureFormat current = RequireFormat(file, out bool currentSrgb);

        if (file.LayerCount != 1)
            throw new NotSupportedException($"The file has {file.LayerCount} layers; only single-layer files can be replaced.");

        if (dds.Format == TextureFormat.Rgba8 && current != TextureFormat.Rgba8 && PixelFormats.CanRoundTripThroughRgba8(current))
        {
            var collapsed = new List<byte[]>();
            for (int mip = 0; mip < dds.Mips.Count; mip++)
                collapsed.Add(PixelFormats.CollapseRgba8(current, dds.Mips[mip], Math.Max(1, dds.Width >> mip) * Math.Max(1, dds.Height >> mip)));
            dds = new DdsImage(dds.Width, dds.Height, current, collapsed);
        }

        TxtgFormat format;
        TxtgBlockInfo block;
        int? rawCode = null;
        if (dds.Format == current && (!dds.ColorSpaceKnown || dds.IsSrgb == currentSrgb))
        {
            // Same format: keep the file's own code, which can be a variant ToRawCode would not give back.
            format = file.Format;
            block = file.BlockInfo;
            rawCode = file.RawFormatCode;
        }
        else if (!TxtgFormats.TryFromTextureFormat(dds.Format, dds.IsSrgb, out format, out block))
        {
            throw new NotSupportedException($"A {dds.Format} DDS can't be imported into a TXTG.");
        }

        var surfaces = new List<TxtgSurfaceData>(dds.Mips.Count);
        for (int mip = 0; mip < dds.Mips.Count; mip++)
            surfaces.Add(new TxtgSurfaceData(0, mip, dds.Mips[mip]));

        return TxtgFile.CreateFrom(file, dds.Width, dds.Height, format, surfaces, block, rawCode);
    }

    /// <summary>
    /// Replaces one layer of the file with a DDS, in place, leaving the other layers as they are. Every layer shares the
    /// file's size, format and mip count. A DDS that differs in any of them is converted to fit (resized, its mips
    /// rebuilt, re-encoded) unless <paramref name="convert"/> is false, in which case it is refused. An RGBA8 DDS is
    /// accepted for the small formats <c>ToDds(editable: true)</c> expands without counting as a conversion.
    /// Returns what was changed to make the DDS fit, in words; empty if it fitted as it was.
    /// </summary>
    public static IReadOnlyList<string> ReplaceLayerFromDds(this TxtgFile file, DdsImage dds, int layer, bool convert = true)
    {
        ArgumentNullException.ThrowIfNull(dds);
        TextureFormat current = RequireFormat(file, out bool srgb);

        int layers = Math.Max(1, file.LayerCount);
        if (layer < 0 || layer >= layers)
            throw new ArgumentOutOfRangeException(nameof(layer), layer, $"The file has {layers} layer(s).");

        if (dds.Format == TextureFormat.Rgba8 && current != TextureFormat.Rgba8 && PixelFormats.CanRoundTripThroughRgba8(current)
            && dds.Width == file.Width && dds.Height == file.Height && dds.Mips.Count == file.MipCount)
        {
            var collapsed = new List<byte[]>();
            for (int mip = 0; mip < dds.Mips.Count; mip++)
                collapsed.Add(PixelFormats.CollapseRgba8(current, dds.Mips[mip], Math.Max(1, dds.Width >> mip) * Math.Max(1, dds.Height >> mip)));
            dds = new DdsImage(dds.Width, dds.Height, current, collapsed);
        }

        List<string> changes = TextureConverter.Differences(dds, current, file.Width, file.Height, file.MipCount);
        IReadOnlyList<byte[]> mips = dds.Mips;
        if (changes.Count > 0)
        {
            if (!convert)
                throw new ArgumentException(
                    $"A layer has to match the file: it is {file.Width}x{file.Height} {current} with {file.MipCount} mip(s), " +
                    $"but the DDS is {dds.Width}x{dds.Height} {dds.Format} with {dds.Mips.Count}.", nameof(dds));
            mips = TextureConverter.Convert(dds, current, file.Width, file.Height, file.MipCount, srgb);
        }

        for (int mip = 0; mip < mips.Count; mip++)
        {
            TxtgSurface surface = file.Surface(layer, mip) ?? throw new InvalidDataException($"Layer {layer} is missing mip {mip}.");
            surface.Data = mips[mip];
        }
        return changes;
    }

    private static TextureFormat RequireFormat(TxtgFile file, out bool srgb)
        => file.TryGetTextureFormat(out TextureFormat format, out srgb)
            ? format
            : throw new NotSupportedException($"{file.FormatName} (0x{file.RawFormatCode:X4}) has no TexSharp equivalent.");
}
