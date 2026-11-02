using SkiaSharp;

namespace SpankBank.Data.Downloads;

/// <summary>
/// Turns a frame rendered from a Flash file (a PNG from Ruffle's exporter) into the post's preview JPEG, and tells
/// whether the frame shows anything: the first seconds of a game are often a plain preloader.
/// </summary>
public static class FlashPreview
{
    /// <summary>Longest side of the preview, in pixels.</summary>
    public const int MaxSide = 300;

    /// <summary>A frame where this share of the pixels is (nearly) one colour counts as empty and a later frame is tried.</summary>
    public const double BlankShare = 0.985;

    /// <summary>Even the best frame is not used when it is this uniform.</summary>
    public const double HopelessShare = 0.995;

    /// <summary>The share of the picture taken by its most common colour, from 0 to 1 (looked at on a coarse grid, in 16 steps per channel).</summary>
    public static double DominantShare(string pngPath)
    {
        using var source = Decode(pngPath);
        using var grid = source.Resize(new SKImageInfo(48, 48, SKColorType.Rgba8888, SKAlphaType.Unpremul), new SKSamplingOptions(SKFilterMode.Nearest))
            ?? throw new InvalidOperationException("The frame could not be scaled.");

        var buckets = new Dictionary<int, int>();
        for (var y = 0; y < grid.Height; y++)
            for (var x = 0; x < grid.Width; x++)
            {
                var pixel = grid.GetPixel(x, y);
                var key = (pixel.Red >> 4) << 8 | (pixel.Green >> 4) << 4 | (pixel.Blue >> 4);
                buckets[key] = buckets.GetValueOrDefault(key) + 1;
            }
        return buckets.Values.Max() / (double)(grid.Width * grid.Height);
    }

    /// <summary>Scales the PNG down to <see cref="MaxSide"/> (never up) and writes it as a JPEG, via a temporary file so a reader never sees half of it.</summary>
    public static void WriteJpeg(string pngPath, string jpegPath)
    {
        using var source = Decode(pngPath);
        var scale = Math.Min(1.0, MaxSide / (double)Math.Max(source.Width, source.Height));
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));

        // Flash movies may have transparent stages; JPEG has no alpha, so draw on white.
        using var target = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(target))
        {
            canvas.Clear(SKColors.White);
            using var image = SKImage.FromBitmap(source);
            canvas.DrawImage(image, new SKRect(0, 0, width, height), new SKSamplingOptions(SKCubicResampler.Mitchell));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(jpegPath)!);
        var part = jpegPath + ".part";
        using (var encoded = SKImage.FromBitmap(target).Encode(SKEncodedImageFormat.Jpeg, 85))
        using (var file = File.Create(part))
            encoded.SaveTo(file);
        File.Move(part, jpegPath, overwrite: true);
    }

    private static SKBitmap Decode(string path) => SKBitmap.Decode(path) ?? throw new InvalidOperationException($"{Path.GetFileName(path)} is not an image.");
}
