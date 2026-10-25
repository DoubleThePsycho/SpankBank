namespace SpankBank.Data.Import;

public sealed record ExportCleanupResult(bool Deleted, string? Warning);

public static class E621ExportFileCleanup
{
    public static ExportCleanupResult DeleteAfterSuccessfulHandledImport(string exportPath, string exportFolderPath)
    {
        if (string.IsNullOrWhiteSpace(exportPath)
            || string.IsNullOrWhiteSpace(exportFolderPath)
            || exportPath.EndsWith(".partial", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(exportPath))
        {
            return new ExportCleanupResult(false, null);
        }

        var fullExportPath = Path.GetFullPath(exportPath);
        var fullExportFolder = Path.GetFullPath(exportFolderPath);
        if (!fullExportPath.StartsWith(fullExportFolder, StringComparison.OrdinalIgnoreCase))
            return new ExportCleanupResult(false, null);

        var fileName = Path.GetFileName(exportPath);
        if (!IsHandledImportFile(fileName))
            return new ExportCleanupResult(false, null);

        try
        {
            File.Delete(fullExportPath);
            return new ExportCleanupResult(true, null);
        }
        catch (IOException ex)
        {
            return new ExportCleanupResult(false, $"Cleanup warning: {fileName} could not be deleted because it appears to be in use. {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return new ExportCleanupResult(false, $"Cleanup warning: {fileName} could not be deleted due to permissions. {ex.Message}");
        }
    }

    private static bool IsHandledImportFile(string fileName)
        => (fileName.StartsWith("tags-", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("posts-", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("tag_aliases-", StringComparison.OrdinalIgnoreCase))
        && (fileName.EndsWith(".csv.gz", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase));
}
