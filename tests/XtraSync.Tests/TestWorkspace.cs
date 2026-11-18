namespace XtraSync.Tests;

/// <summary>Owns disposable fixtures under the test output directory, away from user data.</summary>
public abstract class TestWorkspace : IDisposable
{
    protected static readonly DateTime Baseline = new(2026, 1, 15, 12, 30, 15, DateTimeKind.Utc);
    protected string Root { get; } = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
    protected string Left => Path.Combine(Root, "left");
    protected string Right => Path.Combine(Root, "right");

    protected TestWorkspace()
    {
        Directory.CreateDirectory(Left);
        Directory.CreateDirectory(Right);
    }

    protected static string Write(string root, string relative, string content = "data", int seconds = 0)
    {
        string path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, Baseline.AddSeconds(seconds));
        return path;
    }

    protected MainViewModel CreateViewModel()
    {
        return new MainViewModel(new FolderHistory(Path.Combine(Root, "history.json")),
            new ExclusionList(Path.Combine(Root, "exclusions.json")), new RunLogStore(Path.Combine(Root, "reports")))
            { LeftPath = Left, RightPath = Right };
    }

    public void Dispose()
    {
        Directory.Delete(Root, recursive: true);
        GC.SuppressFinalize(this);
    }
}
