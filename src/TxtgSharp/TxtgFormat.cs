namespace TxtgSharp;
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

    R8Unorm,

    R8G8Unorm,

    R8G8B8A8Unorm
}
public readonly record struct TxtgBlockInfo(int Width, int Height, int BytesPerBlock);

public static class TxtgFormats
{
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

        0xA0A => TxtgFormat.R8G8B8A8Unorm,
        0xB0B => TxtgFormat.R8G8Unorm,
        0xC0C => TxtgFormat.R8Unorm,

        // A handful of files carry a low byte nothing else uses - 0x305, 0x605, 0x709 and a
        // few more, eighty-odd files across the whole of TexToGo. The family in the high byte
        // is what decides how the blocks are laid out, and only the colour space is in doubt,
        // so a block-compressed family is still readable. ASTC is not: its block size is in
        // the low byte too, and guessing it wrong decodes to noise.
        _ => (code >> 8) switch
        {
            2 or 3 => TxtgFormat.Bc1Unorm,
            5 => TxtgFormat.Bc3UnormSrgb,
            6 => TxtgFormat.Bc4Unorm,
            7 => TxtgFormat.Bc5Unorm,
            9 => TxtgFormat.Bc7Unorm,
            _ => TxtgFormat.Unknown
        }
    };

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

    public static bool TryGetFootprint(uint setting2, out int width, out int height)
    {
        width = ((int)(setting2 >> 4) & 0xF) + 1;
        height = ((int)setting2 & 0xF) + 1;
        return (setting2 & 0xFFFFFF00) == 0x7F00;
    }

    public static uint ToSetting2(int blockWidth, int blockHeight) =>
        0x7F00u | (uint)(blockWidth - 1) << 4 | (uint)(blockHeight - 1);

    public static bool IsAstc(this TxtgFormat format) => format is
        TxtgFormat.Astc4x4Srgb or TxtgFormat.Astc4x4Unorm or TxtgFormat.Astc8x5Unorm or
        TxtgFormat.Astc8x8Unorm or TxtgFormat.Astc8x8Srgb;

    public static bool IsSrgb(this TxtgFormat format) => format is
        TxtgFormat.Astc4x4Srgb or TxtgFormat.Astc8x8Srgb or
        TxtgFormat.Bc1UnormSrgb or TxtgFormat.Bc3UnormSrgb;

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
