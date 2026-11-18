namespace XtraSync.Tests;

public sealed class FolderServiceTests : TestWorkspace
{
    [Theory]
    [InlineData(false, false, "Equal")]
    [InlineData(true, false, "Different size")]
    [InlineData(false, true, "Right newer")]
    [InlineData(true, true, "Right newer")]
    public void StatusHonorsOptionsAndTimestampPrecedence(bool size, bool time, string expected)
    {
        var left = new Entry("file", false, 1, Baseline.ToFileTimeUtc());
        var right = new Entry("file", false, 2, Baseline.AddSeconds(10).ToFileTimeUtc());
        Assert.Equal(expected, FolderService.Status(left, right, new(size, time)));
        Assert.Equal("Left newer", FolderService.Status(right, left, new()));
        Assert.Equal("Left only", FolderService.Status(left, null, new()));
        Assert.Equal("Right only", FolderService.Status(null, right, new()));
        Assert.Equal("Type conflict", FolderService.Status(left, right with { IsDirectory = true }, new()));
        Assert.Equal("Folder", FolderService.Status(left with { IsDirectory = true }, right with { IsDirectory = true }, new()));
        Assert.Equal("Equal", FolderService.Status(left, left, new()));
    }

    [Fact]
    public void StatusCanIgnoreWholeHourDifferences()
    {
        var left = new Entry("file", false, 1, Baseline.ToFileTimeUtc());
        Assert.Equal("Equal", FolderService.Status(left, left with { FileTime = Baseline.AddHours(1).ToFileTimeUtc() },
            new(IgnoreTimeZone: true)));
    }

    [Fact]
    public void RootsAreNormalizedAndMustExistWithoutOverlap()
    {
        Assert.Equal((Left, Right), FolderService.ValidateRoots(Left + "\\", Right + "\\"));
        Assert.ThrowsAny<IOException>(() => FolderService.ValidateRoots(Left, Left.ToUpperInvariant()));
        Assert.ThrowsAny<IOException>(() => FolderService.ValidateRoots(Left, Root));
        Assert.ThrowsAny<IOException>(() => FolderService.ValidateRoots(Root, Right));
        Assert.ThrowsAny<IOException>(() => FolderService.ValidateRoots(Left, Path.Combine(Root, "missing")));
        Assert.ThrowsAny<IOException>(() => FolderService.ValidateRoots(Path.Combine(Root, "missing"), Right));
    }

    [Fact]
    public void ScanIncludesEmptyDirectoriesAndTracksProgress()
    {
        Write(Left, "a.txt", "123");
        Write(Left, "nested/b.txt", "12345");
        Directory.CreateDirectory(Path.Combine(Left, "empty"));
        var updates = new List<OperationProgress>();
        using var control = new OperationControl(updates.Add);
        control.Begin("scan");
        var entries = FolderService.Scan(Left, control: control);
        Assert.Equal(4, entries.Count);
        Assert.Equal(3, entries["A.TXT"].Size);
        Assert.True(entries["empty"].IsDirectory);
        Assert.Equal(2, updates[^1].Files);
        Assert.Equal(2, updates[^1].Folders);
        Assert.Equal(8, updates[^1].Bytes);
        Assert.Single(FolderService.Scan(Left, includeSubfolders: false));
    }

    [Fact]
    public void ScanExcludesEntireTreesAndProtectsAllAncestors()
    {
        Write(Left, "a/b/secret.tmp");
        Write(Left, "cache/hidden.txt");
        Write(Left, "visible.txt");
        var protectedFolders = new HashSet<string>();
        var scan = FolderService.Scan(Left, exclusions: "*.tmp;cache", protectedFolders: protectedFolders);
        Assert.Equal(3, scan.Count);
        Assert.Contains("a", protectedFolders);
        Assert.Contains(Path.Combine("a", "b"), protectedFolders);
        Assert.DoesNotContain("cache", scan.Keys);
    }

