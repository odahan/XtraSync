namespace XtraSync;

/// <summary>Defines upstream name exclusions and the optional one-way destination cleanup.</summary>
public sealed record FilterSettings(
    string CommonPatterns = "",
    string FilePatterns = "",
    string FolderPatterns = "",
    bool DeleteExcludedFromDestination = false);
