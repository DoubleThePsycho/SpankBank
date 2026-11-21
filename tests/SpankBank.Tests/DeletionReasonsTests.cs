using SpankBank.Data.Downloads;

namespace SpankBank.Tests;

public class DeletionReasonsTests
{
    [Theory]
    [InlineData("Duplicate or inferior version of another post (post #6863643)", DeletionKind.Inferior)]
    [InlineData("Inferior version/duplicate of post #123", DeletionKind.Inferior)]
    [InlineData("Previously deleted (post #1) / Inferior version/duplicate of post #2", DeletionKind.Inferior)]
    [InlineData("Does not meet minimum quality standards (Trivial or low quality edit)", DeletionKind.LowEffortEdit)]
    [InlineData("Does not meet minimum quality standards (Trivial edit)", DeletionKind.LowEffortEdit)]
    [InlineData("Does not meet minimum quality standards (Artistic)", DeletionKind.Other)]
    [InlineData("Does not meet minimum quality standards (Bad digitization of traditional media)", DeletionKind.Other)]
    [InlineData("Paysite, commercial, or subscription content", DeletionKind.Other)]
    [InlineData("Downscaled from original source", DeletionKind.Other)]
    [InlineData("", DeletionKind.Other)]
    [InlineData(null, DeletionKind.Other)]
    public void The_reason_decides_whether_a_deletion_counts_as_inferior_or_a_low_effort_edit(string? reason, DeletionKind expected)
        => Assert.Equal(expected, DeletionReasons.Classify(reason));

    [Fact]
    public void The_latest_deletion_flag_of_each_post_is_its_reason()
    {
        const string json = """
            [
              {"id":10,"post_id":1,"type":"deletion","reason":"Paysite/commercial content"},
              {"id":12,"post_id":1,"type":"deletion","reason":"Duplicate or inferior version of another post (post #9)"},
              {"id":11,"post_id":2,"type":"flag","reason":"Duplicate or inferior version of another post (post #9)"},
              {"id":13,"post_id":3,"type":"deletion","reason":"Irrelevant to site (Human only)"}
            ]
            """;

        var reasons = DeletionReasons.Parse(json, [1, 2, 3, 4])!;

        Assert.Equal("Duplicate or inferior version of another post (post #9)", reasons[1]);
        Assert.Null(reasons[2]); // only a flag, never deleted for it
        Assert.Equal("Irrelevant to site (Human only)", reasons[3]);
        Assert.Null(reasons[4]); // no flags at all
    }

    [Theory]
    [InlineData("""{"success":false,"reason":"found unpermitted parameter"}""")]
    [InlineData("<html>Please wait</html>")]
    [InlineData("")]
    public void An_answer_that_is_not_a_list_of_flags_is_not_trusted(string answer)
        => Assert.Null(DeletionReasons.Parse(answer, [1]));

    [Theory]
    [InlineData("Young [[human]]-[[humanoid|like]] character in an explicit situation", "Young human-like character in an explicit situation")]
    [InlineData("[[conditional_dnp|Conditional DNP]] (Only the artist)", "Conditional DNP (Only the artist)")]
    [InlineData("Duplicate or inferior version of another post (post #6863643)", "Duplicate or inferior version of another post (post #6863643)")]
    [InlineData("takedown #28134: Artist requested removal", "Artist requested removal")]
    [InlineData("Takedown #7: [[conditional_dnp|Conditional DNP]]", "Conditional DNP")]
    [InlineData("Previously deleted by a takedown #5: not at the start", "Previously deleted by a takedown #5: not at the start")]
    public void The_sites_link_markup_is_removed_from_a_reason_shown_on_the_post_page(string reason, string expected)
        => Assert.Equal(expected, DeletionReasons.PlainText(reason));
}
