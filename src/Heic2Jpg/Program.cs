using System.IO;

namespace Heic2Jpg;

internal static class Program
{
    private const int DefaultQuality = 92;

    [STAThread]
    internal static int Main(string[] args)
    {
        var quality = DefaultQuality;
        var outputDirectory = string.Empty;
        var sources = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            if (arg is "--quality" or "-q" && i + 1 < args.Length)
            {
                if (!int.TryParse(args[++i], out quality) || quality is < 1 or > 100)
                {
                    return Fail($"--quality must be between 1 and 100, got '{args[i]}'.");
                }
                continue;
            }

            if (arg is "--outdir" or "-o" && i + 1 < args.Length)
            {
                outputDirectory = args[++i];
                continue;
            }

            if (arg is "--help" or "-h" or "/?")
            {
                Console.WriteLine("heic2jpg [--quality 1-100] [--outdir DIR] <file.heic> [more files...]");
                return 0;
            }

            sources.Add(arg);
        }

        if (sources.Count == 0)
        {
            return Fail("No input files. Try --help.");
        }

        Log.Write($"Starting: {sources.Count} file(s), quality={quality}, outdir='{outputDirectory}'");

        var failures = new List<string>();
        var converted = 0;
        var skipped = 0;

        foreach (var source in sources)
        {
            try
            {
                if (!File.Exists(source))
                {
                    failures.Add($"{Path.GetFileName(source)}: file not found");
                    continue;
                }

                var targetDirectory = string.IsNullOrEmpty(outputDirectory)
                    ? Path.GetDirectoryName(Path.GetFullPath(source))!
                    : outputDirectory;

                Directory.CreateDirectory(targetDirectory);

                var destination = NamingPolicy.ResolveOutputPath(source, targetDirectory);
                if (destination is null)
                {
                    Log.Write($"Skipped (naming policy): {source}");
                    skipped++;
                    continue;
                }

                Converter.Convert(source, destination, quality);
                Log.Write($"Converted: {source} -> {destination}");
                converted++;
            }
            catch (Exception ex)
            {
                Log.Write($"FAILED: {source}: {ex.GetType().Name}: {ex.Message}");
                failures.Add($"{Path.GetFileName(source)}: {ex.Message}");
            }
        }

        Log.Write($"Done: {converted} converted, {skipped} skipped, {failures.Count} failed");

        if (failures.Count > 0)
        {
            Report(BuildFailureSummary(converted, skipped, failures));
            return 1;
        }

        return 0;
    }

    private static string BuildFailureSummary(int converted, int skipped, List<string> failures)
    {
        var lines = new List<string>();
        if (converted > 0)
        {
            lines.Add($"{converted} file(s) converted.");
        }
        if (skipped > 0)
        {
            lines.Add($"{skipped} file(s) skipped.");
        }

        lines.Add($"{failures.Count} file(s) failed:");
        // Explorer selections can be huge; an unbounded dialog would run off screen.
        lines.AddRange(failures.Take(10).Select(f => "  " + f));
        if (failures.Count > 10)
        {
            lines.Add($"  ...and {failures.Count - 10} more.");
        }

        lines.Add(string.Empty);
        lines.Add("Details: " + Log.Path);
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Launched from Explorer there is no console to read, so surface problems in a
    /// dialog. Successful runs stay silent -- the new .jpg next to the source is
    /// the confirmation.
    /// </summary>
    private static void Report(string message)
    {
        try
        {
            System.Windows.MessageBox.Show(
                message,
                "Convert to JPEG",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
        }
        catch
        {
            Console.Error.WriteLine(message);
        }
    }

    private static int Fail(string message)
    {
        Log.Write("ERROR: " + message);
        Console.Error.WriteLine(message);
        return 2;
    }
}
