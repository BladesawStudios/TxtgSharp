using System.Collections.Concurrent;
using System.Diagnostics;

namespace TxtgSharp.Cli;

internal static class ConvertCommand
{
    public static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Usage();
            return args.Length == 0 ? 1 : 0;
        }

        if (args[0] == "--list-formats")
        {
            Console.WriteLine(string.Join(Environment.NewLine, TargetFormat.Names));
            return 0;
        }

        Options options = Options.Parse(args);
        List<string> inputs = JobPlanner.ExpandInputs(options.InputPaths, options.Recursive);

        if (inputs.Count == 0)
        {
            Console.Error.WriteLine("error: nothing to convert.");
            return 1;
        }

        if (options.OutputPath is not null && inputs.Count > 1)
            throw new ConversionException(
                $"{inputs.Count} inputs but a single output file. Use --out <dir> to name each " +
                "output after its source.");

        List<ConversionJob> jobs = JobPlanner.Plan(inputs, options);
        return jobs.Count == 1 && options.OutputPath is not null ? Single(jobs[0], options) : Batch(jobs, options);
    }

    private static int Single(ConversionJob job, Options options)
    {
        if (job.TemplatePath is not null)
        {
            TxtgFile template = TxtgFile.FromFile(job.TemplatePath);
            Console.WriteLine($"template {Path.GetFileName(job.TemplatePath)}: " +
                              $"{template.Width}x{template.Height}, {template.LayerCount} layers, " +
                              $"{template.MipCount} mips, {template.FormatName}");
        }

        ConversionResult result = Converter.Run(job, options, Console.WriteLine);

        Console.WriteLine($"wrote {Path.GetFileName(job.OutputPath)} ({result.Summary})");
        if (result.Warning is not null)
            Console.WriteLine($"warning: {result.Warning}");

        WarnAboutMissingTemplate(job.TemplatePath is null);
        return 0;
    }

    private static int Batch(List<ConversionJob> jobs, Options options)
    {
        Console.WriteLine($"converting {jobs.Count} file(s) with {options.Jobs} worker(s)");

        var failures = new ConcurrentBag<(string Input, string Message)>();
        var done = new ConcurrentQueue<ConversionResult>();
        int completed = 0;
        object consoleLock = new();
        Stopwatch clock = Stopwatch.StartNew();

        Parallel.ForEach(jobs, new ParallelOptions { MaxDegreeOfParallelism = options.Jobs }, job =>
        {
            string line;
            try
            {
                ConversionResult result = Converter.Run(job, options);
                done.Enqueue(result);
                line = $"-> {Path.GetFileName(job.OutputPath)}  ({result.Summary})" +
                       (result.Warning is null ? "" : $"  [{result.Warning}]");
            }
            catch (Exception ex)
            {
                failures.Add((job.InputPath, ex.Message));
                line = $" FAILED: {ex.Message}";
            }

            int index = Interlocked.Increment(ref completed);
            lock (consoleLock)
                Console.WriteLine($"[{index,4}/{jobs.Count}] {Path.GetFileName(job.InputPath)} {line}");
        });

        clock.Stop();

        long bytes = done.Sum(r => r.Bytes);
        int copied = done.Sum(r => r.Copied);

        Console.WriteLine();
        Console.WriteLine($"{done.Count} of {jobs.Count} converted in {clock.Elapsed.TotalSeconds:F1}s " +
                          $"({bytes:N0} bytes written" + (copied > 0 ? $", {copied} surface(s) copied through)" : ")"));

        if (!failures.IsEmpty)
        {
            Console.WriteLine();
            Console.WriteLine($"{failures.Count} failed:");
            foreach ((string input, string message) in failures.OrderBy(f => f.Input, StringComparer.OrdinalIgnoreCase))
                Console.WriteLine($"  {Path.GetFileName(input)}: {message}");
        }

        var warned = done.Where(r => r.Warning is not null).ToList();
        if (warned.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"{warned.Count} with an alpha warning:");
            foreach (ConversionResult r in warned.OrderBy(r => r.Job.InputPath, StringComparer.OrdinalIgnoreCase))
                Console.WriteLine($"  {Path.GetFileName(r.Job.InputPath)}: {r.Warning}");
        }

        WarnAboutMissingTemplate(jobs.All(j => j.TemplatePath is null));
        return failures.IsEmpty ? 0 : 2;
    }

    private static void WarnAboutMissingTemplate(bool none)
    {
        if (!none) return;

        Console.WriteLine();
        Console.WriteLine("note: no template was used. The digest at 0x1C and the sampler settings at 0x40 " +
                          "and 0x4D are guesses; point --template-dir at the originals to keep the real ones.");
    }

    public static void Usage()
    {
        Console.WriteLine("""
            Converts images into TexToGo (.txtg) containers.

            Usage:
              txtg-cli convert <input> <output.txtg> [options]     one file, named explicitly
              txtg-cli convert <inputs...> --out <dir> [options]   a batch, each named after its source

            Inputs may be files, directories or wildcards, in .png .jpg .bmp .tga .gif .psd or
            .dds. A DDS already in the target block format is copied through without re-encoding.
            In a batch, Rock_A_Alb.png becomes Rock_A_Alb.txtg.

            Options:
              --out <dir>              Write one .txtg per input, named after the source file.
              --template <file.txtg>   Build on this container: it supplies the target format and
                                       every header field this tool cannot derive.
              --template-dir <dir>     Same, but matched per file by name - Rock_A_Alb.png takes
                                       <dir>/Rock_A_Alb.txtg. This is the one to use for a batch.
              --format <name>          Target format, overriding any template. --list-formats
                                       lists them; bc1, bc3, bc4, bc5, bc7, astc8x8, r8 and so on.
                                       With --template-dir it is also the fallback for inputs that
                                       have no matching original.
              --recursive              Descend into subdirectories when an input is a directory.
              --jobs <n>               Files to convert at once. Defaults to the processor count.
              --mips <n>               Mip levels to write. Defaults to the template's count, or a
                                       full chain down to 1x1.
              --layer <n>              Replace one layer of an array template, leaving the rest
                                       byte for byte as they were. Required for an array.
              --quality <fast|balanced|best>   BC encoder effort. Default balanced.
              --level <1-22>           Zstd compression level for the payloads. Default 12.
              --force                  Allow an input whose size differs from its template's.

            Examples:
              txtg-cli convert rock.png Rock_A_Alb.txtg --template romfs/TexToGo/Rock_A_Alb.txtg
              txtg-cli convert edited/ --out build/TexToGo --template-dir romfs/TexToGo
              txtg-cli convert "edited/*.dds" --out build --template-dir romfs/TexToGo --format bc7
              txtg-cli convert mat.png MaterialAlb.txtg --template orig/MaterialAlb.txtg --layer 12
            """);
    }
}
