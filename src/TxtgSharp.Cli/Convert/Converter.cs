namespace TxtgSharp.Cli;

internal sealed record ConversionJob(string InputPath, string OutputPath, string? TemplatePath);

internal sealed record ConversionResult(
    ConversionJob Job, int Width, int Height, string Format, int Mips, int Copied, long Bytes,
    string? Warning = null)
{
    public string Summary =>
        $"{Width}x{Height} {Format}, {Mips} mip(s)" +
        (Copied > 0 ? $", {Copied} copied" : "") +
        $", {Bytes:N0} bytes";
}

internal sealed class ConversionException(string message) : Exception(message);

internal static class Converter
{
    public static ConversionResult Run(ConversionJob job, Options options, Action<string>? log = null)
    {
        TxtgFile? template = job.TemplatePath is null ? null : TxtgFile.FromFile(job.TemplatePath);
        TargetFormat target = ResolveTarget(options, template);
        ImageSource source = ImageSource.Load(job.InputPath);

        if (template is { LayerCount: > 1 })
        {
            if (options.Layer is null)
                throw new ConversionException(
                    $"template has {template.LayerCount} array layers; pass --layer <n> to replace one " +
                    "of them, or --format to build a fresh single-layer container instead");

            return ReplaceLayer(template, job, options, target, source);
        }

        if (options.Layer is > 0)
            throw new ConversionException(
                $"--layer {options.Layer} was given but the output has a single layer");

        if (template is not null && !options.Force &&
            (source.Width != template.Width || source.Height != template.Height))
        {
            throw new ConversionException(
                $"input is {source.Width}x{source.Height} but the template is " +
                $"{template.Width}x{template.Height}; pass --force to write the new size into the header");
        }

        bool matchesTemplate = template is not null &&
                               source.Width == template.Width && source.Height == template.Height;

        int mips = options.MipCount ?? (matchesTemplate
            ? template!.MipCount
            : MipChain.FullCount(source.Width, source.Height));

        mips = Math.Clamp(mips, 1, MipChain.FullCount(source.Width, source.Height));

        var surfaces = new List<TxtgSurfaceData>(mips);
        int copied = 0;

        for (int mip = 0; mip < mips; mip++)
        {
            int mipWidth = Math.Max(1, source.Width >> mip);
            int mipHeight = Math.Max(1, source.Height >> mip);
            int expected = SurfaceEncoder.ExpectedLength(mipWidth, mipHeight, target.Block);

            byte[]? passthrough = source.BlocksFor(target, mip);
            bool copyable = passthrough is not null && passthrough.Length >= expected;
            byte[] data;

            if (copyable)
            {
                data = passthrough!.Length == expected ? passthrough : passthrough.AsSpan(0, expected).ToArray();
                copied++;
            }
            else
            {
                data = SurfaceEncoder.Encode(
                    source.Rgba(mip, target.Srgb), mipWidth, mipHeight, target, options.Quality,
                    options.Jobs == 1);
            }

            surfaces.Add(new TxtgSurfaceData(0, mip, data));
            log?.Invoke($"  mip {mip}: {mipWidth}x{mipHeight} -> {data.Length:N0} bytes" +
                        (copyable ? " (copied)" : ""));
        }

        TxtgFile output = template is not null
            ? TxtgFile.CreateFrom(template, source.Width, source.Height, target.Format, surfaces, target.Block)
            : TxtgFile.Create(source.Width, source.Height, target.Format, surfaces, target.Block);

        string? directory = Path.GetDirectoryName(job.OutputPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        output.Save(job.OutputPath, options.CompressionLevel);

        return new ConversionResult(
            job, source.Width, source.Height, target.Name, mips, copied, new FileInfo(job.OutputPath).Length,
            AlphaWarning(source, target, copied == mips));
    }

    private static ConversionResult ReplaceLayer(
        TxtgFile template, ConversionJob job, Options options, TargetFormat target, ImageSource source)
    {
        int layer = options.Layer!.Value;
        if (layer < 0 || layer >= template.LayerCount)
            throw new ConversionException(
                $"--layer {layer} is out of range; the template has {template.LayerCount} layers (0..{template.LayerCount - 1})");

        if (source.Width != template.Width || source.Height != template.Height)
            throw new ConversionException(
                $"input is {source.Width}x{source.Height} but the array is {template.Width}x{template.Height}; " +
                "every layer of an array shares one size, so this one cannot differ");

        if (options.FormatName is not null && target.Block != template.BlockInfo)
            throw new ConversionException(
                $"--format {target.Name} does not match the array's {template.BlockInfo.Width}x" +
                $"{template.BlockInfo.Height} blocks; layers of one array share a format");

        int replaced = 0;
        foreach (TxtgSurface surface in template.Surfaces.Where(s => s.ArrayIndex == layer).OrderBy(s => s.MipLevel))
        {
            surface.Data = SurfaceEncoder.Encode(
                source.Rgba(surface.MipLevel, target.Srgb), surface.Width, surface.Height, target,
                options.Quality, options.Jobs == 1);
            replaced++;
        }

        if (replaced == 0)
            throw new ConversionException($"the template carries no surfaces for layer {layer}");

        string? directory = Path.GetDirectoryName(job.OutputPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        template.Save(job.OutputPath, options.CompressionLevel);

        return new ConversionResult(job, template.Width, template.Height, $"{target.Name} layer {layer}",
            replaced, 0, new FileInfo(job.OutputPath).Length, AlphaWarning(source, target, false));
    }

    private static string? AlphaWarning(ImageSource source, TargetFormat target, bool copiedThrough)
    {
        if (copiedThrough) return null;

        AlphaKind has = SurfaceEncoder.AlphaOf(source.Rgba(0, target.Srgb));
        AlphaKind holds = SurfaceEncoder.AlphaCapacityOf(target);
        if (has <= holds) return null;

        return has == AlphaKind.Smooth && holds == AlphaKind.Binary
            ? $"source has soft alpha but {target.Name} stores one bit per texel, so edges become hard"
            : $"source has alpha but {target.Name} stores none, so it is dropped";
    }

    public static TargetFormat ResolveTarget(Options options, TxtgFile? template)
    {
        if (options.FormatName is not null)
        {
            if (!TargetFormat.TryParse(options.FormatName, out TargetFormat parsed))
                throw new ConversionException(
                    $"unknown format '{options.FormatName}'; run --list-formats to see them all");

            return parsed;
        }

        if (template is not null)
            return TargetFormat.FromContainer(template);

        throw new ConversionException("need --format or a template to know what to encode to");
    }
}
