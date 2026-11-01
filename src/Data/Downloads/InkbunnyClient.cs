using System.Net.Http.Json;
using System.Text.Json;

namespace SpankBank.Data.Downloads;

/// <summary>One file of an Inkbunny submission. <paramref name="Md5"/> is the full-size file's md5 when the API gave it.</summary>
public sealed record InkbunnyFile(string Url, string? Md5);

/// <summary>
/// The little of Inkbunny's API the downloader needs: log in, then list a submission's files. Without an account it uses
/// the guest login, which only shows general-rated work; mature work needs an account with those ratings switched on.
/// </summary>
public sealed class InkbunnyClient
{
    private const string Site = "https://inkbunny.net";
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http;
    private readonly Func<CancellationToken, Task> _wait;
    private readonly string _username;
    private readonly string _password;
    private string? _sid;

    public InkbunnyClient(HttpClient http, Func<CancellationToken, Task> wait, string? username, string? password)
    {
        _http = http;
        _wait = wait;
        IsGuest = string.IsNullOrWhiteSpace(username);
        _username = IsGuest ? "guest" : username!.Trim();
        _password = IsGuest ? "" : password ?? "";
    }

    public bool IsGuest { get; }

    /// <summary>What went wrong the last time a call returned null.</summary>
    public string? Problem { get; private set; }

    /// <returns>The files, none when Inkbunny shows nothing for that id (gone, or hidden by the account's ratings); null when it could not be asked.</returns>
    public async Task<IReadOnlyList<InkbunnyFile>?> FilesAsync(long submissionId, CancellationToken cancellationToken, bool mayRelogin = true)
    {
        try
        {
            if (_sid is null && !await LoginAsync(cancellationToken)) return null;

            await _wait(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(CallTimeout);
            using var response = await _http.GetAsync($"{Site}/api_submissions.php?sid={Uri.EscapeDataString(_sid!)}&submission_ids={submissionId}", timeout.Token);
            if (!response.IsSuccessStatusCode) return Fail($"HTTP {(int)response.StatusCode}");

            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
            if (json.RootElement.TryGetProperty("error_code", out var code))
            {
                _sid = null; // most likely an expired session
                return mayRelogin ? await FilesAsync(submissionId, cancellationToken, mayRelogin: false) : Fail($"error {code}");
            }

            var files = new List<InkbunnyFile>();
            if (json.RootElement.TryGetProperty("submissions", out var submissions) && submissions.ValueKind == JsonValueKind.Array)
                foreach (var submission in submissions.EnumerateArray())
                {
                    if (!submission.TryGetProperty("files", out var list) || list.ValueKind != JsonValueKind.Array) continue;
                    foreach (var file in list.EnumerateArray())
                        if (Text(file, "file_url_full") is { Length: > 0 } url)
                            files.Add(new InkbunnyFile(url, Text(file, "full_file_md5")));
                }
            Problem = null;
            return files;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return Fail(ex is OperationCanceledException ? "no answer in time" : ex.Message);
        }
    }

    private async Task<bool> LoginAsync(CancellationToken cancellationToken)
    {
        await _wait(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CallTimeout);
        using var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = _username, ["password"] = _password });
        using var response = await _http.PostAsync($"{Site}/api_login.php", form, timeout.Token);

        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
        if (Text(json.RootElement, "sid") is { Length: > 0 } sid)
        {
            _sid = sid;
            return true;
        }

        Fail($"login refused: {Text(json.RootElement, "error_message") ?? $"HTTP {(int)response.StatusCode}"}");
        return false;
    }

    private IReadOnlyList<InkbunnyFile>? Fail(string problem)
    {
        Problem = problem;
        return null;
    }

    private static string? Text(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
