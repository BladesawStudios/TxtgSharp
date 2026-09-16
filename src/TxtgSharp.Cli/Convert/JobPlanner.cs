namespace TxtgSharp.Cli;

internal static class JobPlanner
{
    private static readonly string[] ImageExtensions =
        [".png", ".jpg", ".jpeg", ".bmp", ".tga", ".gif", ".psd", ".dds"];

    public static bool IsImage(string path) =>
        ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static List<string> ExpandInputs(IEnumerable<string> paths, bool recursive)
    {
        SearchOption depth = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var found = new List<string>();

        foreach (string path in paths)
        {
            if (Directory.Exists(path))
            {
                found.AddRange(Directory.EnumerateFiles(path, "*", depth).Where(IsImage));
            }
            else if (path.Contains('*') || path.Contains('?'))
            {
                string directory = Path.GetDirectoryName(path) is { Length: > 0 } d ? d : ".";
                string pattern = Path.GetFileName(path);

                if (!Directory.Exists(directory))
                    throw new ConversionException($"no such directory: {directory}");

                found.AddRange(Directory.EnumerateFiles(directory, pattern, depth).Where(IsImage));
            }
            else if (File.Exists(path))
            {
                found.Add(path);
            }
            else
            {
                throw new ConversionException($"no such file: {path}");
            }
        }

        var byOutput = found
            .DistinctBy(p => Path.GetFullPath(p), StringComparer.OrdinalIgnoreCase)
            .GroupBy(Stem, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .ToList();

        if (byOutput.Count > 0)
        {
            throw new ConversionException(
                $"several inputs would produce {byOutput[0].Key}.txtg: " +
                string.Join(", ", byOutput[0]));
        }

        return found
            .DistinctBy(p => Path.GetFullPath(p), StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static List<ConversionJob> Plan(IReadOnlyList<string> inputs, Options options)
    {
        var jobs = new List<ConversionJob>(inputs.Count);

        foreach (string input in inputs)
        {
            string output = options.OutputDirectory is not null
                ? Path.Combine(options.OutputDirectory, Stem(input) + ".txtg")
                : options.OutputPath!;

            jobs.Add(new ConversionJob(input, output, TemplateFor(input, options)));
        }

        return jobs;
    }

    private static string? TemplateFor(string input, Options options)
    {
        if (options.TemplatePath is not null)
            return options.TemplatePath;

        if (options.TemplateDirectory is null)
            return null;

        string candidate = Path.Combine(options.TemplateDirectory, Stem(input) + ".txtg");
        if (File.Exists(candidate))
            return candidate;

        if (options.FormatName is not null)
            return null;

        throw new ConversionException(
            $"no template named {Stem(input)}.txtg in {options.TemplateDirectory}, and no --format to fall back on");
    }

    private static string Stem(string path) => Path.GetFileNameWithoutExtension(path);
}
