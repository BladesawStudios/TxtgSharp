using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace TxtgSharp;

/// <summary>
/// Tegra block-linear layout, both directions. Texels are grouped into 64x8-byte GOBs;
/// a "block" stacks <c>blockHeight</c> GOBs vertically, and blocks run in column-major
/// order across the image.
/// </summary>
/// <remarks>
/// Both directions walk the same addresses, so the arithmetic lives in one place and the only
/// difference is which side of the copy is the source. The walk goes one GOB at a time: a GOB
/// is 512 contiguous swizzled bytes spanning 8 rows, so this order keeps reads local instead of
/// striding the whole surface once per row. Within a GOB row the transfer unit is a 16-byte
/// sector, because every term of the swizzled address either selects a sector or is
/// <c>xBytes % 16</c> - so an aligned 16-byte run of row bytes is contiguous on both sides
/// whatever the format's block size.
/// </remarks>
internal static class TxtgSwizzle
{
    /// <summary>
    /// Derived layout for one surface. <see cref="SwizzledSize"/> is the size TotK actually
    /// stores, which is not the padded surface size: retail payloads stop at the last byte the
    /// image occupies, dropping the padding tail of the final GOB block row.
    /// </summary>
    internal readonly struct Geometry
    {
        public Geometry(int width, int height, TxtgBlockInfo block)
        {
            WidthInBlocks = Math.Max(1, DivRoundUp(width, block.Width));
            HeightInBlocks = Math.Max(1, DivRoundUp(height, block.Height));
            RowBytes = WidthInBlocks * block.BytesPerBlock;

            BlockHeight = Math.Clamp(PowerOfTwoAtLeast(DivRoundUp(HeightInBlocks, 8)), 1, 16);
            BlockRows = 8 * BlockHeight;
            GobsPerRow = DivRoundUp(RowBytes, 64);
            GobColumnBytes = 512 * BlockHeight;
            BlockRowBytes = GobColumnBytes * GobsPerRow;

            LinearSize = RowBytes * HeightInBlocks;

            // The last byte the image occupies. Both the row term and the sector term of the
            // swizzled address rise monotonically, so the maximum sits at the last row of the
            // last GOB of the last column - no need to walk the surface to find it.
            int lastRow = HeightInBlocks - 1;
            int lastColumnBytes = RowBytes - (GobsPerRow - 1) * 64;      // 1..64
            int lastSector = DivRoundUp(lastColumnBytes, 16) - 1;        // 0..3

            SwizzledSize =
                lastRow / BlockRows * BlockRowBytes
                + (GobsPerRow - 1) * GobColumnBytes
                + lastRow % BlockRows / 8 * 512
                + (lastRow % 8 >> 1) * 64 + (lastRow % 8 & 1) * 16
                + (lastSector >> 1) * 256 + (lastSector & 1) * 32
                + Math.Min(16, lastColumnBytes - lastSector * 16);
        }

        public int WidthInBlocks { get; }
        public int HeightInBlocks { get; }
        public int RowBytes { get; }
        public int BlockHeight { get; }
        public int BlockRows { get; }
        public int GobsPerRow { get; }
        public int GobColumnBytes { get; }
        public int BlockRowBytes { get; }

        /// <summary>Deswizzled size: one tightly packed row of blocks per block row.</summary>
        public int LinearSize { get; }

        /// <summary>Swizzled size as TotK stores it, trimmed to the last occupied byte.</summary>
        public int SwizzledSize { get; }
    }

    /// <summary>Linear size of one surface - the length <see cref="TxtgSurface.Data"/> must have.</summary>
    internal static int LinearSize(int width, int height, TxtgBlockInfo block) =>
        new Geometry(width, height, block).LinearSize;

    /// <summary>Swizzled size of one surface, matching what retail containers store.</summary>
    internal static int SwizzledSize(int width, int height, TxtgBlockInfo block) =>
        new Geometry(width, height, block).SwizzledSize;

