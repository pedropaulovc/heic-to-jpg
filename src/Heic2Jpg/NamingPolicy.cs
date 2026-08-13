using System.IO;

namespace Heic2Jpg;

/// <summary>
/// Decides what the converted file is called and what happens when that name is
/// already taken. Isolated from <see cref="Converter"/> on purpose: the pixel
/// pipeline has one correct answer, but this is a product decision.
/// </summary>
internal static class NamingPolicy
{
    /// <summary>
    /// Builds the destination path for a source image.
    /// </summary>
    /// <param name="sourcePath">Full path to the source .heic/.heif file.</param>
    /// <param name="targetDirectory">
    /// Directory the .jpg must land in. Already created by the caller.
    /// </param>
    /// <returns>
    /// The full destination path, or <c>null</c> to skip this file entirely
    /// (counted as "skipped", not "failed").
    /// </returns>
    /// <summary>
    /// Upper bound on the collision suffix. A folder holding a thousand copies of
    /// one photo means something has gone wrong upstream; failing loudly beats
    /// spinning through the filesystem.
    /// </summary>
    private const int MaxAttempts = 1000;

    internal static string? ResolveOutputPath(string sourcePath, string targetDirectory)
    {
        var baseName = Path.GetFileNameWithoutExtension(sourcePath);

        var candidate = Path.Combine(targetDirectory, baseName + ".jpg");
        if (!File.Exists(candidate))
        {
            return candidate;
        }

        // Never overwrite. A HEIC and a JPEG of the same shot routinely sit side by
        // side after an iPhone import, so the plain name colliding is normal rather
        // than exceptional -- and the existing file may be the better original.
        // Suffixing matches what Explorer itself does when pasting a duplicate.
        for (var i = 2; i <= MaxAttempts; i++)
        {
            candidate = Path.Combine(targetDirectory, $"{baseName} ({i}).jpg");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException(
            $"Could not find a free name for '{baseName}.jpg' after {MaxAttempts} attempts.");
    }
}