    [Fact]
    public void CompareMergesCaseInsensitiveNamesSortsAndFormatsMissingSides()
    {
        Write(Left, "b.txt");
        Write(Right, "B.TXT");
        Write(Right, "a.txt");
        Directory.CreateDirectory(Path.Combine(Left, "z"));
        var rows = FolderService.Compare(Left, Right, new());
        Assert.Equal(["a.txt", "b.txt", "z"], rows.Select(row => row.RelativePath));
        Assert.Equal("Equal", rows[1].Status);
        Assert.Equal("—", rows[0].LeftSize);
        Assert.Equal("—", rows[0].LeftModified);
        Assert.Equal("Folder", rows[2].LeftSize);
        Assert.Equal("—", rows[2].RightModified);
        Assert.Equal(Baseline.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), rows[1].LeftModified);
    }

    [Theory]
    [InlineData(MirrorMode.Right)]
    [InlineData(MirrorMode.Left)]
    public void MirrorCopiesReplacesDeletesAndPreservesEmptyDirectories(MirrorMode mode)
    {
        string source = mode == MirrorMode.Right ? Left : Right;
        string target = mode == MirrorMode.Right ? Right : Left;
        Write(source, "new/deep/file.txt", "source");
        Write(source, "replace.txt", "old source", -10);
        Write(target, "replace.txt", "new target", 10);
        Write(target, "orphan/deep/file.txt");
        Directory.CreateDirectory(Path.Combine(source, "empty"));
        var plan = FolderService.Plan(Left, Right, mode, new());
        Assert.Equal(source, plan.Source);
        Assert.Equal(target, plan.Destination);
        Assert.Empty(plan.Conflicts!);
        FolderService.Execute(plan);
        Assert.Equal("old source", File.ReadAllText(Path.Combine(target, "replace.txt")));
        Assert.Equal("source", File.ReadAllText(Path.Combine(target, "new/deep/file.txt")));
        Assert.Equal(Baseline.AddSeconds(-10), File.GetLastWriteTimeUtc(Path.Combine(target, "replace.txt")));
        Assert.True(Directory.Exists(Path.Combine(target, "empty")));
        Assert.False(Directory.Exists(Path.Combine(target, "orphan")));
        Assert.All(FolderService.Compare(source, target, new()), row => Assert.Contains(row.Status, new[] { "Equal", "Folder" }));
        Assert.Empty(FolderService.Plan(Left, Right, mode, new()).Actions);
    }

    [Fact]
    public void MirrorReplacesBothKindsOfTypeConflict()
    {
        Write(Left, "file", "replacement");
        Write(Right, "file/deep/old");
        Write(Left, "folder/new");
        Write(Right, "folder");
        FolderService.Execute(FolderService.Plan(Left, Right, MirrorMode.Right, new()));
        Assert.Equal("replacement", File.ReadAllText(Path.Combine(Right, "file")));
        Assert.True(File.Exists(Path.Combine(Right, "folder/new")));
    }

    [Theory]
    [InlineData(MirrorMode.Left)]
    [InlineData(MirrorMode.Right)]
    [InlineData(MirrorMode.Both)]
    public void ShallowModeAndExclusionsApplyToComparisonPlanAndExecution(MirrorMode mode)
    {
        Write(Left, "left.txt");
        Write(Right, "right.txt");
        Write(Left, "nested/left.txt");
        Write(Right, "nested/right.txt");
        Write(Left, "secret.tmp", "left");
        Write(Right, "secret.tmp", "right");
        var settings = new ComparisonSettings(IncludeSubfolders: false, Exclusions: "*.tmp");
        Assert.Equal(2, FolderService.Compare(Left, Right, settings).Count);
        var plan = FolderService.Plan(Left, Right, mode, settings);
        Assert.All(plan.Actions, action => Assert.Contains(action.RelativePath, new[] { "left.txt", "right.txt" }));
        FolderService.Execute(plan);
        Assert.Equal("left", File.ReadAllText(Path.Combine(Left, "secret.tmp")));
        Assert.Equal("right", File.ReadAllText(Path.Combine(Right, "secret.tmp")));
        Assert.True(File.Exists(Path.Combine(Left, "nested/left.txt")));
        Assert.True(File.Exists(Path.Combine(Right, "nested/right.txt")));
    }

    [Fact]
    public void MirrorPreservesExcludedDescendantsAndTheirAncestors()
    {
        Write(Right, "orphan/deep/secret.tmp", "keep");
        Write(Right, "orphan/deep/delete.txt");
        FolderService.Execute(FolderService.Plan(Left, Right, MirrorMode.Right, new(Exclusions: "*.tmp")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(Right, "orphan/deep/secret.tmp")));
        Assert.False(File.Exists(Path.Combine(Right, "orphan/deep/delete.txt")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MirrorSkipsTypeReplacementWhenEitherFolderContainsExcludedItems(bool sourceIsFile)
    {
        string fileRoot = sourceIsFile ? Left : Right;
        string directoryRoot = sourceIsFile ? Right : Left;
        Write(fileRoot, "conflict", "keep file");
        Write(directoryRoot, "conflict/secret.tmp", "keep secret");
        Write(directoryRoot, "conflict/child.txt", "keep child");
        var plan = FolderService.Plan(Left, Right, MirrorMode.Right, new(Exclusions: "*.tmp"));
        Assert.Single(plan.Conflicts!);
        Assert.Empty(plan.Actions);
        FolderService.Execute(plan);
        Assert.Equal("keep file", File.ReadAllText(Path.Combine(fileRoot, "conflict")));
        Assert.Equal("keep secret", File.ReadAllText(Path.Combine(directoryRoot, "conflict/secret.tmp")));
        Assert.True(File.Exists(Path.Combine(directoryRoot, "conflict/child.txt")));
    }

    [Fact]
    public void BothWaysCopiesMissingAndNewerFilesWithoutDeleting()
    {
        Write(Left, "left/only.txt", "left");
        Write(Right, "right/only.txt", "right");
        Write(Left, "left-newer", "new-left", 10);
        Write(Right, "left-newer", "old");
        Write(Left, "right-newer", "old");
        Write(Right, "right-newer", "new-right", 10);
        Write(Left, "equal", "same");
        Write(Right, "equal", "same");
        var plan = FolderService.Plan(Left, Right, MirrorMode.Both, new());
        Assert.True(plan.BothWays);
        Assert.DoesNotContain(plan.Actions, action => action.Kind.StartsWith("Delete"));
        Assert.Contains(plan.Actions, action => action.Reverse && action.Direction == "Right → Left");
        Assert.Contains(plan.Actions, action => !action.Reverse && action.Direction == "Source → Destination");
        FolderService.Execute(plan);
        foreach (string root in new[] { Left, Right })
        {
            Assert.Equal("left", File.ReadAllText(Path.Combine(root, "left/only.txt")));
            Assert.Equal("right", File.ReadAllText(Path.Combine(root, "right/only.txt")));
            Assert.Equal("new-left", File.ReadAllText(Path.Combine(root, "left-newer")));
            Assert.Equal("new-right", File.ReadAllText(Path.Combine(root, "right-newer")));
        }
        Assert.Empty(FolderService.Plan(Left, Right, MirrorMode.Both, new()).Actions);
    }

    [Fact]
    public void BothWaysPreservesAmbiguousFilesAndBlocksTypeConflictDescendants()
    {
        Write(Left, "ambiguous", "short");
        Write(Right, "ambiguous", "much longer");
        Write(Left, "conflict/deep/child");
        Write(Right, "conflict");
        var plan = FolderService.Plan(Left, Right, MirrorMode.Both, new());
        Assert.Equal(2, plan.Conflicts!.Count);
        Assert.Empty(plan.Actions);
        FolderService.Execute(plan);
        Assert.Equal("short", File.ReadAllText(Path.Combine(Left, "ambiguous")));
        Assert.Equal("much longer", File.ReadAllText(Path.Combine(Right, "ambiguous")));
        Assert.True(File.Exists(Path.Combine(Left, "conflict/deep/child")));
    }

    [Theory]
    [InlineData(true, "add")]
    [InlineData(false, "add")]
    [InlineData(true, "remove")]
    [InlineData(false, "remove")]
    [InlineData(true, "size")]
    [InlineData(false, "size")]
    [InlineData(true, "timestamp")]
    [InlineData(false, "timestamp")]
    public void StalePlansFailBeforeAnyWrite(bool sourceChanges, string change)
    {
        Write(Left, "file", "left");
        Write(Right, "file", "right");
        var plan = FolderService.Plan(Left, Right, MirrorMode.Right, new());
        string changedRoot = sourceChanges ? Left : Right;
        string path = Path.Combine(changedRoot, "file");
        switch (change)
        {
            case "add": Write(changedRoot, "new"); break;
            case "remove": File.Delete(path); break;
            case "size": File.AppendAllText(path, "changed"); break;
            case "timestamp": File.SetLastWriteTimeUtc(path, Baseline.AddSeconds(20)); break;
        }
        if (change == "remove")
        {
            ExecutionResult result = FolderService.Execute(plan);
            Assert.Equal(sourceChanges ? 1 : 0, result.Problems.FailedFiles);
            Assert.Equal(sourceChanges ? 0 : 1, result.Applied.CopyFiles);
            return;
        }
        Assert.Contains("changed after preview", Assert.ThrowsAny<IOException>(() => FolderService.Execute(plan)).Message);
        Assert.Equal(sourceChanges ? "right" : "left", File.ReadAllText(Path.Combine(sourceChanges ? Right : Left, "file")));
    }

    [Fact]
    public void ExcludedChangesDoNotInvalidatePreview()
    {
        Write(Left, "copy.txt");
        var plan = FolderService.Plan(Left, Right, MirrorMode.Right, new(Exclusions: "*.tmp"));
        Write(Right, "ignored.tmp");
        FolderService.Execute(plan);
        Assert.True(File.Exists(Path.Combine(Right, "copy.txt")));
        Assert.True(File.Exists(Path.Combine(Right, "ignored.tmp")));
    }

    [Fact]
    public void CancellationPreventsScanningAndExecution()
    {
        Write(Left, "file");
        var plan = FolderService.Plan(Left, Right, MirrorMode.Right, new());
        using var control = new OperationControl();
        control.Stop();
        Assert.ThrowsAny<OperationCanceledException>(() => FolderService.Scan(Left, control: control));
        Assert.ThrowsAny<OperationCanceledException>(() => FolderService.Execute(plan, control));
        Assert.Empty(Directory.GetFiles(Right));
    }

    [Fact]
    public void CopyFailurePreservesDestinationAndCleansTemporarySibling()
    {
        string source = Write(Left, "file", "new data", 10);
        Write(Right, "file", "original");
        var plan = FolderService.Plan(Left, Right, MirrorMode.Right, new());
        using (var locked = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(1, FolderService.Execute(plan).Problems.FailedFiles);
        }
        Assert.Equal("original", File.ReadAllText(Path.Combine(Right, "file")));
        Assert.Single(Directory.GetFiles(Right));
    }

    [Fact]
    public void StoppingDuringCopyPreservesOriginalAndRemovesPartialFile()
    {
        string source = Write(Left, "file", "", 10);
        using (var stream = File.OpenWrite(source)) stream.SetLength(3 * 1024 * 1024);
        Write(Right, "file", "original");
        var plan = FolderService.Plan(Left, Right, MirrorMode.Right, new());
        var clock = new ManualTimeProvider();
        OperationControl? active = null;
        using var control = new OperationControl(update =>
        {
            clock.Advance(80);
            if (update.Phase == "Mirroring" && update.Bytes >= 1024 * 1024) active!.Stop();
        }, clock);
        active = control;
        Assert.ThrowsAny<OperationCanceledException>(() => FolderService.Execute(plan, control));
        Assert.Equal("original", File.ReadAllText(Path.Combine(Right, "file")));
        Assert.Single(Directory.GetFiles(Right));
        var fresh = FolderService.Plan(Left, Right, MirrorMode.Right, new());
        FolderService.Execute(fresh);
        Assert.Equal(new FileInfo(source).Length, new FileInfo(Path.Combine(Right, "file")).Length);
    }

    [Fact]
    public void ExecutionReportsPhasesOperationCountsAndBytes()
    {
        Write(Left, "file", "12345");
        var plan = FolderService.Plan(Left, Right, MirrorMode.Right, new());
        var updates = new List<OperationProgress>();
        using var control = new OperationControl(updates.Add);
        FolderService.Execute(plan, control);
        Assert.Contains(updates, update => update.Phase == "Validating preview");
        Assert.Equal(new OperationProgress("Mirroring", Path.Combine(Right, "file"), 1, 0, 5, 1, 1,
            OperationStage.Executing, new(CopyFiles: 1, CopyBytes: 5), new(CopyFiles: 1, CopyBytes: 5),
            Sequence: updates[^1].Sequence), updates[^1]);
        updates.Clear();
        Write(Right, "other");
        FolderService.Execute(FolderService.Plan(Left, Right, MirrorMode.Both, new()), control);
        Assert.Equal("Synchronizing", updates[^1].Phase);
    }
}
