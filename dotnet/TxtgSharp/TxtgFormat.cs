namespace TxtgSharp;

/// <summary>Texture formats a TexToGo container can hold.</summary>
public enum TxtgFormat
{
    Unknown = 0,
    Astc4x4Srgb,
    Astc8x5Unorm,
    Astc8x8Unorm,
    Astc8x8Srgb,
    Bc1Unorm,
    Bc1UnormSrgb,
    Bc3UnormSrgb,
    Bc4Unorm,
    Bc5Unorm,
    Bc7Unorm
}

/// <summary>Block geometry for a <see cref="TxtgFormat"/>. Every supported format is block compressed.</summary>
public readonly record struct TxtgBlockInfo(int Width, int Height, int BytesPerBlock);

public static class TxtgFormats
{
    /// <summary>
    /// Maps the container's raw format code. The codes are grouped by family in the high
    /// byte (1 = ASTC, 2/3 = BC1, 5 = BC3, 6 = BC4, 7 = BC5, 9 = BC7) with the low byte
    /// selecting the colour space and block size.
    /// </summary>
    public static TxtgFormat FromRawCode(int code) => code switch
    {
        0x101 => TxtgFormat.Astc8x5Unorm,
        0x102 => TxtgFormat.Astc8x8Unorm,
        0x105 => TxtgFormat.Astc8x8Srgb,
        0x109 => TxtgFormat.Astc4x4Srgb,
        0x202 => TxtgFormat.Bc1Unorm,
        0x203 => TxtgFormat.Bc1UnormSrgb,
        0x302 => TxtgFormat.Bc1Unorm,
        0x505 => TxtgFormat.Bc3UnormSrgb,
        0x602 or 0x606 or 0x607 => TxtgFormat.Bc4Unorm,
        0x702 or 0x703 or 0x707 => TxtgFormat.Bc5Unorm,
        0x901 => TxtgFormat.Bc7Unorm,
        _ => TxtgFormat.Unknown
    };

    public static bool IsAstc(this TxtgFormat format) => format is
        TxtgFormat.Astc4x4Srgb or TxtgFormat.Astc8x5Unorm or
        TxtgFormat.Astc8x8Unorm or TxtgFormat.Astc8x8Srgb;

    public static bool IsSrgb(this TxtgFormat format) => format is
        TxtgFormat.Astc4x4Srgb or TxtgFormat.Astc8x8Srgb or
        TxtgFormat.Bc1UnormSrgb or TxtgFormat.Bc3UnormSrgb;

    /// <summary>All supported formats use 16-byte blocks except BC1 and BC4, which use 8.</summary>
    public static TxtgBlockInfo BlockInfo(this TxtgFormat format) => format switch
    {
        TxtgFormat.Astc4x4Srgb => new TxtgBlockInfo(4, 4, 16),
        TxtgFormat.Astc8x5Unorm => new TxtgBlockInfo(8, 5, 16),
        TxtgFormat.Astc8x8Unorm or TxtgFormat.Astc8x8Srgb => new TxtgBlockInfo(8, 8, 16),
        TxtgFormat.Bc1Unorm or TxtgFormat.Bc1UnormSrgb => new TxtgBlockInfo(4, 4, 8),
        TxtgFormat.Bc4Unorm => new TxtgBlockInfo(4, 4, 8),
        TxtgFormat.Bc3UnormSrgb or TxtgFormat.Bc5Unorm or TxtgFormat.Bc7Unorm => new TxtgBlockInfo(4, 4, 16),
        _ => new TxtgBlockInfo(1, 1, 4)
    };
}
