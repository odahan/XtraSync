using System.IO;
using System.Text.Json;

namespace XtraSync;

/// <summary>Stores separate recent folder lists in the current user's local application data.</summary>
public sealed class FolderHistory
{
    private readonly string filePath;

    public List<string> Left { get; set; } = [];
    public List<string> Right { get; set; } = [];

    public FolderHistory() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "XtraSync", "folder-history.json"))
    {
    }

    public FolderHistory(string filePath)
    {
        this.filePath = filePath;
    }

    /// <summary>Loads history without preventing startup when settings are unavailable or damaged.</summary>
    public void Load()
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(filePath));
            Left = ReadList(document.RootElement, nameof(Left));
            Right = ReadList(document.RootElement, nameof(Right));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            Left = [];
            Right = [];
        }
    }

    /// <summary>Moves an existing folder to the front, ignoring spelling and trailing separator duplicates.</summary>
    public bool Remember(string path, bool isLeft)
    {
        string? normalized = Normalize(path);
        if (normalized is null || !Directory.Exists(normalized))
        {
            return false;
        }

        List<string> folders = isLeft ? Left : Right;
        if (folders.Count > 0 && StringComparer.OrdinalIgnoreCase.Equals(folders[0], normalized))
        {
            return false;
        }

        folders.RemoveAll(folder => StringComparer.OrdinalIgnoreCase.Equals(folder, normalized));
        folders.Insert(0, normalized);
        if (folders.Count > 10)
        {
            folders.RemoveRange(10, folders.Count - 10);
        }

        return true;
    }

    /// <summary>Atomically replaces the saved history so interrupted writes preserve the previous file.</summary>
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        string temporaryPath = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(new { Left, Right }));
            File.Move(temporaryPath, filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static List<string> ReadList(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var entries) ||
            entries.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return entries.EnumerateArray()
            .Where(entry => entry.ValueKind == JsonValueKind.String)
            .Select(entry => Normalize(entry.GetString()!))
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToList();
    }

    private static string? Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
        {
            return null;
        }
    }
}
