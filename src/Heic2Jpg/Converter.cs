using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Heic2Jpg;

internal static class Converter
{
    /// <summary>
    /// Photo metadata worth carrying from the HEIC into the JPEG. Everything not
    /// listed here is lost on re-encode, which is why the list is explicit.
    /// Each is copied independently: a single unsupported field must not abort
    /// the rest, since HEIF sources vary a lot in what they populate.
    /// </summary>
    private static readonly string[] PolicyQueries =
    [
        "System.Photo.DateTaken",
        "System.Photo.CameraManufacturer",
        "System.Photo.CameraModel",
        "System.Photo.ExposureTime",
        "System.Photo.FNumber",
        "System.Photo.ISOSpeed",
        "System.Photo.FocalLength",
        "System.Photo.ExposureBias",
        "System.Photo.Flash",
        "System.Photo.LensModel",
        "System.GPS.Latitude",
        "System.GPS.LatitudeRef",
        "System.GPS.Longitude",
        "System.GPS.LongitudeRef",
        "System.GPS.Altitude",
        "System.Image.Title",
        "System.Comment",
        "System.Copyright",
    ];

    internal static void Convert(string sourcePath, string destinationPath, int quality)
    {
        BitmapFrame frame;
        BitmapMetadata? sourceMetadata;

        // OnLoad caches the pixels up front so the file handle is released before
        // we start writing, which matters when the destination is the same folder.
        using (var input = File.OpenRead(sourcePath))
        {
            var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0)
            {
                throw new InvalidOperationException("Decoder returned no frames.");
            }

            frame = decoder.Frames[0];
            sourceMetadata = frame.Metadata as BitmapMetadata;
        }

        // JPEG has no alpha channel. HEIC may carry one, so flatten deliberately
        // rather than letting the encoder pick a conversion for us.
        var opaque = new FormatConvertedBitmap(frame, PixelFormats.Bgr24, null, 0.0);
        opaque.Freeze();

        var encoder = new JpegBitmapEncoder { QualityLevel = quality };
        encoder.Frames.Add(BitmapFrame.Create(opaque, null, BuildJpegMetadata(sourceMetadata), null));

        // Encode to a sibling temp file and move into place, so an interrupted run
        // can never leave a half-written .jpg that looks like a successful result.
        var tempPath = destinationPath + ".partial";
        try
        {
            using (var output = File.Create(tempPath))
            {
                encoder.Save(output);
            }

            File.Move(tempPath, destinationPath, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }

        // Keep the capture date meaningful for tools that sort by file time.
        var written = File.GetLastWriteTime(sourcePath);
        File.SetLastWriteTime(destinationPath, written);
        File.SetCreationTime(destinationPath, File.GetCreationTime(sourcePath));
    }

    private static BitmapMetadata BuildJpegMetadata(BitmapMetadata? source)
    {
        var target = new BitmapMetadata("jpg");

        // The Microsoft HEIF decoder already applies EXIF rotation during decode and
        // reports orientation as 1, so the decoded pixels are upright. Stamping 1
        // explicitly stops any viewer from re-rotating an already-rotated image.
        TrySet(target, "System.Photo.Orientation", (ushort)1);

        if (source is null)
        {
            return target;
        }

        foreach (var query in PolicyQueries)
        {
            object? value = null;
            try
            {
                value = source.GetQuery(query);
            }
            catch
            {
                continue; // Source codec does not expose this field.
            }

            if (value is null || (value is string s && s.Length == 0))
            {
                continue;
            }

            TrySet(target, query, value);
        }

        return target;
    }

    private static void TrySet(BitmapMetadata target, string query, object value)
    {
        try
        {
            target.SetQuery(query, value);
        }
        catch
        {
            // JPEG cannot represent every HEIF field; skipping one is not an error.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }
}
