using AstcSharp;
using AstcSharp.Core;
using TxtgSharp;
using TxtgSharp.Cli;

if (args.Length == 0)
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  TxtgSharp.Cli info <file.txtg> [more.txtg ...]");
    Console.WriteLine("  TxtgSharp.Cli dump <file.txtg> <out_dir> [layer ...]   (mip 0, defaults to layer 0)");
    return 1;
}

return args[0].ToLowerInvariant() switch
{
    "dump" => Dump(args.Skip(1).ToArray()),
    "info" => Info(args.Skip(1).ToArray()),
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
            TxtgBlockInfo block = txtg.Format.BlockInfo();

            Console.WriteLine($"  {txtg.Width}x{txtg.Height}, {txtg.LayerCount} layers, {txtg.MipCount} mips");
            Console.WriteLine($"  format {txtg.Format} (0x{txtg.RawFormatCode:X4}), " +
                              $"block {block.Width}x{block.Height}, {block.BytesPerBlock} bytes");
            Console.WriteLine($"  surfaces parsed: {txtg.Surfaces.Count} of {txtg.LayerCount * txtg.MipCount}");

            int checkedCount = 0, mismatched = 0;
            foreach (TxtgSurface s in txtg.LayersOfMip(0))
            {
                int wb = (s.Width + block.Width - 1) / block.Width;
                int hb = (s.Height + block.Height - 1) / block.Height;
                if (s.Data.Length != wb * hb * block.BytesPerBlock) mismatched++;
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

    Footprint footprint = Footprint.FromFootprintType(FootprintOf(txtg.Format));
    LdrDecodeMode mode = txtg.Format.IsSrgb() ? LdrDecodeMode.Srgb : LdrDecodeMode.Linear;
    string stem = Path.GetFileNameWithoutExtension(path);

    Console.WriteLine($"{stem}: {txtg.Format}, decoding mip 0 of {layers.Length} layer(s)");

    foreach (int layer in layers)
    {
        TxtgSurface? surface = txtg.LayersOfMip(0).FirstOrDefault(s => s.ArrayIndex == layer);
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
    }

    return 0;
}

static FootprintType FootprintOf(TxtgFormat format) => format switch
{
    TxtgFormat.Astc4x4Srgb => FootprintType.Footprint4x4,
    TxtgFormat.Astc8x5Unorm => FootprintType.Footprint8x5,
    TxtgFormat.Astc8x8Unorm or TxtgFormat.Astc8x8Srgb => FootprintType.Footprint8x8,
    _ => throw new NotSupportedException($"No ASTC footprint for {format}.")
};
