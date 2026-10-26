namespace SpankBank.Web.Services;

/// <summary>
/// The two words the site uses for where data lives: "local" is this device (the files and watches here),
/// "online" is e621. They appear in the urls (/artists/local, /pools/online) and the text of the pages.
/// The urls are also written out in each page's @page line, so changing a word means changing those too.
/// </summary>
public static class Routes
{
    public const string Local = "local";
    public const string Online = "online";
}
