using SpankBank.Core;
using SpankBank.Data.Import;

namespace SpankBank.Tests;

[Collection("SiteConfig")]
public class ExportIndexTests : IDisposable
{
    public ExportIndexTests() => SiteConfig.Configure(SiteConfigTests.E621);

    public void Dispose() => SiteConfig.Configure(SiteConfigTests.E621);

    private static string Index(string url, string? checksum = "abc123")
        => $$"""[{"name":"posts","url":"{{url}}","updated_at":"2026-11-05T01:00:00Z"{{(checksum is null ? "" : $",\"checksum\":\"{checksum}\"")}}}]""";

    [Fact]
    public void The_download_address_carries_the_checksum_so_a_cached_older_file_is_not_served()
    {
        var files = E621ExportDownloadService.ParseLatestExports(Index("https://e621.net/db_export/posts.csv.gz"));

        Assert.Equal("https://e621.net/db_export/posts.csv.gz?v=abc123", files["posts"].DownloadUri.ToString());
        Assert.Equal("abc123", files["posts"].Sha256);
        Assert.Equal("posts-2026-11-05.csv.gz", files["posts"].FileName);
    }

    [Fact]
    public void An_existing_query_is_kept()
        => Assert.Equal("https://e621.net/db_export/posts.csv.gz?a=1&v=abc123",
            E621ExportDownloadService.ParseLatestExports(Index("https://e621.net/db_export/posts.csv.gz?a=1"))["posts"].DownloadUri.ToString());

    [Fact]
    public void Without_a_checksum_the_update_time_names_the_version()
    {
        var uri = E621ExportDownloadService.ParseLatestExports(Index("https://e621.net/db_export/posts.csv.gz", checksum: null))["posts"].DownloadUri;

        Assert.Matches(@"\?v=\d+$", uri.ToString());
    }

    [Fact]
    public void An_index_pointing_off_site_is_refused()
        => Assert.Throws<InvalidOperationException>(() => E621ExportDownloadService.ParseLatestExports(Index("https://example.org/db_export/posts.csv.gz")));
}
