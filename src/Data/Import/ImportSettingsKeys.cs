namespace SpankBank.Data.Import;

public static class ImportSettingsKeys
{
    public const string ExportFolderPath = "Import.ExportFolderPath";
    public const string PostsExportPath = "Import.PostsExportPath";
    public const string TagsExportPath = "Import.TagsExportPath";
    public const string PoolsExportPath = "Import.PoolsExportPath";
    public const string TagAliasesExportPath = "Import.TagAliasesExportPath";
    public const string TagImplicationsExportPath = "Import.TagImplicationsExportPath";
    public const string AutoDownloadExports = "Import.AutoDownloadExports";
    public const string LastExportCheckAt = "Import.LastExportCheckAt";
    public const string LastExportDownloadAt = "Import.LastExportDownloadAt";
    public const string LastExportDownloadResult = "Import.LastExportDownloadResult";
    public const string LastExportDownloadStatusText = "Import.LastExportDownloadStatusText";
    /// <summary>"true" lets the next posts import shrink the catalog by more than the sanity limit; cleared after that import succeeds.</summary>
    public const string ForceNextPostsImport = "Import.ForceNextPostsImport";
    public const string PromptSuppressedUntilDate = "Import.PromptSuppressedUntilDate";
}
