using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace XtraSync;

/// <summary>Stores exclusion patterns and mirror preferences, and matches names or relative paths on both sides.</summary>
public sealed class ExclusionList
{
    private readonly string filePath;
    public string Line { get; private set; } = "";
    public List<string> History { get; private set; } = [];
    public bool CompareAfterMirroring { get; private set; }

    public ExclusionList(string? filePath = null)
    {
        this.filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XtraSync", "exclusions.json");
    }

    /// <summary>Restores patterns without preventing startup if settings are missing or damaged.</summary>
    public string Load() => LoadSettings().CommonPatterns;

    /// <summary>Restores typed filters, including the legacy JSON string format.</summary>
    public FilterSettings LoadSettings()
    {
        CompareAfterMirroring = false;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(filePath));
            if (document.RootElement.ValueKind == JsonValueKind.String)
            {
                Line = document.RootElement.GetString() ?? "";
                History = [];
                return ExclusionLine.Parse(Line);
            }
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("ExclusionLine", out var line))
            {
                Line = line.GetString() ?? "";
                bool cleanup = document.RootElement.GetProperty("DeleteExcludedFromDestination").GetBoolean();
                History = ReadHistory(document.RootElement);
                CompareAfterMirroring = document.RootElement.TryGetProperty(nameof(CompareAfterMirroring), out var compare)
                    && compare.ValueKind == JsonValueKind.True;
                return ExclusionLine.Parse(Line) with { DeleteExcludedFromDestination = cleanup };
            }
            var settings = document.RootElement.Deserialize<FilterSettings>() ?? new();
            settings = settings with
            {
                CommonPatterns = settings.CommonPatterns ?? "",
                FilePatterns = settings.FilePatterns ?? "",
                FolderPatterns = settings.FolderPatterns ?? ""
            };
            Line = ExclusionLine.Format(settings);
            History = [];
            return settings;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException
            or InvalidOperationException or KeyNotFoundException)
        {
            Line = "";
            History = [];
            return new();
        }
    }

    /// <summary>Atomically saves the list, including when it is cleared.</summary>
    public void Save(string text) => SaveSettings(new FilterSettings(text));

    /// <summary>Atomically persists all filters and their mirror cleanup option together.</summary>
    public void SaveSettings(FilterSettings settings) => SaveLine(ExclusionLine.Format(settings), settings.DeleteExcludedFromDestination);

    /// <summary>Saves the current preferences without inserting intermediate keystrokes into history.</summary>
    public void SaveLine(string line, bool deleteExcludedFromDestination, bool? compareAfterMirroring = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath))!);
        string temporary = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new
            {
                ExclusionLine = line,
                DeleteExcludedFromDestination = deleteExcludedFromDestination,
                CompareAfterMirroring = compareAfterMirroring ?? CompareAfterMirroring,
                History
            }));
            File.Move(temporary, filePath, true);
            Line = line;
            CompareAfterMirroring = compareAfterMirroring ?? CompareAfterMirroring;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>Commits one complete nonempty line to the ten most recent distinct entries.</summary>
    public bool Remember(string line)
    {
        string key = ExclusionLine.HistoryKey(line);
        if (key.Length == 0) return false;
        if (History.Count > 0 && StringComparer.OrdinalIgnoreCase.Equals(ExclusionLine.HistoryKey(History[0]), key))
            return false;
        History.RemoveAll(item => StringComparer.OrdinalIgnoreCase.Equals(ExclusionLine.HistoryKey(item), key));
        History.Insert(0, line.Trim());
        if (History.Count > 10) History.RemoveRange(10, History.Count - 10);
        return true;
    }

    private static List<string> ReadHistory(JsonElement root)
    {
        if (!root.TryGetProperty("History", out var history) || history.ValueKind != JsonValueKind.Array) return [];
        return history.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!.Trim()).Where(item => ExclusionLine.HistoryKey(item).Length > 0)
            .DistinctBy(ExclusionLine.HistoryKey, StringComparer.OrdinalIgnoreCase).Take(10).ToList();
    }

    /// <summary>Compiles semicolon or newline separated patterns with star and question mark wildcards.</summary>
    public static Func<string, bool> Matcher(string text)
    {
        var patterns = text.Split([';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(pattern => pattern.Trim().Replace('\\', '/').Trim('/'))
            .Where(pattern => pattern.Length > 0)
            .Select(pattern => (HasPath: pattern.Contains('/'), Expression: new Regex(
                "^" + Regex.Escape(pattern).Replace("\\*", "[^/]*").Replace("\\?", "[^/]") + "$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)))
            .ToArray();
        return path =>
        {
            string relative = path.Replace('\\', '/');
            string name = relative[(relative.LastIndexOf('/') + 1)..];
            return patterns.Any(pattern => pattern.Expression.IsMatch(pattern.HasPath ? relative : name));
        };
    }
}
