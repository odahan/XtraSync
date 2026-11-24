namespace XtraSync.Tests;

public sealed class FailurePolicyIntegrationTests : TestWorkspace
{
    private MainViewModel Model(FaultFileSystem fs) => new(new FolderHistory(Path.Combine(Root, "history.json")),
        new ExclusionList(Path.Combine(Root, "exclusions.json")), new RunLogStore(Path.Combine(Root, "reports")), fs.Policy())
    { LeftPath = Left, RightPath = Right, GenerateHtmlReport = true, ConfirmMirror = _ => true };

    [Fact]
    public async Task CompletedWithIssuesIsShownAndRecordedInBothReports()
    {
        string bad = Write(Left, "bad", "12345", 10);
        Write(Right, "bad", "original");
        Write(Left, "good", "123");
        var fs = new FaultFileSystem { OpenFault = (path, _) => path == bad ? new UnauthorizedAccessException() : null };
        var model = Model(fs);
        await model.MirrorRightCommand.ExecuteAsync(null);
        Assert.StartsWith("Completed with issues:", model.Status);
        Assert.Contains("1 failed files", model.Status);
        Assert.Contains("See the log", model.Status);
        Assert.Equal(1, model.Problems.InaccessibleFiles);
        Assert.Contains("1 failed files", model.ProblemSummary);
        Assert.Equal(1, model.AppliedStatistics.CopyFiles);
        Assert.False(model.GenerateHtmlReport);
        Assert.Equal(100, model.ProgressValue);
        string log = File.ReadAllText(Path.Combine(Root, "reports", "XtraSync.log"));
        Assert.Contains("[ERROR] Operation failed", log);
        Assert.Contains("Failed files / folders: 1 / 0", log);
        Assert.Contains("Inaccessible or locked files: 1", log);
        Assert.Contains("Outcome: Completed with issues:", log);
        string html = File.ReadAllText(Assert.Single(Directory.GetFiles(Path.Combine(Root, "reports"), "*.html")));
        Assert.Contains("Completed with issues:", html);
        Assert.Contains("Inaccessible or locked files", html);
        Assert.Equal("original", File.ReadAllText(Path.Combine(Right, "bad")));
    }

    [Fact]
    public async Task RecoveredFailuresFinishWithWarningsInsteadOfUnqualifiedSuccess()
    {
        string source = Write(Left, "file");
        var fs = new FaultFileSystem { OpenFault = (path, count) => path == source && count < 3 ? FaultFileSystem.Io(32) : null };
        var model = Model(fs);
        await model.MirrorRightCommand.ExecuteAsync(null);
        Assert.Contains("Completed with warnings:", model.Status);
        Assert.Equal(2, model.Problems.Retries);
        Assert.Equal(1, model.Problems.Recovered);
        Assert.False(model.Problems.HasIssues);
        string log = File.ReadAllText(Path.Combine(Root, "reports", "XtraSync.log"));
        Assert.DoesNotContain("[ERROR]", log);
        Assert.Contains("Retry attempts / recovered operations: 2 / 1", log);
    }

    [Fact]
    public async Task UnknownScanScopesAreVisibleInTheReviewAndFinalSummary()
    {
        Write(Left, "unknown/file");
        string keep = Write(Right, "unknown/keep");
        Write(Left, "good");
        string inaccessible = Path.Combine(Left, "unknown");
        var fs = new FaultFileSystem { EnumerationFault = (path, _) => path == inaccessible ? new UnauthorizedAccessException() : null };
        var model = Model(fs);
        model.CompareAfterMirroring = true;
        bool reviewed = false;
        model.ConfirmMirror = plan =>
        {
            reviewed = true;
            Assert.Contains(plan.Conflicts!, conflict => conflict.Contains("unavailable scope"));
            Assert.DoesNotContain(plan.Actions, action => action.RelativePath.Contains("unknown"));
            return true;
        };
        await model.MirrorRightCommand.ExecuteAsync(null);
        Assert.True(reviewed);
        Assert.StartsWith("Completed with issues", model.Status);
        Assert.Equal(1, model.Problems.UnavailableEntries);
        Assert.Equal(1, model.Problems.Conflicts);
        Assert.True(File.Exists(keep));
        Assert.Contains(model.Rows, row => row.Status == "Unavailable");
    }

    [Fact]
    public async Task StorageFailureStopsAndProducesAnInterruptedSummary()
    {
        string source = Write(Left, "a");
        Write(Left, "later");
        var fs = new FaultFileSystem { OpenFault = (path, _) => path == source ? FaultFileSystem.Io(112) : null };
        var model = Model(fs);
        await model.MirrorRightCommand.ExecuteAsync(null);
        Assert.StartsWith("Stopped:", model.Status);
        Assert.False(model.GenerateHtmlReport);
        Assert.Equal(1, model.Problems.FailedFiles);
        Assert.Equal(0, model.AppliedStatistics.CopyFiles);
        Assert.Contains("Storage", File.ReadAllText(Path.Combine(Root, "reports", "XtraSync.log")));
    }

    [Fact]
    public void RootDisappearanceBeforeADeletionStopsWithoutDeletingAnything()
    {
        string keep = Write(Right, "orphan", "preserve");
        var fs = new FaultFileSystem();
        var plan = FolderService.Plan(Left, Right, MirrorMode.Right, new(ErrorPolicy: fs.Policy()));
        using var control = new OperationControl(progress =>
        {
            if (progress.Stage == OperationStage.Executing && progress.Path.Length == 0)
                fs.ProbeFault = (path, _) => path == Left ? new DirectoryNotFoundException("disconnected") : null;
        });
        Assert.Equal(ErrorCategory.RootUnavailable, Assert.Throws<RunAbortedException>(() => FolderService.Execute(plan, control)).Category);
        Assert.Equal("preserve", File.ReadAllText(keep));
        Assert.Equal(0, control.AppliedStatistics.DeleteFiles);
    }

    [Fact]
    public void DestinationChangesWhileCopyingAreDetectedBeforeCommit()
    {
        string source = Write(Left, "file", "source", 10);
        string target = Write(Right, "file", "original");
        var fs = new FaultFileSystem
        {
            OpenFault = (path, _) => { if (path == source) File.AppendAllText(target, " external edit"); return null; }
        };
        var plan = FolderService.Plan(Left, Right, MirrorMode.Right, new(ErrorPolicy: fs.Policy()));
        Assert.Equal(ErrorCategory.StalePlan, Assert.Throws<RunAbortedException>(() => FolderService.Execute(plan)).Category);
        Assert.Equal("original external edit", File.ReadAllText(target));
        Assert.Empty(Directory.GetFiles(Right, "*.tmp"));
    }

    [Fact]
    public void RootProbeRejectsAFileAndALinkInsteadOfTreatingThemAsAvailableRoots()
    {
        string file = Write(Left, "file");
        Assert.Throws<DirectoryNotFoundException>(() => new FileSystemAccess().ProbeRoot(file));
        Assert.Equal(ErrorCategory.Safety, Assert.Throws<RunAbortedException>(() => new LinkedRootFileSystem().ProbeRoot(Left)).Category);
    }

    private sealed class LinkedRootFileSystem : FileSystemAccess
    {
        public override FileAttributes Attributes(string path) => FileAttributes.Directory | FileAttributes.ReparsePoint;
    }
}
