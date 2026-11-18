namespace XtraSync.Tests;

public sealed class FailureHandlingTests : TestWorkspace
{
    private MirrorPlan Plan(FaultFileSystem fs, MirrorMode mode = MirrorMode.Right, OperationControl? control = null) =>
        FolderService.Plan(Left, Right, mode, new(ErrorPolicy: fs.Policy()), control);

    [Fact]
    public void LockedSourceRecoversAfterTwoRetriesAndIndependentWorkCompletes()
    {
        string bad = Write(Left, "bad", "12345");
        Write(Left, "good", "123");
        var fs = new FaultFileSystem { OpenFault = (path, count) => path == bad && count < 3 ? FaultFileSystem.Io(32) : null };
        using var control = new OperationControl();
        var result = FolderService.Execute(Plan(fs), control);
        Assert.Equal(2, result.Applied.CopyFiles);
        Assert.Equal(2, result.Problems.Retries);
        Assert.Equal(1, result.Problems.Recovered);
        Assert.False(result.Problems.HasIssues);
        Assert.Equal("12345", File.ReadAllText(Path.Combine(Right, "bad")));
        Assert.Equal(100, control.Snapshot.Percentage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IsolatedInaccessibleOrMissingSourcePreservesDestinationAndContinues(bool missing)
    {
        string bad = Write(Left, "bad", "12345", 10);
        Write(Right, "bad", "original");
        Write(Left, "good", "123");
        var fs = new FaultFileSystem
        {
            OpenFault = (path, _) => path == bad
            ? missing ? new FileNotFoundException("missing") : new UnauthorizedAccessException("denied") : null
        };
        var result = FolderService.Execute(Plan(fs));
        Assert.Equal(1, result.Problems.FailedFiles);
        Assert.Equal(5, result.Problems.FailedBytes);
        Assert.Equal(missing ? 1 : 0, result.Problems.MissingFiles);
        Assert.Equal(missing ? 0 : 1, result.Problems.InaccessibleFiles);
        Assert.Equal(1, result.Applied.CopyFiles);
        Assert.Equal(1, fs.Count("open", bad));
        Assert.Equal("original", File.ReadAllText(Path.Combine(Right, "bad")));
        Assert.Equal("123", File.ReadAllText(Path.Combine(Right, "good")));
        Assert.Empty(Directory.GetFiles(Right, "*.tmp"));
    }

    [Fact]
    public void MissingSourceDuringRevalidationIsAnItemFailureNotAnAuthorizationToDeleteItsTarget()
    {
        string missing = Write(Left, "missing", "12345", 10);
        Write(Right, "missing", "keep");
        Write(Left, "good");
        var fs = new FaultFileSystem();
        var plan = Plan(fs);
        File.Delete(missing);
        var result = FolderService.Execute(plan);
        Assert.Equal(1, result.Problems.MissingFiles);
        Assert.Equal(1, result.Applied.CopyFiles);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(Right, "missing")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AlreadyAbsentDeletesAreNotCountedAsDeletedFilesOrBytes(bool folder)
    {
        string target = folder ? Directory.CreateDirectory(Path.Combine(Right, "gone")).FullName : Write(Right, "gone", "12345");
        Write(Left, "good");
        var fs = new FaultFileSystem();
        var plan = Plan(fs);
        if (folder) Directory.Delete(target); else File.Delete(target);
        var result = FolderService.Execute(plan);
        Assert.Equal(folder ? 0 : 1, result.Problems.AlreadyAbsentFiles);
        Assert.Equal(folder ? 0 : 5, result.Problems.AlreadyAbsentBytes);
        Assert.Equal(folder ? 1 : 0, result.Problems.UnavailableEntries);
        Assert.Equal(0, result.Applied.DeleteFiles);
        Assert.Equal(0, result.Applied.DeleteBytes);
        Assert.Equal(1, result.Applied.CopyFiles);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeleteRaceAfterTheEntryCheckIsAlsoReportedAsAlreadyAbsent(bool folder)
    {
        string target = folder ? Directory.CreateDirectory(Path.Combine(Right, "gone")).FullName : Write(Right, "gone", "12345");
        var fs = new FaultFileSystem();
        if (folder) fs.DeleteFolderFault = (path, _) => { Directory.Delete(path); return new DirectoryNotFoundException(); };
        else fs.DeleteFileFault = (path, _) => path == target ? DeleteExternally(path) : null;
        var result = FolderService.Execute(Plan(fs));
        Assert.Equal(folder ? 0 : 1, result.Problems.AlreadyAbsentFiles);
        Assert.Equal(folder ? 1 : 0, result.Problems.AlreadyAbsentFolders);
        Assert.Equal(0, result.Applied.DeleteFiles + result.Applied.DeleteFolders);
        Assert.False(result.Problems.HasIssues);
    }

    private static Exception DeleteExternally(string path) { File.Delete(path); return FaultFileSystem.Io(2); }

    [Fact]
    public void RetryingACommitCountsRetransferredBytesButCommitsOneCopy()
    {
        Write(Left, "file", "12345", 10);
        Write(Right, "file", "old");
        var fs = new FaultFileSystem { MoveFault = (_, count) => count < 3 ? FaultFileSystem.Io(32) : null };
        using var control = new OperationControl();
        var result = FolderService.Execute(Plan(fs), control);
        Assert.Equal(2, result.Problems.Retries);
        Assert.Equal(1, result.Problems.Recovered);
        Assert.Equal(1, result.Applied.CopyFiles);
        Assert.Equal(1, result.Applied.ReplaceFiles);
        Assert.Equal(5, result.Applied.CopyBytes);
        Assert.Equal(15, control.Snapshot.Bytes);
        Assert.Equal("12345", File.ReadAllText(Path.Combine(Right, "file")));
        Assert.Empty(Directory.GetFiles(Right, "*.tmp"));
    }

    [Fact]
    public void FailedChildDeletionBlocksItsParentDeletionAndReplacementButNotUnrelatedCopies()
    {
        Write(Left, "parent", "replacement");
        string child = Write(Right, "parent/keep", "original");
        Write(Left, "good");
        var fs = new FaultFileSystem { DeleteFileFault = (path, _) => path == Path.GetFullPath(child) ? new UnauthorizedAccessException() : null };
        var result = FolderService.Execute(Plan(fs));
        Assert.Equal("original", File.ReadAllText(child));
        Assert.True(File.Exists(Path.Combine(Right, "good")));
        Assert.Equal(1, result.Problems.FailedFiles);
        Assert.Equal(1, result.Problems.SkippedFolders);
        Assert.Equal(1, result.Problems.SkippedFiles);
        Assert.Equal(0, result.Applied.DeleteFolders);
        Assert.Empty(Directory.GetFiles(Right, "*.tmp"));
    }

    [Fact]
    public void FailedFolderCreationSkipsDescendantCopiesWithoutOpeningTheirSources()
    {
        string child = Write(Left, "new/child");
        Write(Left, "good");
        string folder = Path.Combine(Right, "new");
        var fs = new FaultFileSystem { CreateFault = (path, _) => path == folder ? new UnauthorizedAccessException() : null };
        var result = FolderService.Execute(Plan(fs));
        Assert.Equal(1, result.Problems.FailedFolders);
        Assert.Equal(1, result.Problems.SkippedFiles);
        Assert.Equal(1, result.Applied.CopyFiles);
        Assert.Equal(0, fs.Count("open", child));
        Assert.True(File.Exists(Path.Combine(Right, "good")));
    }

    [Fact]
    public void UnreadableReplacementSourceDoesNotDestroyTheOldDestinationTree()
    {
        string source = Write(Left, "parent", "replacement");
        string old = Write(Right, "parent/keep", "keep");
        Write(Left, "good");
        var fs = new FaultFileSystem { OpenFault = (path, _) => path == source ? FaultFileSystem.Io(32) : null };
        var result = FolderService.Execute(Plan(fs));
        Assert.Equal(1, result.Problems.FailedFiles);
        Assert.Equal(1, result.Problems.SkippedFiles);
        Assert.Equal(1, result.Problems.SkippedFolders);
        Assert.Equal(0, result.Applied.DeleteFiles);
        Assert.Equal("keep", File.ReadAllText(old));
        Assert.True(File.Exists(Path.Combine(Right, "good")));
    }

    [Fact]
    public void ANewChildPreventsFolderDeletionWithoutStoppingIndependentWork()
    {
        Directory.CreateDirectory(Path.Combine(Right, "orphan"));
        Write(Left, "good");
        var fs = new FaultFileSystem();
        using var control = new OperationControl(update =>
        {
            if (update.Stage == OperationStage.Executing && update.Path.Length == 0) Write(Right, "orphan/new", "preserve");
        });
        var result = FolderService.Execute(Plan(fs), control);
        Assert.Equal(1, result.Problems.FailedFolders);
        Assert.Equal(1, result.Applied.CopyFiles);
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(Right, "orphan/new")));
    }

    [Theory]
    [InlineData(MirrorMode.Right)]
    [InlineData(MirrorMode.Left)]
    [InlineData(MirrorMode.Both)]
    public void UnknownSubtreeIsNeverTreatedAsEmptyInAnyMode(MirrorMode mode)
    {
        string unreadableRoot = mode == MirrorMode.Left ? Right : Left;
        string otherRoot = mode == MirrorMode.Left ? Left : Right;
        string secret = Write(unreadableRoot, "unknown/source", "secret");
        string keep = Write(otherRoot, "unknown/target", "preserve");
        Write(unreadableRoot, "good");
        string unreadable = Path.GetDirectoryName(secret)!;
        var fs = new FaultFileSystem { EnumerationFault = (path, _) => path == unreadable ? new UnauthorizedAccessException() : null };
        using var control = new OperationControl();
        var plan = Plan(fs, mode, control);
        Assert.Contains("unknown", plan.UnavailablePaths!);
        Assert.DoesNotContain(plan.Actions, action => action.RelativePath.StartsWith("unknown"));
        Assert.Contains(plan.Conflicts!, conflict => conflict.Contains("unavailable scope"));
        FolderService.Execute(plan, control);
        Assert.Equal("preserve", File.ReadAllText(keep));
        Assert.Equal("secret", File.ReadAllText(secret));
        Assert.True(File.Exists(Path.Combine(otherRoot, "good")));
        Assert.Equal(1, control.Problems.UnavailableEntries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedAttributesOrMetadataProduceUnavailableRowsInsteadOfFalseOrphans(bool metadata)
    {
        string file = Write(Left, "file", "source");
        Write(Right, "file", "target");
        var fs = new FaultFileSystem();
        if (metadata) fs.LengthFault = (path, _) => path == file ? new FileNotFoundException() : null;
        else fs.AttributeFault = (path, _) => path == file ? new UnauthorizedAccessException() : null;
        using var control = new OperationControl();
        var rows = FolderService.Compare(Left, Right, new(ErrorPolicy: fs.Policy()), control);
        Assert.Equal("Unavailable", Assert.Single(rows).Status);
        Assert.Equal("Unavailable", rows[0].LeftSize);
        Assert.Equal(1, control.Problems.UnavailableEntries);
        var plan = Plan(fs);
        Assert.Empty(plan.Actions);
    }

    [Fact]
    public void APartiallyEnumeratedRootIsUnknownAndCannotAuthorizeAnyWrites()
    {
        Write(Left, "first");
        Write(Right, "preserve");
        var fs = new FaultFileSystem();
        IEnumerable<string> BrokenListing(string root)
        {
            yield return Path.Combine(root, "first");
            throw FaultFileSystem.Io(12345);
        }
        fs.Enumeration = path => path == Left ? BrokenListing(path) : Directory.EnumerateFileSystemEntries(path);
        var plan = Plan(fs);
        Assert.Contains("", plan.UnavailablePaths!);
        Assert.Empty(plan.Actions);
        FolderService.Execute(plan);
        Assert.True(File.Exists(Path.Combine(Right, "preserve")));
        Assert.Contains(FolderService.Compare(Left, Right, new(ErrorPolicy: fs.Policy())), row => row.RelativePath == "(root)" && row.Status == "Unavailable");
    }

    [Fact]
    public void ASubtreeBecomingUnreadableAfterPreviewSkipsItsActionsButStillCopiesIndependentFiles()
    {
        Write(Left, "unknown/file");
        Write(Left, "good");
        var fs = new FaultFileSystem();
        var plan = Plan(fs);
        fs.EnumerationFault = (path, _) => path == Path.Combine(Left, "unknown") ? new UnauthorizedAccessException() : null;
        var result = FolderService.Execute(plan);
        Assert.Equal(1, result.Problems.UnavailableEntries);
        Assert.Equal(1, result.Problems.SkippedFiles);
        Assert.Equal(1, result.Problems.SkippedFolders);
        Assert.Equal(1, result.Applied.CopyFiles);
        Assert.False(Directory.Exists(Path.Combine(Right, "unknown")));
    }

    [Fact]
    public void ADisappearedSourceDirectoryDoesNotTriggerDeletionOfItsTargetSubtree()
    {
        Write(Left, "unknown/file");
        string keep = Write(Right, "unknown/keep");
        Write(Left, "good");
        var fs = new FaultFileSystem();
        var plan = Plan(fs);
        Directory.Delete(Path.Combine(Left, "unknown"), true);
        var result = FolderService.Execute(plan);
        Assert.True(File.Exists(keep));
        Assert.Equal(1, result.Problems.UnavailableEntries);
        Assert.Equal(2, result.Problems.SkippedFiles);
        Assert.Equal(1, result.Applied.CopyFiles);
    }

    [Fact]
    public void ASourceChangingDuringCopyAbortsBeforeReplacingTheDestination()
    {
        string source = Write(Left, "file", "source", 10);
        Write(Right, "file", "original");
        var fs = new FaultFileSystem { OpenFault = (path, _) => { if (path == source) File.AppendAllText(path, "changed"); return null; } };
        var failure = Assert.Throws<RunAbortedException>(() => FolderService.Execute(Plan(fs)));
        Assert.Equal(ErrorCategory.StalePlan, failure.Category);
        Assert.Equal("original", File.ReadAllText(Path.Combine(Right, "file")));
        Assert.Empty(Directory.GetFiles(Right, "*.tmp"));
    }

    [Fact]
    public void AnUnexpectedDestinationAppearingAfterValidationIsNeverOverwritten()
    {
        Write(Left, "file", "source");
        var fs = new FaultFileSystem();
        using var control = new OperationControl(update =>
        {
            if (update.Stage == OperationStage.Executing && update.Path.Length == 0) Write(Right, "file", "external");
        });
        Assert.Equal(ErrorCategory.StalePlan, Assert.Throws<RunAbortedException>(() => FolderService.Execute(Plan(fs), control)).Category);
        Assert.Equal("external", File.ReadAllText(Path.Combine(Right, "file")));
    }

    [Fact]
    public void StorageFailureStopsTheRunWithoutTryingTheNextFile()
    {
        string bad = Write(Left, "a", "source");
        string later = Write(Left, "later", "source");
        var fs = new FaultFileSystem { OpenFault = (path, _) => path == bad ? FaultFileSystem.Io(112) : null };
        using var control = new OperationControl();
        Assert.Equal(ErrorCategory.Storage, Assert.Throws<RunAbortedException>(() => FolderService.Execute(Plan(fs), control)).Category);
        Assert.Equal(0, fs.Count("open", later));
        Assert.Equal(1, control.Problems.FailedFiles);
        Assert.Equal(0, control.Problems.Retries);
        Assert.Empty(Directory.GetFiles(Right));
    }

    [Fact]
    public void CleanupFailuresAreWarningsAndReservedTemporaryFilesAreNeverSynchronized()
    {
        Write(Left, "file", "source");
        var fs = new FaultFileSystem
        {
            MoveFault = (_, _) => new UnauthorizedAccessException(),
            DeleteFileFault = (path, _) => path.Contains(".xtrasync-", StringComparison.Ordinal) ? new UnauthorizedAccessException() : null
        };
        var result = FolderService.Execute(Plan(fs));
        Assert.Equal(1, result.Problems.FailedFiles);
        Assert.Equal(1, result.Problems.Warnings);
        string leftover = Assert.Single(Directory.GetFiles(Right));
        Assert.Contains(".xtrasync-", leftover);
        Assert.Empty(FolderService.Scan(Right));
        Assert.DoesNotContain(FolderService.Plan(Left, Right, MirrorMode.Both, new()).Actions, action => action.RelativePath.Contains(".xtrasync-"));
    }
}
