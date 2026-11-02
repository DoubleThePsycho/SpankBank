using SkiaSharp;
using SpankBank.Data.Downloads;

namespace SpankBank.Tests;

public class FlashPreviewTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "spankbank-flash-" + Guid.NewGuid().ToString("N"));

    public FlashPreviewTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private string Png(int width, int height, Func<int, int, SKColor> pixel)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++) bitmap.SetPixel(x, y, pixel(x, y));
        var path = Path.Combine(_folder, Guid.NewGuid().ToString("N") + ".png");
        using (var data = SKImage.FromBitmap(bitmap).Encode(SKEncodedImageFormat.Png, 100))
        using (var file = File.Create(path))
            data.SaveTo(file);
        return path;
    }

    [Fact]
    public void A_single_colour_frame_is_blank()
        => Assert.True(FlashPreview.DominantShare(Png(200, 150, (_, _) => new SKColor(170, 190, 210))) >= FlashPreview.BlankShare);

    [Fact]
    public void A_frame_with_real_content_is_not_blank()
    {
        // Left 40% black on white.
        var share = FlashPreview.DominantShare(Png(200, 150, (x, _) => x < 80 ? SKColors.Black : SKColors.White));
        Assert.True(share < FlashPreview.BlankShare);
        Assert.InRange(share, 0.55, 0.65);
    }

    [Fact]
    public void Large_frames_are_scaled_down_to_the_preview_size_and_small_ones_are_kept()
    {
        var big = Png(800, 400, (x, y) => new SKColor((byte)(x % 256), (byte)(y % 256), 0));
        var small = Png(100, 80, (x, y) => new SKColor((byte)(x * 2), (byte)(y * 3), 0));
        var bigOut = Path.Combine(_folder, "preview", "ab", "cd", "big.jpg");
        var smallOut = Path.Combine(_folder, "preview", "ab", "cd", "small.jpg");

        FlashPreview.WriteJpeg(big, bigOut);
        FlashPreview.WriteJpeg(small, smallOut);

        using var bigImage = SKBitmap.Decode(bigOut);
        Assert.Equal((300, 150), (bigImage.Width, bigImage.Height));
        using var smallImage = SKBitmap.Decode(smallOut);
        Assert.Equal((100, 80), (smallImage.Width, smallImage.Height));
        Assert.False(File.Exists(bigOut + ".part"));
    }

    [Fact]
    public void Transparent_stages_are_flattened_on_white()
    {
        var png = Png(40, 40, (_, _) => SKColors.Transparent);
        var jpeg = Path.Combine(_folder, "t.jpg");

        FlashPreview.WriteJpeg(png, jpeg);

        using var image = SKBitmap.Decode(jpeg);
        var pixel = image.GetPixel(20, 20);
        Assert.True(pixel.Red > 240 && pixel.Green > 240 && pixel.Blue > 240);
    }
}
