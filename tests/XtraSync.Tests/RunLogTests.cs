using System.Globalization;

namespace XtraSync.Tests;

public sealed class RunLogTests : TestWorkspace
{
    private static readonly RunContext Context = new("Mirror to right", "left", "right", "*.tmp", "Subfolders=true");
    private RunLogStore Store => new(Path.Combine(Root, "reports"), new ReportClock());

    [Fact]
    public void ThirdRunReplacesOnlyThePreviousBackupAndLeavesUnrelatedFilesAlone()
    {
        var store = Store;
        Directory.CreateDirectory(store.DirectoryPath);
        Write(store.DirectoryPath, "other.log", "unrelated");
        using (var first = store.Start(Context)) { first.Info("FIRST RUN"); first.Complete("Completed", false); }
        Assert.False(File.Exists(store.BackupPath));
        using (var second = store.Start(Context))
        {
            Assert.Contains("FIRST RUN", File.ReadAllText(store.BackupPath));
            Assert.DoesNotContain("FIRST RUN", ReadLive(store.LogPath));
            second.Info("SECOND RUN"); second.Complete("Completed", false);
        }
        using (var third = store.Start(Context))
        {
            Assert.Contains("SECOND RUN", File.ReadAllText(store.BackupPath));
            Assert.DoesNotContain("FIRST RUN", File.ReadAllText(store.BackupPath));
            third.Info("THIRD RUN"); third.Complete("Stopped", false);
        }
        Assert.Contains("THIRD RUN", File.ReadAllText(store.LogPath));
        Assert.Equal(2, Directory.GetFiles(store.DirectoryPath, "XtraSync*.log").Length);
        Assert.Equal("unrelated", File.ReadAllText(Path.Combine(store.DirectoryPath, "other.log")));
        Assert.False(File.Exists(store.LockPath));
    }

    [Fact]
    public void ConcurrentRunCannotRotateAnActiveLogOrRemoveItsBackup()
    {
        var store = Store;
        using (var previous = store.Start(Context)) { previous.Info("previous"); previous.Complete("Completed", false); }
        using (var current = store.Start(Context))
        {
            current.Info("current");
            Assert.Throws<IOException>(() => Store.Start(Context));
            Assert.Contains("current", ReadLive(store.LogPath));
            Assert.Contains("previous", File.ReadAllText(store.BackupPath));
            current.Complete("Completed", false);
        }
        using var next = store.Start(Context);
        next.Complete("Completed", false);
    }

    [Fact]
    public void RotationFailureReleasesTheRunLock()
    {
        var store = Store;
        using (var first = store.Start(Context)) first.Complete("Completed", false);
        Directory.CreateDirectory(store.BackupPath);
        Assert.Throws<UnauthorizedAccessException>(() => store.Start(Context));
        Assert.False(File.Exists(store.LockPath));
        Assert.True(File.Exists(store.LogPath));
        Directory.Delete(store.BackupPath);
        using var retry = store.Start(Context);
        retry.Complete("Completed", false);
    }

    [Fact]
    public void ConstructorFailureReleasesTheRunLock()
    {
        var store = Store;
        Directory.CreateDirectory(store.LogPath);
        Assert.Throws<UnauthorizedAccessException>(() => store.Start(Context));
        Assert.False(File.Exists(store.LockPath));
    }

    [Fact]
    public void JournalContainsEveryDiagnosticAndEndsWithCompleteStatistics()
    {
        var store = Store;
        using (var run = store.Start(Context))
        {
            run.Warning("first warning");
            run.Warning("second warning");
            run.Error("copy failed", new IOException("source locked"));
            run.Observe(new("Scanning folders", "file", 12, 3, 100, 0, 0));
            run.Observe(new("Scanning folders", "last", 14, 5, 120, 0, 0));
            run.Observe(new("Mirroring", "partial", 3, 1, 75, 4, 8, OperationStage.Executing,
                new(5, 100, 2, 25, 1, 1, 2, 10), new(2, 50, 1, 15, 1, 0, 1, 5)));
            run.Observe(new("Comparing entries", "last", 4, 2, 90, 3, 3, OperationStage.Comparing,
                EqualFiles: 2, DifferentItems: 1));
            run.Complete("Stopped after partial work", false);
        }
        string log = File.ReadAllText(store.LogPath);
        Assert.Contains("[WARNING] first warning", log);
        Assert.Contains("[WARNING] second warning", log);
        Assert.Contains("[ERROR] copy failed", log);
        Assert.Contains("System.IO.IOException: source locked", log);
        Assert.Contains("Files copied / planned: 2 / 5", log);
        Assert.Contains("Files deleted / planned: 1 / 2", log);
        Assert.Contains("Files replaced / planned: 1 / 2", log);
        Assert.Contains("Folders created / planned: 1 / 1", log);
        Assert.Contains("Folders deleted / planned: 0 / 1", log);
        Assert.Contains("(75 bytes)", log);
        Assert.Contains("Scanning folders: files / folders: 14 / 5", log);
        Assert.Contains("Comparing entries: equal files / differences: 2 / 1", log);
        Assert.Contains("Warnings: 2", log);
        Assert.Contains("Errors: 1", log);
        Assert.Equal(1, log.Split("Phase: Scanning folders").Length - 1);
        Assert.EndsWith("=== END RUN SUMMARY ===", log.TrimEnd());
    }

