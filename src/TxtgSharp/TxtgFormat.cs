namespace TxtgSharp;

/// <summary>Texture formats a TexToGo container can hold.</summary>
/// <remarks>
/// The ASTC members name the block size the declared format code implies. That code does not
/// actually pin the footprint down - <see cref="TxtgFile.BlockInfo"/> does, from the settings
/// word - so treat these as a family-and-colour-space label rather than as geometry.
/// </remarks>
public enum TxtgFormat
{
    Unknown = 0,
    Astc4x4Srgb,
    Astc4x4Unorm,
    Astc8x5Unorm,
    Astc8x8Unorm,
    Astc8x8Srgb,
    Bc1Unorm,
    Bc1UnormSrgb,
    Bc3UnormSrgb,
    Bc4Unorm,
    Bc5Unorm,
    Bc7Unorm,

    /// <summary>Uncompressed, one byte per texel.</summary>
    R8Unorm,

    /// <summary>Uncompressed, two bytes per texel.</summary>
    R8G8Unorm,

    /// <summary>Uncompressed, four bytes per texel.</summary>
    R8G8B8A8Unorm
}

/// <summary>
/// Block geometry for a <see cref="TxtgFormat"/>. Most formats are block compressed; the
/// uncompressed ones describe themselves as 1x1 blocks of one texel.
/// </summary>
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
        0x102 or 0x106 or 0x107 or 0x10A or 0x10C => TxtgFormat.Astc8x8Unorm,
        0x105 => TxtgFormat.Astc8x8Srgb,
        0x109 => TxtgFormat.Astc4x4Srgb,
        0x202 or 0x302 => TxtgFormat.Bc1Unorm,
        0x203 or 0x303 or 0x305 => TxtgFormat.Bc1UnormSrgb,
        0x505 => TxtgFormat.Bc3UnormSrgb,
        0x602 or 0x605 or 0x606 or 0x607 or 0x609 => TxtgFormat.Bc4Unorm,
        0x702 or 0x703 or 0x705 or 0x707 or 0x709 => TxtgFormat.Bc5Unorm,
        0x901 => TxtgFormat.Bc7Unorm,

        // The uncompressed codes repeat their family in the low byte and run widest first.
        // Their texel size is measured from the containers that use them; the channel order is
        // not, and the header's swizzle field at 0x18 has the last word on it anyway.
        0xA0A => TxtgFormat.R8G8B8A8Unorm,
        0xB0B => TxtgFormat.R8G8Unorm,
        0xC0C => TxtgFormat.R8Unorm,

        _ => TxtgFormat.Unknown
    };

    /// <summary>Raw format code to write back for a format, used when building a container from scratch.</summary>
    public static int ToRawCode(this TxtgFormat format) => format switch
    {
        TxtgFormat.Astc8x5Unorm => 0x101,
        TxtgFormat.Astc4x4Unorm or TxtgFormat.Astc8x8Unorm => 0x102,
        TxtgFormat.Astc8x8Srgb => 0x105,
        TxtgFormat.Astc4x4Srgb => 0x109,
        TxtgFormat.Bc1Unorm => 0x202,
        TxtgFormat.Bc1UnormSrgb => 0x203,
        TxtgFormat.Bc3UnormSrgb => 0x505,
        TxtgFormat.Bc4Unorm => 0x606,
        TxtgFormat.Bc5Unorm => 0x707,
        TxtgFormat.Bc7Unorm => 0x901,
        TxtgFormat.R8G8B8A8Unorm => 0xA0A,
        TxtgFormat.R8G8Unorm => 0xB0B,
        TxtgFormat.R8Unorm => 0xC0C,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "No raw code for this format.")
    };

    /// <summary>
    /// Block footprint carried by the header's second settings word, which is what the game
    /// really goes by. The low byte holds <c>(width - 1) &lt;&lt; 4 | (height - 1)</c> over a
    /// constant <c>0x7F00</c>, so 0x7F33 is 4x4, 0x7F75 is 8x6 and 0x7F77 is 8x8. Every ASTC
    /// footprint from 4x4 to 12x12 shows up across TotK's containers, including several the
    /// declared format code gets wrong.
    /// </summary>
    public static bool TryGetFootprint(uint setting2, out int width, out int height)
    {
        width = ((int)(setting2 >> 4) & 0xF) + 1;
        height = ((int)setting2 & 0xF) + 1;
        return (setting2 & 0xFFFFFF00) == 0x7F00;
    }

    /// <summary>The settings word that encodes a footprint, the inverse of <see cref="TryGetFootprint"/>.</summary>
    public static uint ToSetting2(int blockWidth, int blockHeight) =>
        0x7F00u | (uint)(blockWidth - 1) << 4 | (uint)(blockHeight - 1);

    public static bool IsAstc(this TxtgFormat format) => format is
        TxtgFormat.Astc4x4Srgb or TxtgFormat.Astc4x4Unorm or TxtgFormat.Astc8x5Unorm or
        TxtgFormat.Astc8x8Unorm or TxtgFormat.Astc8x8Srgb;

    public static bool IsSrgb(this TxtgFormat format) => format is
        TxtgFormat.Astc4x4Srgb or TxtgFormat.Astc8x8Srgb or
        TxtgFormat.Bc1UnormSrgb or TxtgFormat.Bc3UnormSrgb;

    /// <summary>
    /// Block-compressed formats use 16-byte blocks except BC1 and BC4, which use 8. The
    /// uncompressed formats have 1x1 blocks, so their "block" is one texel.
    /// </summary>
    public static TxtgBlockInfo BlockInfo(this TxtgFormat format) => format switch
    {
        TxtgFormat.Astc4x4Srgb or TxtgFormat.Astc4x4Unorm => new TxtgBlockInfo(4, 4, 16),
        TxtgFormat.Astc8x5Unorm => new TxtgBlockInfo(8, 5, 16),
        TxtgFormat.Astc8x8Unorm or TxtgFormat.Astc8x8Srgb => new TxtgBlockInfo(8, 8, 16),
        TxtgFormat.Bc1Unorm or TxtgFormat.Bc1UnormSrgb => new TxtgBlockInfo(4, 4, 8),
        TxtgFormat.Bc4Unorm => new TxtgBlockInfo(4, 4, 8),
        TxtgFormat.Bc3UnormSrgb or TxtgFormat.Bc5Unorm or TxtgFormat.Bc7Unorm => new TxtgBlockInfo(4, 4, 16),
        TxtgFormat.R8G8B8A8Unorm => new TxtgBlockInfo(1, 1, 4),
        TxtgFormat.R8G8Unorm => new TxtgBlockInfo(1, 1, 2),
        TxtgFormat.R8Unorm => new TxtgBlockInfo(1, 1, 1),
        _ => new TxtgBlockInfo(1, 1, 4)
    };
}
