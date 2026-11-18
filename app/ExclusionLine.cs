namespace XtraSync;

/// <summary>Converts the editable exclusion line to typed patterns and back without losing entry types.</summary>
public static class ExclusionLine
{
    public static string[] Items(string text) => text.Split([';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
        .Select(item => item.Trim()).Where(item => item.Length > 0).ToArray();

    public static FilterSettings Parse(string text)
    {
        var common = new List<string>();
        var files = new List<string>();
        var folders = new List<string>();
        foreach (string item in Items(text))
        {
            if (item.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                files.AddRange(Items(item[5..]));
            else if (item.StartsWith("folder:", StringComparison.OrdinalIgnoreCase))
                folders.AddRange(Items(item[7..]));
            else
                common.Add(item);
        }
        return new(string.Join("; ", common), string.Join("; ", files), string.Join("; ", folders));
    }

    public static string Format(FilterSettings settings) => string.Join("; ",
        Items(settings.CommonPatterns)
            .Concat(Items(settings.FilePatterns).Select(item => "file:" + item))
            .Concat(Items(settings.FolderPatterns).Select(item => "folder:" + item)));

    public static string ToMultiline(string patterns) => string.Join(Environment.NewLine, Items(patterns));

    /// <summary>Ignores separator spacing, slash spelling and case when comparing recent lines.</summary>
    public static string HistoryKey(string line) => Format(Parse(line)).Replace('\\', '/');
}
