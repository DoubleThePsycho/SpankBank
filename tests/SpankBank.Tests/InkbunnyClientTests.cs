using System.Net;
using System.Text;
using SpankBank.Data.Downloads;

namespace SpankBank.Tests;

public class InkbunnyClientTests
{
    private sealed class Fake(Func<HttpRequestMessage, string> answer) : HttpMessageHandler
    {
        public List<(string Url, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.RequestUri!.ToString(), body));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer(request), Encoding.UTF8, "application/json") };
        }
    }

    private static InkbunnyClient Client(Fake fake, string? user = null, string? password = null)
        => new(new HttpClient(fake), _ => Task.CompletedTask, user, password);

    [Fact]
    public async Task Without_an_account_it_logs_in_as_guest_and_lists_the_files()
    {
        var fake = new Fake(request => request.RequestUri!.AbsolutePath.Contains("api_login")
            ? """{"sid":"abc"}"""
            : """{"submissions":[{"files":[{"file_url_full":"https://x/1.png","full_file_md5":"aa"},{"file_url_full":"https://x/2.png"}]}]}""");
        var client = Client(fake);

        var files = await client.FilesAsync(42, CancellationToken.None);

        Assert.True(client.IsGuest);
        Assert.Equal([new InkbunnyFile("https://x/1.png", "aa"), new InkbunnyFile("https://x/2.png", null)], files);
        Assert.Contains("username=guest", fake.Requests[0].Body);
        Assert.Contains("sid=abc", fake.Requests[1].Url);
        Assert.Contains("submission_ids=42", fake.Requests[1].Url);
    }

    [Fact]
    public async Task A_refused_login_is_reported_not_an_empty_answer()
    {
        var client = Client(new Fake(_ => """{"error_code":0,"error_message":"Login failed"}"""), "me", "wrong");

        Assert.Null(await client.FilesAsync(1, CancellationToken.None));
        Assert.Contains("Login failed", client.Problem);
    }

    [Fact]
    public async Task An_expired_session_is_renewed_once()
    {
        var logins = 0;
        var fake = new Fake(request =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("api_login")) return $$"""{"sid":"s{{++logins}}"}""";
            return request.RequestUri.Query.Contains("sid=s1") ? """{"error_code":2,"error_message":"Invalid Session ID"}""" : """{"submissions":[]}""";
        });
        var client = Client(fake, "me", "pw");

        var files = await client.FilesAsync(1, CancellationToken.None);

        Assert.NotNull(files);
        Assert.Empty(files);
        Assert.Equal(2, logins);
    }
}
