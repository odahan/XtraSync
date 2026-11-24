namespace XtraSync.Tests;

public sealed class OperationStatisticsTests : TestWorkspace
{
    [Theory]
    [InlineData(MirrorMode.Left)]
    [InlineData(MirrorMode.Right)]
    public void ReviewAndExecutionUseTheSameCopyDeleteAndReplacementTotals(MirrorMode mode)
    {
        string source = mode == MirrorMode.Right ? Left : Right;
        string target = mode == MirrorMode.Right ? Right : Left;
        Write(source, "copy", "12345");
        Write(source, "replace", "1234567", 10);
        Write(target, "replace", "old");
        Write(target, "orphan/deep/delete", "123456789");
        Directory.CreateDirectory(Path.Combine(source, "new-folder"));
        var plan = FolderService.Plan(Left, Right, mode, new());
        var expected = new OperationStatistics(CopyFiles: 2, CopyBytes: 12, DeleteFiles: 1, DeleteBytes: 9,
            CreateFolders: 1, DeleteFolders: 2, ReplaceFiles: 1, ReplacedBytes: 3);
        Assert.Equal(expected, OperationStatistics.FromPlan(plan));
        var review = new ReviewDialogViewModel(plan);
        Assert.Equal(expected, review.Statistics);
        Assert.Equal(2, review.Groups[0].Files);
        Assert.Equal(12, review.Groups[0].Bytes);
        Assert.Equal(SizeFormatter.Format(12), review.Groups[0].Size);
        Assert.Equal(1, review.Groups[1].Files);
        Assert.Equal(9, review.Groups[1].Bytes);
        Assert.Equal(1, review.Groups[2].Files);
        Assert.Equal(3, review.Groups[2].Bytes);
        Assert.True(review.CanApply);
        Assert.Equal("Apply mirror", review.ApplyLabel);
        Assert.Equal("Review mirror operations", review.Title);
        Assert.Contains(source, review.Roots);
        Assert.Contains(target, review.Roots);
        Assert.Contains("3 file operations", review.AffectedSummary);
        Assert.Contains("1 to create", review.FolderSummary);
        Assert.Contains("2 to delete", review.FolderSummary);
        Assert.Contains("0 conflicts", review.ConflictSummary);
        Assert.Contains("preserved", review.Warning);
        Assert.All(review.Actions, action => Assert.Equal(SizeFormatter.Format(action.Bytes), action.Size));
        Assert.Contains(review.Actions, action => action.Operation == "Delete file" && action.Direction == "Destination only");
        using var control = new OperationControl();
        FolderService.Execute(plan, control);
        Assert.Equal(expected, control.Snapshot.Planned);
        Assert.Equal(expected, control.Snapshot.Applied);
        Assert.Equal(12, control.Snapshot.Bytes);
        Assert.Equal(plan.Actions.Count, control.Snapshot.Completed);
        Assert.Equal(100, control.Snapshot.Percentage);
    }

    [Fact]
    public void BothWayTotalsUseTheCorrectSourceAndOverwriteSizesInEachDirection()
    {
        Write(Left, "left-newer", "12345", 10);
        Write(Right, "left-newer", "old");
        Write(Left, "right-newer", "xx");
        Write(Right, "right-newer", "1234567", 10);
        Write(Right, "only-right", "1234");
        var plan = FolderService.Plan(Left, Right, MirrorMode.Both, new());
        var expected = new OperationStatistics(CopyFiles: 3, CopyBytes: 16, ReplaceFiles: 2, ReplacedBytes: 5);
        var review = new ReviewDialogViewModel(plan);
        Assert.Equal(expected, review.Statistics);
        Assert.Equal("Apply synchronization", review.ApplyLabel);
        Assert.Contains("No deletions", review.Warning);
        Assert.Contains("Left:", review.Roots);
        Assert.Contains("Right:", review.Roots);
        Assert.Contains(review.Actions, row => row.Direction == "Right → Left" && row.Bytes == 7);
        using var control = new OperationControl();
        FolderService.Execute(plan, control);
        Assert.Equal(expected, control.Snapshot.Applied);
        Assert.Equal(0, control.Snapshot.Applied!.DeleteFiles);
    }

    [Fact]
    public void TypeReplacementCountsExplicitDeletesWithoutDoubleCountingOverwrites()
    {
        Write(Left, "file", "12345");
        Write(Right, "file/old", "123");
        Write(Left, "folder/new", "1234567");
        Write(Right, "folder", "1234");
        var plan = FolderService.Plan(Left, Right, MirrorMode.Right, new());
        Assert.Equal(new OperationStatistics(2, 12, 2, 7, 1, 1), OperationStatistics.FromPlan(plan));
    }

