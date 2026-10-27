using SpankBank.Core.Models;

namespace SpankBank.Data.Repositories;

public sealed record PostListItem(
    long Id,
    PostRating Rating,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int Score,
    string? FileExt,
    string? Md5,
    int FavCount,
    double? Duration = null,
    string? TagString = null
)
{
    private static readonly string[] VideoExtensions = ["webm", "mp4", "swf"];

    public bool IsVideo => FileExt is not null && VideoExtensions.Contains(FileExt, StringComparer.OrdinalIgnoreCase);

    private bool HasTag(string tag) => TagString is not null && (" " + TagString + " ").Contains(" " + tag + " ", StringComparison.Ordinal);

    /// <summary>Not a still image: e621's "animated" tag, or a video file.</summary>
    public bool IsAnimated => IsVideo || HasTag("animated");

    /// <summary>The tags the thumbnail badges key off (same names as e621ng's data-tags): animated, sound, sound_warning.</summary>
    public string BadgeTags
        => string.Join(' ', new[] { IsAnimated ? "animated" : null, HasTag("sound") ? "sound" : null, HasTag("sound_warning") ? "sound_warning" : null }.Where(t => t is not null));

    /// <summary>"0:45" or "1:02:03" for videos with a known length, else null.</summary>
    public string? DurationText
    {
        get
        {
            if (!IsAnimated || Duration is not > 0) return null;
            var t = TimeSpan.FromSeconds(Math.Round(Duration.Value));
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
        }
    }
}
