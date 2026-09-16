using BCnEncoder.Encoder;

namespace TxtgSharp.Cli;

internal sealed record Options(
    IReadOnlyList<string> InputPaths,
    string? OutputPath,
    string? OutputDirectory,
    string? TemplatePath,
    string? TemplateDirectory,
    string? FormatName,
    int? MipCount,
    int? Layer,
    CompressionQuality Quality,
    int CompressionLevel,
    bool Force,
    bool Recursive,
    int Jobs)
{
    public static Options Parse(string[] args)
    {
        var positional = new List<string>();
        string? outDir = null, template = null, templateDir = null, format = null;
        int? mips = null;
        int? layer = null;
        CompressionQuality quality = CompressionQuality.Balanced;
        int level = 12;
        int jobs = Environment.ProcessorCount;
        bool force = false, recursive = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out" or "-o": outDir = Next(args, ref i); break;
                case "--template": template = Next(args, ref i); break;
                case "--template-dir": templateDir = Next(args, ref i); break;
                case "--format": format = Next(args, ref i); break;
                case "--mips": mips = ParseInt(Next(args, ref i), "--mips"); break;
                case "--layer": layer = ParseInt(Next(args, ref i), "--layer"); break;
                case "--level": level = ParseInt(Next(args, ref i), "--level"); break;
                case "--jobs" or "-j": jobs = Math.Max(1, ParseInt(Next(args, ref i), "--jobs")); break;
                case "--recursive" or "-r": recursive = true; break;
                case "--force": force = true; break;
                case "--quality":
                    string name = Next(args, ref i);
                    quality = name.ToLowerInvariant() switch
                    {
                        "fast" => CompressionQuality.Fast,
                        "balanced" => CompressionQuality.Balanced,
                        "best" => CompressionQuality.BestQuality,
                        _ => throw new ConversionException($"unknown quality '{name}'; use fast, balanced or best")
                    };
                    break;

                default:
                    if (args[i].StartsWith('-'))
                        throw new ConversionException($"unknown option '{args[i]}'");
                    positional.Add(args[i]);
                    break;
            }
        }

        if (positional.Count == 0)
            throw new ConversionException("need at least one input path");

        string? outPath = null;
        if (outDir is null)
        {
            if (positional.Count < 2)
                throw new ConversionException(
                    "need an output: either a second path, or --out <dir> to name each output after its source");

            outPath = positional[^1];
            positional.RemoveAt(positional.Count - 1);

            if (!outPath.EndsWith(".txtg", StringComparison.OrdinalIgnoreCase))
                throw new ConversionException(
                    $"'{outPath}' is not a .txtg path; use --out <dir> for a batch");
        }

        if (template is not null && templateDir is not null)
            throw new ConversionException("--template and --template-dir are alternatives; pick one");

        if (template is not null && !File.Exists(template))
            throw new ConversionException($"no such template: {template}");

        if (templateDir is not null && !Directory.Exists(templateDir))
            throw new ConversionException($"no such template directory: {templateDir}");

        if (template is null && templateDir is null && format is null)
            throw new ConversionException("need --format, --template or --template-dir");

        return new Options(positional, outPath, outDir, template, templateDir, format,
                           mips, layer, quality, level, force, recursive, jobs);
    }

    private static string Next(string[] args, ref int i)
    {
        if (++i >= args.Length) throw new ConversionException($"{args[i - 1]} needs a value");
        return args[i];
    }

    private static int ParseInt(string value, string option) =>
        int.TryParse(value, out int parsed)
            ? parsed
            : throw new ConversionException($"{option} needs a number, got '{value}'");
}
