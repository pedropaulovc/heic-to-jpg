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
    /// Upper bound on the collision suffix. A folder holding a thousand copies of
    /// one photo means something has gone wrong upstream; failing loudly beats
    /// spinning through the filesystem.
    /// </summary>
    private const int MaxAttempts = 1000;

    /// <summary>
    /// Names the converted file may take, in order of preference: the source name
    /// with a .jpg extension, then " (2)", " (3)" and so on.
    /// </summary>
    /// <remarks>
    /// Deliberately does not test for existence. A caller that checks and then
    /// creates has a window in which another converter process takes the name, so
    /// the only reliable claim is the creation itself -- see
    /// <see cref="Converter.Convert"/>, which claims a name with a non-overwriting
    /// move and comes back here for the next one if it loses the race.
    /// </remarks>
    internal static IEnumerable<string> CandidateNames(string sourcePath, string targetDirectory)
    {
        var baseName = Path.GetFileNameWithoutExtension(sourcePath);

        // Never overwrite. A HEIC and a JPEG of the same shot routinely sit side by
        // side after an iPhone import, so the plain name colliding is normal rather
        // than exceptional -- and the existing file may be the better original.
        // Suffixing matches what Explorer itself does when pasting a duplicate.
        yield return Path.Combine(targetDirectory, baseName + ".jpg");

        for (var i = 2; i <= MaxAttempts; i++)
        {
            yield return Path.Combine(targetDirectory, $"{baseName} ({i}).jpg");
        }
    }

    internal static IOException Exhausted(string sourcePath)
    {
        var baseName = Path.GetFileNameWithoutExtension(sourcePath);
        return new IOException(
            $"Could not find a free name for '{baseName}.jpg' after {MaxAttempts} attempts.");
    }
}
