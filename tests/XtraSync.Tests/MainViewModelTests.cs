using CommunityToolkit.Mvvm.Input;

namespace XtraSync.Tests;

public sealed class MainViewModelTests : TestWorkspace
{
    [Fact]
    public void ConstructorLoadsOnlyInjectedSettingsAndHistory()
    {
        var history = new FolderHistory(Path.Combine(Root, "history.json"));
        history.Remember(Left, true);
        history.Remember(Right, false);
        history.Save();
        new ExclusionList(Path.Combine(Root, "exclusions.json")).Save("*.tmp");
        var model = CreateViewModel();
        Assert.Equal("*.tmp", model.Exclusions);
        Assert.Equal([Left], model.LeftFolderHistory);
        Assert.Equal([Right], model.RightFolderHistory);
        Assert.True(model.CompareSize);
        Assert.True(model.CompareTimestamps);
        Assert.True(model.IncludeSubfolders);
        Assert.False(model.IgnoreTimeZone);
        Assert.Equal("2", model.Tolerance);
        Assert.True(model.CanEdit);
        Assert.False(model.PauseCommand.CanExecute(null));
        Assert.False(model.StopCommand.CanExecute(null));
        Assert.False(model.RestartCommand.CanExecute(null));
    }

    [Fact]
    public void ExplicitStoresCannotBeNull()
    {
        Assert.Throws<ArgumentNullException>(() => new MainViewModel(null!, new ExclusionList(), new RunLogStore(Root)));
        Assert.Throws<ArgumentNullException>(() => new MainViewModel(new FolderHistory(), null!, new RunLogStore(Root)));
        Assert.Throws<ArgumentNullException>(() => new MainViewModel(new FolderHistory(), new ExclusionList(), null!));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BrowseUpdatesRequestedSideAndPersistsHistory(bool left)
    {
        var model = CreateViewModel();
        model.ChooseFolder = (title, current) =>
        {
            Assert.Equal(left ? "Choose left folder" : "Choose right folder", title);
            Assert.Equal(left ? Left : Right, current);
            return Root;
        };
        (left ? model.BrowseLeftCommand : model.BrowseRightCommand).Execute(null);
        Assert.Equal(Root, left ? model.LeftPath : model.RightPath);
        var restored = CreateViewModel();
        Assert.Equal([Root], left ? restored.LeftFolderHistory : restored.RightFolderHistory);
        Assert.Empty(left ? restored.RightFolderHistory : restored.LeftFolderHistory);
    }

    [Fact]
    public void CancelledOrUnavailableBrowserLeavesPathsUnchanged()
    {
        var model = CreateViewModel();
        model.BrowseLeftCommand.Execute(null);
        model.ChooseFolder = (_, _) => null;
        model.BrowseRightCommand.Execute(null);
        Assert.Equal(Left, model.LeftPath);
        Assert.Equal(Right, model.RightPath);
    }

    [Fact]
    public void EditingExclusionsPersistsIncludingClearing()
    {
        var model = CreateViewModel();
        model.Exclusions = "*.tmp";
        Assert.Equal("*.tmp", CreateViewModel().Exclusions);
        model.Exclusions = "";
        Assert.Equal("", CreateViewModel().Exclusions);
    }

    [Fact]
    public void PersistenceErrorsAppearInStatusWithoutThrowing()
    {
        var model = new MainViewModel(new FolderHistory(Left), new ExclusionList(Right), new RunLogStore(Path.Combine(Root, "reports"))) { LeftPath = Root };
        model.RememberFolder(true);
        Assert.StartsWith("Folder history could not be saved:", model.Status);
        model.Exclusions = "*.tmp";
        Assert.StartsWith("Preferences could not be saved:", model.Status);
    }

    [Fact]
    public void PostMirrorComparisonPreferenceDefaultsOffAndPersistsAcrossModels()
    {
        File.WriteAllText(Path.Combine(Root, "exclusions.json"),
            """{"ExclusionLine":"*.tmp","DeleteExcludedFromDestination":true}""");
        var model = CreateViewModel();
        Assert.False(model.CompareAfterMirroring);

        model.CompareAfterMirroring = true;
        model.Exclusions = "*.bak";
        var restored = CreateViewModel();
        Assert.True(restored.CompareAfterMirroring);
        Assert.True(restored.DeleteExcludedFromDestination);
        Assert.Equal("*.bak", restored.Exclusions);

        restored.CompareAfterMirroring = false;
        Assert.False(CreateViewModel().CompareAfterMirroring);
    }

    [Fact]
    public void BusyAndPausePropertiesNotifyBindingsAndCommands()
    {
        var model = CreateViewModel();
        var properties = new List<string?>();
        int notifications = 0;
        model.PropertyChanged += (_, e) => properties.Add(e.PropertyName);
        model.CompareCommand.CanExecuteChanged += (_, _) => notifications++;
        model.IsBusy = true;
        Assert.False(model.CanEdit);
        foreach (IRelayCommand command in new IRelayCommand[] { model.CompareCommand, model.MirrorLeftCommand,
            model.MirrorRightCommand, model.SynchronizeBothCommand, model.BrowseLeftCommand, model.BrowseRightCommand })
            Assert.False(command.CanExecute(null));
        model.CanControl = true;
        Assert.True(model.PauseCommand.CanExecute(null));
        model.PauseCommand.Execute(null);
        Assert.Equal("Resume", model.PauseLabel);
        model.PauseCommand.Execute(null);
        Assert.Equal("Pause", model.PauseLabel);
        model.StopCommand.Execute(null);
        Assert.False(model.CanControl);
        model.IsBusy = false;
        Assert.True(model.CanEdit);
        Assert.Contains(nameof(model.CanEdit), properties);
        Assert.Contains(nameof(model.PauseLabel), properties);
        Assert.Equal(2, notifications);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("not a number")]
    [InlineData("")]
    [InlineData("2147483648")]
    public async Task InvalidToleranceReportsErrorAndRestoresIdle(string tolerance)
    {
        var model = CreateViewModel();
        model.Tolerance = tolerance;
        await model.CompareCommand.ExecuteAsync(null);
        Assert.StartsWith("Stopped: Timestamp tolerance", model.Status);
        AssertIdle(model);
        Assert.Empty(model.Rows);
    }

    [Fact]
    public async Task MissingRootReportsErrorAndRestoresIdle()
    {
        var model = CreateViewModel();
        model.LeftPath = Path.Combine(Root, "missing");
        await model.CompareCommand.ExecuteAsync(null);
        Assert.StartsWith("Stopped: Both folders must exist", model.Status);
        AssertIdle(model);
    }

    [Fact]
    public async Task CompareUsesOptionsAndRestartScansFreshState()
    {
        Write(Left, "equal", "same");
        Write(Right, "equal", "same");
        Write(Left, "ignored.tmp");
        Write(Left, "nested/child");
        var model = CreateViewModel();
        model.Exclusions = "*.tmp";
        model.IncludeSubfolders = false;
        await model.CompareCommand.ExecuteAsync(null);
        Assert.Empty(model.Rows);
        Assert.Contains("1 equal files", model.Status);
        Assert.Contains("0 differences", model.Status);
        AssertIdle(model);
        Write(Right, "new");
        Assert.True(model.RestartCommand.CanExecute(null));
        await model.RestartCommand.ExecuteAsync(null);
        var row = Assert.Single(model.Rows);
        Assert.Equal("new", row.RelativePath);
        Assert.Equal("Right only", row.Status);
        Assert.Contains("2 items", model.Status);
        Assert.Contains("1 differences", model.Status);
    }

    [Theory]
    [InlineData(MirrorMode.Left)]
    [InlineData(MirrorMode.Right)]
    [InlineData(MirrorMode.Both)]
    public async Task SynchronizationRequiresReviewAndAppliesRequestedDirection(MirrorMode mode)
    {
        Write(Left, "left.txt");
        Write(Right, "right.txt");
        var model = CreateViewModel();
        int reviews = 0;
        model.ConfirmMirror = plan =>
        {
            reviews++;
            Assert.True(model.IsBusy);
            Assert.False(model.CanControl);
            Assert.Equal(mode == MirrorMode.Both, plan.BothWays);
            Assert.Equal(mode == MirrorMode.Left ? Right : Left, plan.Source);
            return true;
        };
        await Command(model, mode).ExecuteAsync(null);
        Assert.Equal(1, reviews);
        Assert.Contains("finished", model.Status);
        AssertIdle(model);
        Assert.Empty(model.Rows);
        if (mode == MirrorMode.Both)
        {
            Assert.Contains("2 equal files", model.Statistics);
            Assert.Contains("0 differences", model.Statistics);
        }
        else
        {
            Assert.Contains("Final comparison skipped", model.Status);
            string log = File.ReadAllText(Path.Combine(Root, "reports", "XtraSync.log"));
            Assert.DoesNotContain("Phase: Comparing entries", log);
        }
        Assert.Equal(mode == MirrorMode.Both ? 2 : 1, Directory.GetFiles(Left).Length);
        Assert.Equal(mode == MirrorMode.Both ? 2 : 1, Directory.GetFiles(Right).Length);
    }

    [Theory]
    [InlineData(MirrorMode.Left)]
    [InlineData(MirrorMode.Right)]
    public async Task EnabledPostMirrorComparisonShowsOnlyRemainingDifferences(MirrorMode mode)
    {
        string source = mode == MirrorMode.Left ? Right : Left;
        string destination = mode == MirrorMode.Left ? Left : Right;
        Write(source, "shared/file");
        Directory.CreateDirectory(Path.Combine(destination, "shared"));
        string[] remaining = ["first", "second", "third"];
        var fs = new FaultFileSystem
        {
            MoveFault = (_, _) =>
            {
                foreach (string path in remaining) Write(destination, path);
                return null;
            }
        };
        var model = new MainViewModel(new FolderHistory(Path.Combine(Root, "history.json")),
            new ExclusionList(Path.Combine(Root, "exclusions.json")), new RunLogStore(Path.Combine(Root, "reports")), fs.Policy())
        {
            LeftPath = Left, RightPath = Right, CompareAfterMirroring = true, ConfirmMirror = _ => true
        };

        await Command(model, mode).ExecuteAsync(null);

        Assert.True(model.CompareAfterMirroring);
        Assert.Equal(remaining, model.Rows.Select(row => row.RelativePath));
        Assert.All(model.Rows, row => Assert.Equal(mode == MirrorMode.Left ? "Left only" : "Right only", row.Status));
        Assert.Contains("Compared 5 / 5 items", model.Statistics);
        Assert.Contains("1 equal files", model.Statistics);
        Assert.Contains("3 differences", model.Statistics);
        Assert.DoesNotContain("Final comparison skipped", model.Status);
        AssertIdle(model);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeclinedOrMissingReviewPreventsAllWrites(bool attachCallback)
    {
        Write(Left, "file");
        var model = CreateViewModel();
        if (attachCallback) model.ConfirmMirror = _ => false;
        await model.MirrorRightCommand.ExecuteAsync(null);
        Assert.Equal("Synchronization cancelled. No files changed.", model.Status);
        Assert.Empty(Directory.GetFiles(Right));
        AssertIdle(model);
    }

    [Fact]
    public async Task EqualFoldersDoNotOpenReview()
    {
        var model = CreateViewModel();
        model.ConfirmMirror = _ => throw new InvalidOperationException("Unexpected review");
        await model.MirrorLeftCommand.ExecuteAsync(null);
        Assert.Equal("The folders already match the comparison rules.", model.Status);
        AssertIdle(model);
    }

    [Fact]
    public async Task ConflictOnlyPlanStillOpensReview()
    {
        Write(Left, "file", "left");
        Write(Right, "file", "right");
        var model = CreateViewModel();
        bool reviewed = false;
        model.ConfirmMirror = plan =>
        {
            reviewed = true;
            Assert.Empty(plan.Actions);
            Assert.Single(plan.Conflicts!);
            return false;
        };
        await model.SynchronizeBothCommand.ExecuteAsync(null);
        Assert.True(reviewed);
        Assert.Equal("left", File.ReadAllText(Path.Combine(Left, "file")));
        Assert.Equal("right", File.ReadAllText(Path.Combine(Right, "file")));
    }

    [Fact]
    public async Task ReviewChangesInvalidatePlanAndSurfaceError()
    {
        Write(Left, "file");
        var model = CreateViewModel();
        model.ConfirmMirror = _ => { Write(Left, "new"); return true; };
        await model.MirrorRightCommand.ExecuteAsync(null);
        Assert.Contains("changed after preview", model.Status);
        Assert.Empty(Directory.GetFiles(Right));
        AssertIdle(model);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PausedRunCanBeStoppedOrRestartedWithFreshReview(bool restart)
    {
        Write(Left, "file");
        var model = CreateViewModel();
        bool paused = false;
        int reviews = 0;
        model.PropertyChanged += (_, e) =>
        {
            if (!paused && e.PropertyName == nameof(model.Status) && model.Status == "Preparing synchronization preview…")
            {
                paused = true;
                model.PauseCommand.Execute(null);
            }
        };
        model.ConfirmMirror = _ => { reviews++; return false; };
        Task run = model.MirrorRightCommand.ExecuteAsync(null);
        Assert.True(paused);
        Assert.True(model.IsPaused);
        if (restart)
        {
            Write(Left, "new");
            await model.RestartCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, reviews);
            Assert.Contains("cancelled", model.Status);
        }
        else
        {
            model.StopCommand.Execute(null);
        }
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        if (!restart) Assert.StartsWith("Stopped. Completed operations", model.Status);
        AssertIdle(model);
        Assert.Empty(Directory.GetFiles(Right));
    }

    [Fact]
    public async Task PausedRunCanResumeAndComplete()
    {
        Write(Left, "file");
        var model = CreateViewModel();
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(model.Status) && model.Status == "Preparing synchronization preview…")
                model.PauseCommand.Execute(null);
        };
        Task run = model.MirrorRightCommand.ExecuteAsync(null);
        Assert.True(model.IsPaused);
        Assert.Equal("Resume", model.PauseLabel);
        model.PauseCommand.Execute(null);
        Assert.False(model.IsPaused);
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("Synchronization cancelled. No files changed.", model.Status);
        AssertIdle(model);
    }

