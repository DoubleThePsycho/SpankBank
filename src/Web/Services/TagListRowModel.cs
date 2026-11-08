using SpankBank.Data.Repositories;

namespace SpankBank.Web.Services;

/// <summary>One row of a tag sidebar: the tag, how many posts here (local) and on the site (online) carry it, and its links.</summary>
public sealed record TagListRowModel(string CssName, TagInfo Tag, int Local, string SearchUrl, string? IncludeUrl = null, string? ExcludeUrl = null);
