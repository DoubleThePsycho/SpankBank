namespace SpankBank.Web.Services;

/// <summary>e621's tag categories as the sidebar shows them (TagCategory::SPLIT_HEADER_LIST): order, css name, header.</summary>
public static class TagCategories
{
    public static readonly (int Category, string CssName, string Header)[] SidebarOrder =
    [
        (6, "invalid", "Invalid"), (1, "artist", "Artists"), (2, "contributor", "Contributors"), (3, "copyright", "Copyrights"),
        (4, "character", "Characters"), (5, "species", "Species"), (0, "general", "General"), (7, "meta", "Meta"), (8, "lore", "Lore"),
    ];
}
