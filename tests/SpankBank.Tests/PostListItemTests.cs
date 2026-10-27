using SpankBank.Core.Models;
using SpankBank.Data.Repositories;

namespace SpankBank.Tests;

public class PostListItemTests
{
    private static PostListItem Item(string? ext, string? tags = null, double? duration = null)
        => new(1, PostRating.Safe, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0, ext, "m", 0, duration, tags);

    [Fact] public void A_still_image_has_no_badges() { var i = Item("png", "cat sound_not"); Assert.False(i.IsAnimated); Assert.Equal("", i.BadgeTags); Assert.Null(i.DurationText); }
    [Fact] public void Videos_are_animated_even_without_the_tag() { Assert.True(Item("webm").IsAnimated); Assert.True(Item("MP4").IsAnimated); }
    [Fact] public void The_animated_tag_marks_a_gif() => Assert.Equal("animated", Item("gif", "cat animated").BadgeTags);
    [Fact] public void Sound_and_warning_tags_are_carried_whole_tags_only() => Assert.Equal("animated sound sound_warning", Item("webm", "x sound sound_warning").BadgeTags);
    [Fact] public void A_longer_tag_does_not_count_as_sound() => Assert.Equal("animated", Item("webm", "x sounds_good").BadgeTags);

    [Theory]
    [InlineData(45.2, "0:45")]
    [InlineData(59.6, "1:00")]
    [InlineData(125, "2:05")]
    [InlineData(3723, "1:02:03")]
    public void Duration_is_shown_as_minutes_and_seconds(double seconds, string text) => Assert.Equal(text, Item("webm", null, seconds).DurationText);

    [Fact] public void No_duration_badge_without_a_length_or_for_stills() { Assert.Null(Item("webm", null, null).DurationText); Assert.Null(Item("webm", null, 0).DurationText); Assert.Null(Item("png", null, 30).DurationText); }
}
