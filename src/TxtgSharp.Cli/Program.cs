using AstcSharp;
using AstcSharp.Core;
using TxtgSharp;
using TxtgSharp.Cli;

if (args.Length == 0)
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  TxtgSharp.Cli info <file.txtg> [more.txtg ...]");
    Console.WriteLine("  TxtgSharp.Cli dump <file.txtg> <out_dir> [layer ...]   (mip 0, defaults to layer 0)");
    Console.WriteLine("  TxtgSharp.Cli replace <file.txtg> <image.png> <out.txtg> [layer]   (ASTC only)");
    Console.WriteLine("  TxtgSharp.Cli roundtrip <file.txtg> [more.txtg ...]   (read, write, compare)");
    return 1;
}

return args[0].ToLowerInvariant() switch
{
    "dump" => Dump(args.Skip(1).ToArray()),
    "info" => Info(args.Skip(1).ToArray()),
    "replace" => Replace(args.Skip(1).ToArray()),
    "roundtrip" => RoundTrip(args.Skip(1).ToArray()),
    _ => Info(args)          // bare file paths keep working
};

static int Info(string[] files)
{
    int failures = 0;

    foreach (string path in files)
    {
        Console.WriteLine($"=== {Path.GetFileName(path)} ===");
        try
        {
            TxtgFile txtg = TxtgFile.FromFile(path);
            TxtgBlockInfo block = txtg.BlockInfo;

            Console.WriteLine($"  {txtg.Width}x{txtg.Height}, {txtg.LayerCount} layers, {txtg.MipCount} mips");
            Console.WriteLine($"  format {txtg.Format} (0x{txtg.RawFormatCode:X4}), " +
                              $"block {block.Width}x{block.Height}, {block.BytesPerBlock} bytes");
            Console.WriteLine($"  surfaces parsed: {txtg.Surfaces.Count} of {txtg.LayerCount * txtg.MipCount}");

            int checkedCount = 0, mismatched = 0;
            foreach (TxtgSurface s in txtg.LayersOfMip(0))
            {
                if (s.Data.Length != s.DataLength) mismatched++;
                checkedCount++;
            }

            TxtgSurface? first = txtg.Surfaces.FirstOrDefault();
            if (first is not null)
            {
                Console.WriteLine($"  mip0 layer0: {first.Width}x{first.Height}, " +
                                  $"swizzled {first.SwizzledSize:N0} -> linear {first.Data.Length:N0} bytes");
            }

            Console.WriteLine($"  mip0 layers checked: {checkedCount}, size mismatches: {mismatched}");
            if (mismatched > 0) failures++;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAILED: {ex.Message}");
            failures++;
        }
    }

    return failures == 0 ? 0 : 2;
}

static int Dump(string[] args)
{
    if (args.Length < 2)
    {
        Console.WriteLine("Usage: TxtgSharp.Cli dump <file.txtg> <out_dir> [layer ...]");
        return 1;
    }

    string path = args[0];
    string outDir = args[1];
    Directory.CreateDirectory(outDir);

    int[] layers = args.Length > 2
        ? args.Skip(2).Select(int.Parse).ToArray()
        : [0];

    TxtgFile txtg = TxtgFile.FromFile(path);
    if (!txtg.Format.IsAstc())
    {
        Console.WriteLine($"{txtg.Format} is not ASTC; only ASTC decoding is wired up here.");
        return 1;
    }

    Footprint footprint = FootprintOf(txtg.BlockInfo);
    LdrDecodeMode mode = txtg.Format.IsSrgb() ? LdrDecodeMode.Srgb : LdrDecodeMode.Linear;
    string stem = Path.GetFileNameWithoutExtension(path);

    Console.WriteLine($"{stem}: {txtg.Format}, decoding mip 0 of {layers.Length} layer(s)");

    foreach (int layer in layers)
    {
        TxtgSurface? surface = txtg.Surface(layer, 0);
        if (surface is null)
        {
            Console.WriteLine($"  layer {layer}: not present");
            continue;
        }

        byte[] rgba = DecodeAstc(surface.Data, surface.Width, surface.Height, footprint, mode);
        string outPath = Path.Combine(outDir, $"{stem}_layer{layer:D3}.png");
        PngWriter.WriteRgba(outPath, rgba, surface.Width, surface.Height);

        Console.WriteLine($"  layer {layer}: {surface.Width}x{surface.Height}, " +
                          $"{rgba.Length:N0} bytes rgba -> {Path.GetFileName(outPath)}");
        Console.WriteLine("    " + ChannelStats(rgba));
    }

    return 0;
}

