namespace TxtgSharp.Cli;

internal static class MipChain
{
    public static int FullCount(int width, int height)
    {
        int levels = 1;
        while (width > 1 || height > 1)
        {
            width = Math.Max(1, width >> 1);
            height = Math.Max(1, height >> 1);
            levels++;
        }

        return levels;
    }

    public static byte[][] Build(byte[] rgba, int width, int height, int levels, bool srgb)
    {
        var chain = new byte[levels][];
        chain[0] = rgba;

        byte[] current = rgba;
        int currentWidth = width, currentHeight = height;

        for (int level = 1; level < levels; level++)
        {
            int nextWidth = Math.Max(1, currentWidth >> 1);
            int nextHeight = Math.Max(1, currentHeight >> 1);
            byte[] next = new byte[nextWidth * nextHeight * 4];

            for (int y = 0; y < nextHeight; y++)
            {
                int y0 = Math.Min(y * 2, currentHeight - 1);
                int y1 = Math.Min(y0 + 1, currentHeight - 1);

                for (int x = 0; x < nextWidth; x++)
                {
                    int x0 = Math.Min(x * 2, currentWidth - 1);
                    int x1 = Math.Min(x0 + 1, currentWidth - 1);

                    int a = (y0 * currentWidth + x0) * 4, b = (y0 * currentWidth + x1) * 4;
                    int c = (y1 * currentWidth + x0) * 4, d = (y1 * currentWidth + x1) * 4;
                    int destination = (y * nextWidth + x) * 4;

                    for (int channel = 0; channel < 3; channel++)
                    {
                        next[destination + channel] = srgb
                            ? LinearToSrgb((SrgbToLinear(current[a + channel]) + SrgbToLinear(current[b + channel]) +
                                            SrgbToLinear(current[c + channel]) + SrgbToLinear(current[d + channel])) * 0.25)
                            : (byte)((current[a + channel] + current[b + channel] +
                                      current[c + channel] + current[d + channel] + 2) / 4);
                    }

                    next[destination + 3] = (byte)((current[a + 3] + current[b + 3] +
                                                    current[c + 3] + current[d + 3] + 2) / 4);
                }
            }

            chain[level] = next;
            current = next;
            currentWidth = nextWidth;
            currentHeight = nextHeight;
        }

        return chain;
    }

    private static readonly double[] SrgbTable = BuildSrgbTable();

    private static double[] BuildSrgbTable()
    {
        double[] table = new double[256];
        for (int i = 0; i < 256; i++)
        {
            double v = i / 255.0;
            table[i] = v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        return table;
    }

    private static double SrgbToLinear(byte value) => SrgbTable[value];

    private static byte LinearToSrgb(double value)
    {
        double encoded = value <= 0.0031308 ? value * 12.92 : 1.055 * Math.Pow(value, 1.0 / 2.4) - 0.055;
        return (byte)Math.Clamp(Math.Round(encoded * 255.0), 0, 255);
    }
}
