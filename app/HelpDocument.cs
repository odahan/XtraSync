using System.Diagnostics;
using System.IO;

namespace XtraSync;

/// <summary>Recreates the bundled user guide on every request before opening it in the default browser.</summary>
public sealed class HelpDocument
{
    public const string FileName = "XtraSync-help.html";
    public const string ResourceName = "XtraSync.UserHelp.html";
    private readonly Action<string> open;
    public string FilePath { get; }

    /// <summary>Uses the real executable directory unless an isolated output directory is supplied.</summary>
    public HelpDocument(string? directory = null, Action<string>? open = null)
    {
        directory ??= Path.GetDirectoryName(Environment.ProcessPath
            ?? throw new IOException("The executable location is unavailable."))!;
        FilePath = Path.Combine(Path.GetFullPath(directory), FileName);
        this.open = open ?? OpenInBrowser;
    }

    /// <summary>Unconditionally replaces the disk copy with the embedded version, then opens that exact file.</summary>
    public void Show()
    {
        using Stream resource = typeof(HelpDocument).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The bundled user guide is missing.");
        string directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".xtrasync-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                resource.CopyTo(output);
            File.Move(temporary, FilePath, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        open(FilePath);
    }

    private static void OpenInBrowser(string path) =>
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
}
