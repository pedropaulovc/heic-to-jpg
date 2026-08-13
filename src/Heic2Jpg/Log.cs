using System.IO;

namespace Heic2Jpg;

/// <summary>
/// Append-only log under %LOCALAPPDATA%. The converter is normally launched by
/// Explorer with no console attached, so this file is the only way to find out
/// what happened after the fact.
/// </summary>
internal static class Log
{
    private static readonly object Gate = new();

    internal static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "heic-to-jpg",
        "convert.log");

    internal static void Write(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.ProcessId}] {message}";
        Console.WriteLine(line);

        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                File.AppendAllText(Path, line + Environment.NewLine);
            }
        }
        catch
        {
            // Logging must never be the reason a conversion fails.
        }
    }
}