// Encodes a PNG into an existing container, which keeps every header field the game reads and
// this library cannot derive. The whole mip chain of the layer is rebuilt, so the texture stays
// consistent at distance rather than only at mip 0.
static int Replace(string[] args)
{
    if (args.Length < 3)
    {
        Console.WriteLine("Usage: TxtgSharp.Cli replace <file.txtg> <image.png> <out.txtg> [layer]");
        return 1;
    }

    string path = args[0], imagePath = args[1], outPath = args[2];
    int layer = args.Length > 3 ? int.Parse(args[3]) : 0;

    TxtgFile txtg = TxtgFile.FromFile(path);
    if (!txtg.Format.IsAstc())
    {
        Console.WriteLine($"{txtg.Format} is not ASTC; only ASTC encoding is wired up here. " +
                          "Set TxtgSurface.Data directly to inject pre-encoded blocks.");
        return 1;
    }

    var (rgba, width, height) = PngReader.ReadRgba(imagePath);
    if (width != txtg.Width || height != txtg.Height)
    {
        Console.WriteLine($"{Path.GetFileName(imagePath)} is {width}x{height}, " +
                          $"container is {txtg.Width}x{txtg.Height}. Resize it first.");
        return 1;
    }

    Footprint footprint = FootprintOf(txtg.BlockInfo);
    Console.WriteLine($"{Path.GetFileName(path)}: encoding {width}x{height} as {txtg.Format} " +
                      $"({footprint.Width}x{footprint.Height} blocks) into layer {layer}");

    int replaced = 0;
    for (int mip = 0; mip < txtg.MipCount; mip++)
    {
        TxtgSurface? surface = txtg.Surface(layer, mip);
        if (surface is null) continue;

        byte[] level = mip == 0 ? rgba : Downsample(rgba, width, height, mip);
        byte[] blocks = EncodeAstc(level, surface.Width, surface.Height, footprint);

        // The encoder pads out to whole blocks, which is also what the container stores.
        if (blocks.Length != surface.DataLength)
            throw new InvalidDataException(
                $"Encoder produced {blocks.Length} bytes for mip {mip}, container wants {surface.DataLength}.");

        surface.Data = blocks;
        replaced++;
        Console.WriteLine($"  mip {mip}: {surface.Width}x{surface.Height} -> {blocks.Length:N0} bytes");
    }

    txtg.Save(outPath);
    Console.WriteLine($"  {replaced} surface(s) replaced -> {Path.GetFileName(outPath)} " +
                      $"({new FileInfo(outPath).Length:N0} bytes)");
    return 0;
}