    [Fact]
    public void ExcludedDestinationCleanupAppearsInTheDeletionGroup()
    {
        Write(Right, "cache/secret.tmp", "123456");
        var plan = FolderService.Plan(Left, Right, MirrorMode.Right,
            new(FolderExclusions: "cache", DeleteExcludedFromDestination: true));
        var review = new ReviewDialogViewModel(plan);
        Assert.Equal(1, review.Statistics.DeleteFiles);
        Assert.Equal(6, review.Statistics.DeleteBytes);
        Assert.Equal(1, review.Statistics.DeleteFolders);
        Assert.Contains("Excluded target items are included", review.Warning);
    }

    [Fact]
    public void ConflictOnlyReviewHasZeroSizesAndCannotApply()
    {
        Write(Left, "ambiguous", "123");
        Write(Right, "ambiguous", "12345");
        var review = new ReviewDialogViewModel(FolderService.Plan(Left, Right, MirrorMode.Both, new()));
        Assert.False(review.CanApply);
        Assert.Single(review.Conflicts);
        Assert.Equal(new OperationStatistics(), review.Statistics);
        Assert.All(review.Groups, group => { Assert.Equal(0, group.Files); Assert.Equal(0, group.Bytes); });
        Assert.Empty(review.Actions);
    }

    [Fact]
    public void EmptyPlanAndZeroByteFilesStillHaveAccurateCounts()
    {
        var empty = FolderService.Plan(Left, Right, MirrorMode.Right, new());
        Assert.Equal(new OperationStatistics(), OperationStatistics.FromPlan(empty));
        var review = new ReviewDialogViewModel(empty with { Conflicts = null });
        Assert.Empty(review.Conflicts);
        Assert.False(review.CanApply);
        Write(Left, "empty", "");
        var plan = FolderService.Plan(Left, Right, MirrorMode.Right, new());
        Assert.Equal(new OperationStatistics(CopyFiles: 1), OperationStatistics.FromPlan(plan));
        using var control = new OperationControl();
        FolderService.Execute(plan, control);
        Assert.Equal(1, control.Snapshot.Applied!.CopyFiles);
        Assert.Equal(0, control.Snapshot.Applied.CopyBytes);
        Assert.Equal(100, control.Snapshot.Percentage);
    }

    [Fact]
    public void StatisticsUseSnapshotsAndDoNotOpenFilesDuringReview()
    {
        string path = Write(Left, "file", "12345");
        var plan = FolderService.Plan(Left, Right, MirrorMode.Right, new());
        using var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.Equal(5, new ReviewDialogViewModel(plan).Statistics.CopyBytes);
        Assert.Throws<ArgumentException>(() => OperationStatistics.ForAction(plan, new FileAction("Invalid", "file")));
    }

