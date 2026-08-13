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
    internal static string? ResolveOutputPath(string sourcePath, string targetDirectory)
    {
        var baseName = Path.GetFileNameWithoutExtension(sourcePath);

        // TODO(human): decide the output name and the collision rule.
        //
        // Available to you:
        //   baseName        e.g. "IMG_6090" (extension already stripped)
        //   targetDirectory the folder to write into
        //   Path.Combine(targetDirectory, baseName + ".jpg")
        //   File.Exists(candidate)
        //
        // Return the chosen full path, or null to skip the file.
        throw new NotImplementedException("NamingPolicy.ResolveOutputPath");
    }
}