    [Fact]
    public void TimestampedReportsUseLocalTimeAndDoNotOverwriteSameSecondReports()
    {
        var clock = new ReportClock();
        var store = new RunLogStore(Path.Combine(Root, "reports"), clock);
        var paths = new List<string>();
        for (int index = 0; index < 3; index++)
        {
            using var run = store.Start(Context);
            var result = run.Complete("Completed", true);
            Assert.Null(result.ReportError);
            paths.Add(result.HtmlPath!);
        }
        Assert.Equal("XtraSync-2026-11-18-15-15-16.html", Path.GetFileName(paths[0]));
        Assert.Equal("XtraSync-2026-11-18-15-15-16-2.html", Path.GetFileName(paths[1]));
        Assert.Equal("XtraSync-2026-11-18-15-15-16-3.html", Path.GetFileName(paths[2]));
        Assert.All(paths, path => Assert.True(File.Exists(path)));
        Assert.Equal(3, Directory.GetFiles(store.DirectoryPath, "*.html").Length);
        Assert.Empty(Directory.GetFiles(store.DirectoryPath, "*.tmp"));
    }

    [Fact]
    public void TimestampNamesRemainGregorianUnderOtherCultures()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            var store = Store;
            using var run = store.Start(Context);
            Assert.Equal("XtraSync-2026-11-18-15-15-16.html", Path.GetFileName(run.Complete("Completed", true).HtmlPath));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void HtmlEscapesPathsOptionsAndErrorsAndContainsNoExternalDependencies()
    {
        var store = Store;
        using var run = store.Start(Context with { Left = "<script>alert('left')</script>", Exclusions = "a&b\"" });
        run.Warning("<img src=x onerror=alert(1)>");
        run.Error("<script>bad()</script>", new IOException("denied <tag>"));
        string path = run.Complete("Failed <error>", true).HtmlPath!;
        string html = File.ReadAllText(path);
        Assert.Contains("<!doctype html>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("a&amp;b&quot;", html);
        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("<img ", html);
        Assert.DoesNotContain("https://", html);
        Assert.Contains("Files copied / planned", html);
        Assert.Contains("Warnings", html);
        Assert.Contains("Errors", html);
        Assert.EndsWith("</body></html>", html);
    }

    [Fact]
    public void HtmlFailureIsLoggedAndTheTextSummaryIsStillLast()
    {
        var store = Store;
        Directory.CreateDirectory(store.DirectoryPath);
        Directory.CreateDirectory(Path.Combine(store.DirectoryPath, "XtraSync-2026-11-18-15-15-16.html.tmp"));
        using (var run = store.Start(Context))
        {
            var result = run.Complete("Completed", true);
            Assert.Null(result.HtmlPath);
            Assert.StartsWith("HTML report could not be saved:", result.ReportError);
        }
        string log = File.ReadAllText(store.LogPath);
        Assert.Contains("[ERROR] HTML report could not be saved:", log);
        Assert.Contains("Errors: 1", log);
        Assert.EndsWith("=== END RUN SUMMARY ===", log.TrimEnd());
    }

    [Fact]
    public void DefaultOutputDirectoryUsesTheExecutableRatherThanTheBundleExtractionDirectory()
    {
        Assert.Equal(Path.GetDirectoryName(Environment.ProcessPath), RunLogStore.ForExecutable().DirectoryPath);
    }

    [Theory]
    [InlineData("XtraSync.log", true)]
    [InlineData("XtraSync.bak.log", true)]
    [InlineData("XtraSync.run.lock", true)]
    [InlineData("XtraSync-help.html", true)]
    [InlineData("XtraSync-2026-11-18-15-15-16.html", true)]
    [InlineData("XtraSync-2026-11-18-15-15-16-2.html.tmp", true)]
    [InlineData("XtraSync-notes.html", false)]
    [InlineData("other.log", false)]
    public void OnlyKnownArtifactsInTheActualOutputDirectoryAreExcluded(string name, bool expected)
    {
        var store = new RunLogStore(Root + Path.DirectorySeparatorChar);
        Assert.Equal(expected, store.IsArtifact(Path.Combine(Root, name)));
        Assert.False(store.IsArtifact(Path.Combine(Left, name)));
    }

    private sealed class ReportClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 18, 14, 15, 16, TimeSpan.Zero);
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.CreateCustomTimeZone("ReportTest", TimeSpan.FromHours(1), "ReportTest", "ReportTest");
    }

    private static string ReadLive(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
