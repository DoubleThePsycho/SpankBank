namespace SpankBank.Data.Import;

public sealed record E621ExportFileSet(
    string? PostsPath,
    string? TagsPath,
    string? PoolsPath,
    string? TagAliasesPath,
    string? TagImplicationsPath
);

public static class E621ExportFileDetector
{
    public static E621ExportFileSet DetectLatest(string exportFolderPath)
    {
        if (string.IsNullOrWhiteSpace(exportFolderPath) || !Directory.Exists(exportFolderPath))
            return new E621ExportFileSet(null, null, null, null, null);

        return new E621ExportFileSet(
            PostsPath: FindNewest(exportFolderPath, "posts-*.csv.gz"),
            TagsPath: FindNewest(exportFolderPath, "tags-*.csv.gz"),
            PoolsPath: FindNewest(exportFolderPath, "pools-*.csv.gz"),
            TagAliasesPath: FindNewest(exportFolderPath, "tag_aliases-*.csv.gz"),
            TagImplicationsPath: FindNewest(exportFolderPath, "tag_implications-*.csv.gz")
        );
    }

    private static string? FindNewest(string folderPath, string pattern)
        => Directory
            .EnumerateFiles(folderPath, pattern, SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ThenByDescending(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .Select(file => file.FullName)
            .FirstOrDefault();
}
