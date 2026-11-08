namespace SpankBank.Web.Services;

/// <summary>The header of a list page: the title, an optional "Add ..." button (opens the modal with the given id) and the Search button.</summary>
public sealed record ListHeaderModel(string Title, bool SearchActive, string? AddText = null, string? AddModal = "modal-add");