    /// <summary>Converts block-linear to linear.</summary>
    internal static byte[] Deswizzle(byte[] swizzled, int width, int height, TxtgBlockInfo block)
    {
        Geometry geometry = new(width, height, block);
        byte[] linear = new byte[geometry.LinearSize];
        Transfer(swizzled, linear, geometry, toLinear: true);
        return linear;
    }

    /// <summary>
    /// Converts linear to block-linear. <paramref name="swizzledSize"/> overrides the computed
    /// size, which is how a repack keeps a surface exactly as long as the one it replaces, and
    /// <paramref name="seed"/> supplies the bytes the walk does not write - the padding rows a
    /// surface whose height does not fill its last block row leaves behind.
    /// </summary>
    internal static byte[] Swizzle(
        byte[] linear, int width, int height, TxtgBlockInfo block, int swizzledSize = 0, byte[]? seed = null)
    {
        Geometry geometry = new(width, height, block);
        if (linear.Length < geometry.LinearSize)
            throw new ArgumentException(
                $"Need {geometry.LinearSize} bytes for a {width}x{height} surface, got {linear.Length}.",
                nameof(linear));

        byte[] swizzled = new byte[swizzledSize > 0 ? swizzledSize : geometry.SwizzledSize];
        if (seed is not null)
            seed.AsSpan(0, Math.Min(seed.Length, swizzled.Length)).CopyTo(swizzled);

        Transfer(swizzled, linear, geometry, toLinear: false);
        return swizzled;
    }

    private static void Transfer(byte[] swizzled, byte[] linear, in Geometry geometry, bool toLinear)
    {
        ref byte swizzledBase = ref MemoryMarshal.GetReference(swizzled.AsSpan());
        ref byte linearBase = ref MemoryMarshal.GetReference(linear.AsSpan());

        for (int gobY = 0; gobY < geometry.HeightInBlocks; gobY += 8)
        {
            int gobRowBase = gobY / geometry.BlockRows * geometry.BlockRowBytes
                             + gobY % geometry.BlockRows / 8 * 512;

            for (int gobX = 0; gobX < geometry.GobsPerRow; gobX++)
            {
                int gobBase = gobRowBase + gobX * geometry.GobColumnBytes;
                int gobXBytes = gobX * 64;

                int rows = Math.Min(8, geometry.HeightInBlocks - gobY);
                for (int y = 0; y < rows; y++)
                {
                    int rowBase = gobBase + (y >> 1) * 64 + (y & 1) * 16;
                    int linearRow = (gobY + y) * geometry.RowBytes;

                    for (int sector = 0; sector < 4; sector++)
                    {
                        int xBytes = gobXBytes + sector * 16;
                        if (xBytes >= geometry.RowBytes) break;

                        int swizzledOffset = rowBase + (sector >> 1) * 256 + (sector & 1) * 32;
                        int linearOffset = linearRow + xBytes;

                        // Clamped rather than asserted: a container is free to trim its payload
                        // even shorter than SwizzledSize, and what sits past the end is padding
                        // the image never reads.
                        int length = Math.Min(16, geometry.RowBytes - xBytes);
                        length = Math.Min(length, swizzled.Length - swizzledOffset);
                        if (length <= 0) continue;

                        if (length == 16)
                        {
                            ref byte source = ref Unsafe.Add(
                                ref toLinear ? ref swizzledBase : ref linearBase,
                                (nint)(toLinear ? swizzledOffset : linearOffset));
                            ref byte destination = ref Unsafe.Add(
                                ref toLinear ? ref linearBase : ref swizzledBase,
                                (nint)(toLinear ? linearOffset : swizzledOffset));
                            Unsafe.WriteUnaligned(ref destination, Unsafe.ReadUnaligned<Vector128<byte>>(ref source));
                        }
                        else if (toLinear)
                            Buffer.BlockCopy(swizzled, swizzledOffset, linear, linearOffset, length);
                        else
                            Buffer.BlockCopy(linear, linearOffset, swizzled, swizzledOffset, length);
                    }
                }
            }
        }
    }

    private static int DivRoundUp(int value, int divisor) => (value + divisor - 1) / divisor;

    private static int PowerOfTwoAtLeast(int value)
    {
        int result = 1;
        while (result < value) result <<= 1;
        return result;
    }
}
