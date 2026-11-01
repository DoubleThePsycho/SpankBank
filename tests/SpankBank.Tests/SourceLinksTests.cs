using SpankBank.Data.Downloads;

namespace SpankBank.Tests;

public class SourceLinksTests
{
    private const string Source = """
        https://www.furaffinity.net/view/162895/
        https://d.furaffinity.net/art/nek0gami/1153762573/1153762573.nek0gami_dn_virago-2.jpg
        https://inkbunny.net/s/194809
        http://inkbunny.net/files/full/250/250589_GamiCross_162895.jpg
        https://va.ib.metapix.net/files/full/50/50948_paige_op_288.png
        https://example.com/files/other.png
        http://127.0.0.1/secret.png
        """;

    [Fact]
    public void Direct_file_urls_are_only_image_files_on_known_hosts_and_use_https()
    {
        Assert.Equal(
            ["https://d.furaffinity.net/art/nek0gami/1153762573/1153762573.nek0gami_dn_virago-2.jpg",
             "https://inkbunny.net/files/full/250/250589_GamiCross_162895.jpg",
             "https://va.ib.metapix.net/files/full/50/50948_paige_op_288.png"],
            SourceLinks.DirectFileUrls(Source));
    }

    [Theory]
    [InlineData("https://inkbunny.net/s/42183", 42183)]
    [InlineData("https://inkbunny.net/s/42183-p3-#pictop", 42183)]
    [InlineData("https://inkbunny.net/submissionview.php?id=42183&page=2", 42183)]
    [InlineData("https://www.inkbunny.net/s/42183", 42183)]
    public void Inkbunny_links_give_the_submission_id(string line, long id)
        => Assert.Equal([id], SourceLinks.InkbunnySubmissionIds(line));

    [Fact]
    public void Other_inkbunny_pages_and_empty_sources_give_nothing()
    {
        Assert.Empty(SourceLinks.InkbunnySubmissionIds("https://inkbunny.net/paige\nhttps://inkbunny.net/FireEagle12"));
        Assert.Empty(SourceLinks.InkbunnySubmissionIds(null));
        Assert.Empty(SourceLinks.DirectFileUrls(""));
    }

    [Fact]
    public void The_same_submission_is_listed_once()
        => Assert.Equal([7L], SourceLinks.InkbunnySubmissionIds("https://inkbunny.net/s/7\nhttps://inkbunny.net/s/7-p2"));

    [Fact]
    public void Scheme_is_dropped_for_the_archive_index()
        => Assert.Equal("inkbunny.net/files/full/1.png", SourceLinks.WithoutScheme("https://inkbunny.net/files/full/1.png"));
}