    [Fact]
    public async Task RestartIgnoresQueuedProgressFromPreviousGeneration()
    {
        Write(Left, "file");
        var model = CreateViewModel();
        var context = new BufferedProgressContext();
        await StartWithContext(context, () => model.CompareCommand.ExecuteAsync(null));
        Action[] staleUpdates = context.TakeUpdates();
        Assert.NotEmpty(staleUpdates);
        Write(Right, "new");
        await StartWithContext(context, () => model.RestartCommand.ExecuteAsync(null));
        Assert.Equal("Comparing entries", model.Phase);
        Assert.Contains("Compared 2 / 2 items", model.Statistics);
        model.Phase = "sentinel";
        foreach (Action update in staleUpdates) update();
        Assert.Equal("sentinel", model.Phase);
        foreach (Action update in context.TakeUpdates()) update();
        Assert.Equal("sentinel", model.Phase);
        Assert.Contains("Compared 2 / 2 items", model.Statistics);
    }

    private static Task StartWithContext(SynchronizationContext context, Func<Task> start)
    {
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            return start();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    /// <summary>Buffers only progress messages so stale UI deliveries can be replayed deterministically.</summary>
    private sealed class BufferedProgressContext : SynchronizationContext
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<Action> updates = new();

        public override void Post(SendOrPostCallback callback, object? state)
        {
            if (state is OperationProgress) updates.Enqueue(() => callback(state));
            else callback(state);
        }

        public Action[] TakeUpdates()
        {
            var result = new List<Action>();
            while (updates.TryDequeue(out Action? action)) result.Add(action);
            return [.. result];
        }
    }

    private static IAsyncRelayCommand Command(MainViewModel model, MirrorMode mode) => mode switch
    {
        MirrorMode.Left => model.MirrorLeftCommand,
        MirrorMode.Right => model.MirrorRightCommand,
        _ => model.SynchronizeBothCommand
    };

    private static void AssertIdle(MainViewModel model)
    {
        Assert.False(model.IsBusy);
        Assert.False(model.IsPaused);
        Assert.False(model.CanControl);
        Assert.False(model.IsIndeterminate);
        Assert.True(model.CanEdit);
        Assert.True(model.CompareCommand.CanExecute(null));
    }
}
