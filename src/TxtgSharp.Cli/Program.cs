using AstcSharp;
using AstcSharp.Core;
using TxtgSharp;
using TxtgSharp.Cli;

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Usage();
    return args.Length == 0 ? 1 : 0;
}

try
{
    return args[0].ToLowerInvariant() switch
    {
        "convert" => ConvertCommand.Run(args.Skip(1).ToArray()),
        "dump" => Dump(args.Skip(1).ToArray()),
        "info" => Info(args.Skip(1).ToArray()),
        "roundtrip" => RoundTrip(args.Skip(1).ToArray()),
        _ => Info(args)
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}

static void Usage()
{
    Console.WriteLine("""
        TxtgSharp CLI - read, inspect and write TexToGo (.txtg) containers.

        Usage:
          txtg-cli info <file.txtg> [more.txtg ...]
          txtg-cli dump <file.txtg> <out_dir> [layer ...]   decode ASTC mip 0 to PNG
          txtg-cli convert <image> <out.txtg> [options]     encode an image into a container
          txtg-cli convert <images...> --out <dir> [...]    a batch, each named after its source
          txtg-cli roundtrip <file.txtg> [...] [--reswizzle]

        Run 'txtg-cli convert --help' for the conversion options.
        """);
}

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
            Console.WriteLine($"  format {txtg.FormatName} (0x{txtg.RawFormatCode:X4}, {txtg.Format}), " +
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

    Console.WriteLine($"{stem}: {txtg.FormatName}, decoding mip 0 of {layers.Length} layer(s)");

    foreach (int layer in layers)
    {
        TxtgSurface? surface = txtg.Surface(layer, 0);
        if (surface is null)
        {
            Console.WriteLine($"  layer {layer}: not present");
            continue;
        }

        using MemoryStream source = new(surface.Data);
        using MemoryStream destination = new();
        AstcDecoder.DecompressImage(source, destination, surface.Width, surface.Height, footprint, mode);

        byte[] rgba = destination.ToArray();
        string outPath = Path.Combine(outDir, $"{stem}_layer{layer:D3}.png");
        PngWriter.WriteRgba(outPath, rgba, surface.Width, surface.Height);

        Console.WriteLine($"  layer {layer}: {surface.Width}x{surface.Height}, " +
                          $"{rgba.Length:N0} bytes rgba -> {Path.GetFileName(outPath)}");
        Console.WriteLine("    " + ChannelStats(rgba));
    }

    return 0;
}

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
