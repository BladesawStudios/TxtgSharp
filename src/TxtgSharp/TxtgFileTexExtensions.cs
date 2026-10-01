using TexSharp;

namespace TxtgSharp;

/// <summary>Decoding and DDS export for a <see cref="TxtgFile"/>, through TexSharp.</summary>
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

    /// <summary>All mips of one layer as a DDS. The pixel data is passed through untouched.</summary>
    public static DdsImage ToDds(this TxtgFile file, int layer = 0)
    {
        TextureFormat format = RequireFormat(file, out bool srgb);

        List<byte[]> mips = [];
        for (int mip = 0; mip < file.MipCount; mip++)
            mips.Add((file.Surface(layer, mip) ?? throw new InvalidDataException($"Layer {layer} is missing mip {mip}.")).Data);

        return new DdsImage(file.Width, file.Height, format, mips, srgb);
    }

    /// <summary>
    /// Writes a DDS over one layer. The DDS has to be the same format and size as the file and carry every mip
    /// it has; changing either means building a new container.
    /// </summary>
    public static void ReplaceFromDds(this TxtgFile file, DdsImage dds, int layer = 0)
    {
        ArgumentNullException.ThrowIfNull(dds);
        TextureFormat format = RequireFormat(file, out _);

        if (dds.Format != format)
            throw new ArgumentException($"The file is {format}, the DDS is {dds.Format}.", nameof(dds));
        if (dds.Width != file.Width || dds.Height != file.Height)
            throw new ArgumentException($"The file is {file.Width}x{file.Height}, the DDS is {dds.Width}x{dds.Height}.", nameof(dds));
        if (dds.Mips.Count < file.MipCount)
            throw new ArgumentException($"The file has {file.MipCount} mips, the DDS has {dds.Mips.Count}.", nameof(dds));

        for (int mip = 0; mip < file.MipCount; mip++)
            (file.Surface(layer, mip) ?? throw new InvalidDataException($"Layer {layer} is missing mip {mip}.")).Data = dds.Mips[mip];
    }

    private static TextureFormat RequireFormat(TxtgFile file, out bool srgb)
        => file.TryGetTextureFormat(out TextureFormat format, out srgb)
            ? format
            : throw new NotSupportedException($"{file.FormatName} (0x{file.RawFormatCode:X4}) has no TexSharp equivalent.");
}