// Writing back a container nobody touched has to reproduce it exactly; anything else means the
// writer is losing something the game might read. With --reswizzle every surface is marked
// modified first, which drops the verbatim-payload shortcut and puts the swizzler itself under
// test: deswizzle then swizzle has to land on the bytes the container shipped.
static int RoundTrip(string[] args)
{
    bool reswizzle = args.Contains("--reswizzle");
    string[] files = args.Where(a => !a.StartsWith("--")).ToArray();
    int failures = 0;

    foreach (string path in files)
    {
        try
        {
            byte[] original = File.ReadAllBytes(path);
            TxtgFile txtg = TxtgFile.FromBytes(original);

            if (reswizzle)
            {
                int differing = 0;
                foreach (TxtgSurface surface in txtg.Surfaces)
                {
                    byte[] before = surface.Swizzled();
                    surface.Data = surface.Data;
                    if (!surface.Swizzled().AsSpan().SequenceEqual(before)) differing++;
                }

                if (differing > 0)
                {
                    Console.WriteLine($"{Path.GetFileName(path)}: {differing} of {txtg.Surfaces.Count} " +
                                      "surface(s) did not re-swizzle to the original bytes");
                    failures++;
                }

                continue;
            }

            byte[] written = txtg.ToBytes();
            if (written.AsSpan().SequenceEqual(original))
                continue;

            int at = 0;
            while (at < Math.Min(original.Length, written.Length) && original[at] == written[at]) at++;
            Console.WriteLine($"{Path.GetFileName(path)}: DIFFERS at 0x{at:X} " +
                              $"({original.Length:N0} -> {written.Length:N0} bytes)");
            failures++;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{Path.GetFileName(path)}: FAILED: {ex.Message}");
            failures++;
        }
    }

    Console.WriteLine($"{files.Length - failures} of {files.Length} reproduced byte for byte.");
    return failures == 0 ? 0 : 2;
}

static byte[] DecodeAstc(byte[] blocks, int width, int height, Footprint footprint, LdrDecodeMode mode)
{
    using MemoryStream source = new(blocks);
    using MemoryStream destination = new();
    AstcDecoder.DecompressImage(source, destination, width, height, footprint, mode);
    return destination.ToArray();
}

static byte[] EncodeAstc(byte[] rgba, int width, int height, Footprint footprint)
{
    using MemoryStream source = new(rgba);
    using MemoryStream destination = new();
    AstcEncoder.CompressImage(source, destination, width, height, footprint);
    return destination.ToArray();
}

static Footprint FootprintOf(TxtgBlockInfo block) => (block.Width, block.Height) switch
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

// Box filter, halving per level. Averaging happens in whatever space the texture stores, which
// is close enough for a mip tail and keeps a normal or roughness map out of a gamma curve it
// was never in.
static byte[] Downsample(byte[] rgba, int width, int height, int levels)
{
    byte[] current = rgba;
    int currentWidth = width, currentHeight = height;

    for (int level = 0; level < levels; level++)
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

                for (int channel = 0; channel < 4; channel++)
                {
                    next[destination + channel] = (byte)((current[a + channel] + current[b + channel] +
                                                          current[c + channel] + current[d + channel] + 2) / 4);
                }
            }
        }

        current = next;
        currentWidth = nextWidth;
        currentHeight = nextHeight;
    }

    return current;
}

// Per-channel statistics say what a texture actually holds without trusting any shader:
// a two-channel tangent normal sits near 0.5 in R and G with x^2+y^2 <= 1, whereas an
// occlusion or roughness channel has its own distribution entirely.
static string ChannelStats(byte[] rgba)
{
    double[] sum = new double[4];
    byte[] min = [255, 255, 255, 255];
    byte[] max = [0, 0, 0, 0];
    long pixels = rgba.Length / 4;
    long inUnitDisc = 0;

    for (long i = 0; i < pixels; i++)
    {
        long o = i * 4;
        for (int c = 0; c < 4; c++)
        {
            byte v = rgba[o + c];
            sum[c] += v;
            if (v < min[c]) min[c] = v;
            if (v > max[c]) max[c] = v;
        }

        double x = rgba[o] / 255.0 * 2.0 - 1.0;
        double y = rgba[o + 1] / 255.0 * 2.0 - 1.0;
        if (x * x + y * y <= 1.0) inUnitDisc++;
    }

    string[] names = ["R", "G", "B", "A"];
    string stats = string.Join("  ", names.Select((n, c) =>
        $"{n} mean {sum[c] / pixels / 255.0:F3} [{min[c] / 255.0:F2}-{max[c] / 255.0:F2}]"));

    return $"{stats}  |  (2R-1)^2+(2G-1)^2<=1 for {100.0 * inUnitDisc / pixels:F1}% of texels";
}
