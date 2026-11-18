namespace XtraSync.Tests;

public sealed class UpstreamFilterTests : TestWorkspace
{
    [Fact]
    public void TypedFiltersApplyOnlyToTheirEntryTypeAndPruneFolders()
    {
        Write(Left, "file.tmp");
        Write(Left, "file.tmp.folder/keep");
        Write(Left, "folder.tmp/keep");
        Write(Left, "bin/never-scanned.txt");
        Write(Left, "file-named-bin");
        Write(Left, "nested/file.tmp");
        var entries = FolderService.Scan(Left, fileExclusions: "*.tmp", folderExclusions: "bin");
        Assert.DoesNotContain("file.tmp", entries.Keys);
        Assert.DoesNotContain(Path.Combine("nested", "file.tmp"), entries.Keys);
        Assert.DoesNotContain("bin", entries.Keys);
        Assert.DoesNotContain(Path.Combine("bin", "never-scanned.txt"), entries.Keys);
        Assert.Contains("folder.tmp", entries.Keys);
        Assert.Contains(Path.Combine("folder.tmp", "keep"), entries.Keys);
        Assert.Contains("file-named-bin", entries.Keys);
    }

    [Fact]
    public void FileAndFolderPatternsWithSameNameRemainIndependent()
    {
        Write(Left, "same");
        Write(Right, "same/child");
        Assert.Empty(FolderService.Scan(Left, fileExclusions: "same"));
        Assert.Equal(2, FolderService.Scan(Right, fileExclusions: "same").Count);
        Assert.Single(FolderService.Scan(Left, folderExclusions: "same"));
        Assert.Empty(FolderService.Scan(Right, folderExclusions: "same"));
    }

