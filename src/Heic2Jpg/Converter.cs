using System.IO;
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
        "System.Photo.ISOSpeed",
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

    /// <summary>
    /// Exposure data that WIC reads back as a <see cref="double"/> but that EXIF
    /// stores as a RATIONAL pair. The policy writer rejects the double outright
    /// ("Unexpected property type or value"), so these have to be packed by hand
    /// and written to the raw EXIF tag instead.
    /// </summary>
    private static readonly (string Policy, ushort Tag, bool Signed)[] RationalFields =
    [
        ("System.Photo.ExposureTime", 0x829A, false),
        ("System.Photo.FNumber",      0x829D, false),
        ("System.Photo.FocalLength",  0x920A, false),
        ("System.Photo.ExposureBias", 0x9204, true),
    ];

    /// <summary>
    /// Decodes <paramref name="sourcePath"/> into <paramref name="targetDirectory"/>
    /// and returns the path it settled on, which may carry a collision suffix.
    /// </summary>
    internal static string Convert(string sourcePath, string targetDirectory, int quality)
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

        // Encode to a private temp file and move into place, so an interrupted run
        // can never leave a half-written .jpg that looks like a successful result.
        // The name is random rather than derived from the destination: two converter
        // processes racing for the same photo must not share a temp file.
        var tempPath = Path.Combine(targetDirectory, $"heic2jpg-{Path.GetRandomFileName()}.partial");
        try
        {
            using (var output = File.Create(tempPath))
            {
                encoder.Save(output);
            }

            // Keep the capture date meaningful for tools that sort by file time.
            // Set here rather than after the move: File.Move preserves timestamps,
            // and the final path is not known until the claim succeeds.
            File.SetLastWriteTime(tempPath, File.GetLastWriteTime(sourcePath));
            File.SetCreationTime(tempPath, File.GetCreationTime(sourcePath));

            return Claim(tempPath, sourcePath, targetDirectory);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    /// <summary>
    /// Renames the encoded file onto the first free candidate name.
    /// </summary>
    /// <remarks>
    /// The move itself is the allocation. A non-overwriting <see cref="File.Move(string, string)"/>
    /// fails if the destination exists, so whichever process moves first owns the
    /// name and the loser simply takes the next one. Testing with
    /// <see cref="File.Exists(string)"/> and then writing would leave a window in
    /// which a concurrent run replaces a finished JPEG.
    /// </remarks>
    private static string Claim(string tempPath, string sourcePath, string targetDirectory)
    {
        foreach (var candidate in NamingPolicy.CandidateNames(sourcePath, targetDirectory))
        {
            try
            {
                File.Move(tempPath, candidate);
                return candidate;
            }
            catch (IOException) when (File.Exists(candidate))
            {
                // Name taken, by an earlier run or by a process that just beat us to
                // it. Anything else -- sharing violation, full disk -- is a real
                // failure and must not be retried against a thousand more names.
            }
        }

        throw NamingPolicy.Exhausted(sourcePath);
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
            var value = TryGet(source, query);
            if (value is null || (value is string s && s.Length == 0))
            {
                continue;
            }

            TrySet(target, query, value);
        }

        foreach (var (policy, tag, signed) in RationalFields)
        {
            if (TryGet(source, policy) is not double value)
            {
                continue;
            }

            var query = $"/app1/ifd/exif/{{ushort={tag}}}";
            if (signed)
            {
                TrySet(target, query, PackSignedRational(value));
                continue;
            }

            if (value > 0)
            {
                TrySet(target, query, PackRational(value));
            }
        }

        return target;
    }

    private static object? TryGet(BitmapMetadata source, string query)
    {
        try
        {
            return source.GetQuery(query);
        }
        catch
        {
            return null; // Source codec does not expose this field.
        }
    }

    /// <summary>EXIF RATIONAL: numerator in the low 32 bits, denominator in the high 32.</summary>
    private static ulong PackRational(double value)
    {
        var (numerator, denominator) = ToFraction(value);
        return numerator | ((ulong)denominator << 32);
    }

    /// <summary>EXIF SRATIONAL: the same layout, but both halves are signed.</summary>
    private static long PackSignedRational(double value)
    {
        var (magnitude, denominator) = ToFraction(Math.Abs(value));
        var numerator = value < 0 ? -(int)magnitude : (int)magnitude;
        return (long)((uint)numerator | ((ulong)denominator << 32));
    }

    private static (uint Numerator, uint Denominator) ToFraction(double value)
    {
        if (value <= 0 || double.IsNaN(value) || double.IsInfinity(value))
        {
            return (0, 1);
        }

        // Sub-second shutter speeds are written as 1/N by every camera, and photo
        // viewers render that form directly. 167/10000 would be equivalent but
        // would display as an unrecognisable decimal.
        if (value < 1.0)
        {
            return (1, Math.Max((uint)Math.Round(1.0 / value), 1));
        }

        const uint Scale = 10000;
        if (value > uint.MaxValue / (double)Scale)
        {
            return ((uint)Math.Round(value), 1);
        }

        var numerator = (uint)Math.Round(value * Scale);
        var divisor = GreatestCommonDivisor(numerator, Scale);
        return (numerator / divisor, Scale / divisor);
    }

    private static uint GreatestCommonDivisor(uint a, uint b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return a == 0 ? 1 : a;
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
