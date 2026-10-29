using SpankBank.Data.Repositories;

namespace SpankBank.Web.Services;

/// <summary>
/// What the shared pools table partial (_PoolsTable) needs. <paramref name="View"/> makes the headers server-side sort links
/// (the paged /pools/local list); without it the table sorts in the browser. <paramref name="ToggleAction"/> is where the
/// status pill posts (fields id, enabled, returnUrl).
/// </summary>
public sealed record PoolsTableModel(
    IReadOnlyList<WatchedPoolItem> Items,
    string ToggleAction,
    ListView? View = null,
    bool ShowArtist = true,
    bool Editable = false);
