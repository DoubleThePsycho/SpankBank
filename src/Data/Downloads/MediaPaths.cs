namespace SpankBank.Data.Downloads;

/// <summary>
/// Where files live under the media root, mirroring e621's own paths (data/ab/cd/md5.ext):
/// originals at ab/cd/md5.ext, previews at preview/ab/cd/md5.jpg, samples at sample/ab/cd/md5.jpg.
/// </summary>
public static class MediaPaths
{
    public static string Original(string root, string md5, string ext) => Path.Combine(root, md5[..2], md5[2..4], $"{md5}.{ext}");
    public static string Preview(string root, string md5) => Path.Combine(root, "preview", md5[..2], md5[2..4], $"{md5}.jpg");
    public static string Sample(string root, string md5) => Path.Combine(root, "sample", md5[..2], md5[2..4], $"{md5}.jpg");

    /// <summary>The same path as a URL below the web app's /media.</summary>
    public static string OriginalUrl(string md5, string ext) => $"/media/{md5[..2]}/{md5[2..4]}/{md5}.{ext}";
    public static string PreviewUrl(string md5) => $"/media/preview/{md5[..2]}/{md5[2..4]}/{md5}.jpg";
    public static string SampleUrl(string md5) => $"/media/sample/{md5[..2]}/{md5[2..4]}/{md5}.jpg";
}
