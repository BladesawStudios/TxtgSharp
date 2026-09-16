using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace TxtgSharp;

internal static class TxtgSwizzle
{
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

            int lastRow = HeightInBlocks - 1;
            int lastColumnBytes = RowBytes - (GobsPerRow - 1) * 64;
            int lastSector = DivRoundUp(lastColumnBytes, 16) - 1;

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

        public int LinearSize { get; }

        public int SwizzledSize { get; }
    }

    internal static int LinearSize(int width, int height, TxtgBlockInfo block) =>
        new Geometry(width, height, block).LinearSize;

    internal static int SwizzledSize(int width, int height, TxtgBlockInfo block) =>
        new Geometry(width, height, block).SwizzledSize;

    internal static byte[] Deswizzle(byte[] swizzled, int width, int height, TxtgBlockInfo block)
    {
        Geometry geometry = new(width, height, block);
        byte[] linear = new byte[geometry.LinearSize];
        Transfer(swizzled, linear, geometry, toLinear: true);
        return linear;
    }

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