    [Theory]
    [InlineData(MirrorMode.Right, false)]
    [InlineData(MirrorMode.Left, false)]
    [InlineData(MirrorMode.Right, true)]
    [InlineData(MirrorMode.Left, true)]
    public void MirrorFiltersSourceAndOnlyDeletesExcludedTargetsWhenEnabled(MirrorMode mode, bool delete)
    {
        string source = mode == MirrorMode.Right ? Left : Right;
        string destination = mode == MirrorMode.Right ? Right : Left;
        Write(source, "visible", "copy me");
        Write(source, "source-only.tmp", "do not copy");
        Write(source, "shared.tmp", "source secret");
        Write(destination, "shared.tmp", "target secret");
        Write(destination, "target-only.tmp", "target-only secret");
        Write(source, "cache/deep/source", "source cache");
        Write(destination, "cache/deep/target", "target cache");
        var settings = new ComparisonSettings(FileExclusions: "*.tmp", FolderExclusions: "cache",
            DeleteExcludedFromDestination: delete);
        var plan = FolderService.Plan(Left, Right, mode, settings);
        Assert.False(plan.SourceSnapshot.ContainsKey("shared.tmp"));
        Assert.False(plan.SourceSnapshot.ContainsKey("cache"));
        Assert.Equal(delete, plan.DestinationSnapshot.ContainsKey("shared.tmp"));
        Assert.Equal(delete, plan.DeleteExcludedFromDestination);
        Assert.DoesNotContain(plan.Actions, action => action.Kind == "Copy file" && action.RelativePath.EndsWith(".tmp"));
        if (delete)
        {
            Assert.Contains(plan.Actions, action => action.Kind == "Delete file" && action.RelativePath == "shared.tmp");
            Assert.Contains(plan.Actions, action => action.Kind == "Delete folder" && action.RelativePath == "cache");
        }
        FolderService.Execute(plan);
        Assert.Equal("copy me", File.ReadAllText(Path.Combine(destination, "visible")));
        Assert.False(File.Exists(Path.Combine(destination, "source-only.tmp")));
        Assert.Equal(!delete, File.Exists(Path.Combine(destination, "shared.tmp")));
        Assert.Equal(!delete, File.Exists(Path.Combine(destination, "target-only.tmp")));
        Assert.Equal(!delete, Directory.Exists(Path.Combine(destination, "cache")));
        Assert.Equal("source secret", File.ReadAllText(Path.Combine(source, "shared.tmp")));
        Assert.True(File.Exists(Path.Combine(source, "cache/deep/source")));
        Assert.DoesNotContain(FolderService.Compare(source, destination, settings), row => row.RelativePath.EndsWith(".tmp"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BidirectionalModeAlwaysIgnoresExclusionsOnBothSides(bool delete)
    {
        Write(Left, "left", "left");
        Write(Right, "right", "right");
        Write(Left, "shared.tmp", "left secret");
        Write(Right, "shared.tmp", "right secret");
        Write(Left, "left-only.tmp");
        Write(Right, "cache/right-only");
        Write(Left, "common.ignore");
        var settings = new ComparisonSettings(Exclusions: "*.ignore", FileExclusions: "*.tmp", FolderExclusions: "cache",
            DeleteExcludedFromDestination: delete);
        var plan = FolderService.Plan(Left, Right, MirrorMode.Both, settings);
        Assert.False(plan.DeleteExcludedFromDestination);
        Assert.Equal(2, plan.Actions.Count);
        Assert.DoesNotContain(plan.Actions, action => action.Kind.StartsWith("Delete"));
        FolderService.Execute(plan);
        Assert.Equal("left secret", File.ReadAllText(Path.Combine(Left, "shared.tmp")));
        Assert.Equal("right secret", File.ReadAllText(Path.Combine(Right, "shared.tmp")));
        Assert.False(File.Exists(Path.Combine(Right, "left-only.tmp")));
        Assert.False(Directory.Exists(Path.Combine(Left, "cache")));
        Assert.False(File.Exists(Path.Combine(Right, "common.ignore")));
        Assert.True(File.Exists(Path.Combine(Left, "right")));
        Assert.True(File.Exists(Path.Combine(Right, "left")));
    }

    [Fact]
    public void SharedPatternsAlsoCleanDestinationWhenEnabled()
    {
        Write(Left, ".git/config");
        Write(Right, ".git/config");
        Write(Left, "file.tmp");
        Write(Right, "file.tmp");
        var plan = FolderService.Plan(Left, Right, MirrorMode.Right,
            new(Exclusions: ".git;*.tmp", DeleteExcludedFromDestination: true));
        Assert.Empty(plan.SourceSnapshot);
        FolderService.Execute(plan);
        Assert.Empty(Directory.GetFileSystemEntries(Right));
        Assert.True(File.Exists(Path.Combine(Left, ".git/config")));
    }

    [Fact]
    public void CleanupStillRespectsShallowScope()
    {
        Write(Left, "visible");
        Write(Right, "root.tmp");
        Write(Right, "cache/keep.tmp");
        FolderService.Execute(FolderService.Plan(Left, Right, MirrorMode.Right,
            new(IncludeSubfolders: false, FileExclusions: "*.tmp", FolderExclusions: "cache", DeleteExcludedFromDestination: true)));
        Assert.False(File.Exists(Path.Combine(Right, "root.tmp")));
        Assert.True(File.Exists(Path.Combine(Right, "cache/keep.tmp")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DefaultMirrorProtectsTypedExcludedDescendantsAndTypeConflicts(bool typeConflict)
    {
        Write(Right, "parent/deep/keep.tmp");
        Write(Right, "parent/deep/delete");
        if (typeConflict) Write(Left, "parent");
        var plan = FolderService.Plan(Left, Right, MirrorMode.Right, new(FileExclusions: "*.tmp"));
        FolderService.Execute(plan);
        Assert.True(File.Exists(Path.Combine(Right, "parent/deep/keep.tmp")));
        Assert.Equal(typeConflict, File.Exists(Path.Combine(Right, "parent/deep/delete")));
        Assert.Equal(typeConflict ? 1 : 0, plan.Conflicts!.Count);
    }

    [Fact]
    public void CleanupAllowsTypeReplacementThatDefaultModeProtects()
    {
        Write(Left, "parent", "replacement");
        Write(Right, "parent/keep.tmp");
        var plan = FolderService.Plan(Left, Right, MirrorMode.Right,
            new(FileExclusions: "*.tmp", DeleteExcludedFromDestination: true));
        Assert.Empty(plan.Conflicts!);
        FolderService.Execute(plan);
        Assert.Equal("replacement", File.ReadAllText(Path.Combine(Right, "parent")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TargetExcludedChangesInvalidateOnlyCleanupPlans(bool delete)
    {
        Write(Left, "visible");
        Write(Right, "secret.tmp");
        var plan = FolderService.Plan(Left, Right, MirrorMode.Right,
            new(FileExclusions: "*.tmp", DeleteExcludedFromDestination: delete));
        Write(Right, "new.tmp");
        if (delete)
        {
            Assert.ThrowsAny<IOException>(() => FolderService.Execute(plan));
            Assert.False(File.Exists(Path.Combine(Right, "visible")));
        }
        else
        {
            FolderService.Execute(plan);
            Assert.True(File.Exists(Path.Combine(Right, "visible")));
        }
        Assert.True(File.Exists(Path.Combine(Right, "new.tmp")));
    }

    [Fact]
    public void SourceExcludedChangesNeverInvalidateCleanupPreview()
    {
        Write(Left, "visible");
        var plan = FolderService.Plan(Left, Right, MirrorMode.Right,
            new(FileExclusions: "*.tmp", FolderExclusions: "cache", DeleteExcludedFromDestination: true));
        Write(Left, "new.tmp");
        Write(Left, "cache/new");
        FolderService.Execute(plan);
        Assert.True(File.Exists(Path.Combine(Right, "visible")));
        Assert.False(File.Exists(Path.Combine(Right, "new.tmp")));
    }

    [Fact]
    public void ExcludedLockedSourceFileIsNeverOpened()
    {
        string path = Write(Left, "secret.tmp");
        using var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        var plan = FolderService.Plan(Left, Right, MirrorMode.Right, new(FileExclusions: "*.tmp"));
        Assert.Empty(plan.SourceSnapshot);
        Assert.Empty(plan.Actions);
        FolderService.Execute(plan);
    }
}