    [Fact]
    public void InterruptedCopyReportsTransferredBytesButNoCommittedFile()
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
            if (update.Stage == OperationStage.Executing && update.Bytes >= 1024 * 1024) active!.Stop();
        }, clock);
        active = control;
        Assert.ThrowsAny<OperationCanceledException>(() => FolderService.Execute(plan, control));
        Assert.Equal(1, control.Snapshot.Planned!.CopyFiles);
        Assert.Equal(0, control.Snapshot.Applied!.CopyFiles);
        Assert.Equal(0, control.Snapshot.Applied.CopyBytes);
        Assert.Equal(0, control.Snapshot.Applied.ReplaceFiles);
        Assert.Equal(1024 * 1024, control.Snapshot.Bytes);
        Assert.Equal(0, control.Snapshot.Completed);
        Assert.InRange(control.Snapshot.Percentage, 33, 34);
        Assert.Equal("original", File.ReadAllText(Path.Combine(Right, "file")));
    }

    [Fact]
    public void FailureAfterADeletionRetainsItsCommittedCount()
    {
        string source = Write(Left, "copy", "12345");
        Write(Right, "delete", "1234");
        var plan = FolderService.Plan(Left, Right, MirrorMode.Right, new());
        using var control = new OperationControl();
        using (var locked = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Equal(1, FolderService.Execute(plan, control).Problems.FailedFiles);
        Assert.Equal(1, control.Snapshot.Applied!.DeleteFiles);
        Assert.Equal(4, control.Snapshot.Applied.DeleteBytes);
        Assert.Equal(0, control.Snapshot.Applied.CopyFiles);
        Assert.Equal(2, control.Snapshot.Completed);
        Assert.Equal(100, control.Snapshot.Percentage);
    }

    [Fact]
    public void ComparingReportsKnownTotalsEqualFilesDifferencesAndBytes()
    {
        Write(Left, "equal", "123");
        Write(Right, "equal", "123");
        Write(Left, "different", "12");
        Write(Right, "different", "1234");
        Write(Left, "left-only", "12345");
        Write(Right, "right-only", "123456");
        Directory.CreateDirectory(Path.Combine(Left, "folder"));
        Directory.CreateDirectory(Path.Combine(Right, "folder"));
        using var control = new OperationControl();
        FolderService.Compare(Left, Right, new(), control);
        Assert.Equal(OperationStage.Comparing, control.Snapshot.Stage);
        Assert.Equal(5, control.Snapshot.Total);
        Assert.Equal(5, control.Snapshot.Completed);
        Assert.Equal(1, control.Snapshot.EqualFiles);
        Assert.Equal(3, control.Snapshot.DifferentItems);
        Assert.Equal(6, control.Snapshot.Files);
        Assert.Equal(2, control.Snapshot.Folders);
        Assert.Equal(23, control.Snapshot.Bytes);
        Assert.Equal(100, control.Snapshot.Percentage);
        Assert.Null(control.Snapshot.Planned);
    }

    [Fact]
    public void CopyProgressAdvancesWithinAFileAndWaitsForCommitBeforeOneHundredPercent()
    {
        using var control = new OperationControl();
        control.Begin("copy", 1, OperationStage.Executing, new(CopyFiles: 1, CopyBytes: 100));
        control.Current("file", 100);
        control.Copied(25);
        control.Publish(true);
        Assert.Equal(25, control.Snapshot.Percentage);
        Assert.Equal(25, control.Snapshot.FileBytes);
        Assert.Equal(100, control.Snapshot.FileSize);
        control.Copied(75);
        control.Publish(true);
        Assert.Equal(99, control.Snapshot.Percentage);
        control.Finished(new("Copy file", "file"), new(CopyFiles: 1, CopyBytes: 100));
        control.Publish(true);
        Assert.Equal(100, control.Snapshot.Percentage);
        Assert.Equal(0, control.Snapshot.FileSize);
    }

    [Fact]
    public async Task ViewModelShowsPlanBeforeConfirmationAndRetainsFinalTotalsAfterRescan()
    {
        Write(Left, "copy", "12345");
        Write(Right, "delete", "123");
        var model = CreateViewModel();
        model.CompareAfterMirroring = true;
        model.ConfirmMirror = plan =>
        {
            Assert.Equal(OperationStatistics.FromPlan(plan), model.PlannedStatistics);
            Assert.Equal(new OperationStatistics(), model.AppliedStatistics);
            Assert.Equal("123", File.ReadAllText(Path.Combine(Right, "delete")));
            Assert.False(File.Exists(Path.Combine(Right, "copy")));
            return true;
        };
        await model.MirrorRightCommand.ExecuteAsync(null);
        Assert.Equal(model.PlannedStatistics, model.AppliedStatistics);
        Assert.Equal(1, model.AppliedStatistics.CopyFiles);
        Assert.Equal(5, model.AppliedStatistics.CopyBytes);
        Assert.Equal(1, model.AppliedStatistics.DeleteFiles);
        Assert.Equal(3, model.AppliedStatistics.DeleteBytes);
        Assert.Contains("1 / 1 files", model.CopyStatistics);
        Assert.Contains("1 / 1 files", model.DeleteStatistics);
        Assert.Contains("Folders: create 0 / 0 · delete 0 / 0", model.FolderStatistics);
        Assert.Contains("Transferred:", model.TransferStatistics);
        Assert.Contains("Compared 1 / 1 items", model.Statistics);
        Assert.Equal(100, model.ProgressValue);
        Assert.False(model.IsIndeterminate);
        model.Exclusions = "copy";
        await model.CompareCommand.ExecuteAsync(null);
        Assert.Equal(new OperationStatistics(), model.PlannedStatistics);
        Assert.Equal(new OperationStatistics(), model.AppliedStatistics);
        Assert.Equal("", model.TransferStatistics);
    }

    [Fact]
    public async Task DecliningReviewLeavesEveryAppliedCountAtZero()
    {
        Write(Left, "copy", "12345");
        Write(Right, "delete", "123");
        var model = CreateViewModel();
        model.ConfirmMirror = _ => false;
        await model.MirrorRightCommand.ExecuteAsync(null);
        Assert.Equal(1, model.PlannedStatistics.CopyFiles);
        Assert.Equal(1, model.PlannedStatistics.DeleteFiles);
        Assert.Equal(new OperationStatistics(), model.AppliedStatistics);
        Assert.True(File.Exists(Path.Combine(Right, "delete")));
        Assert.False(File.Exists(Path.Combine(Right, "copy")));
    }
}
