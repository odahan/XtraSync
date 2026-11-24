namespace XtraSync.Tests;

public sealed class RunLoggingIntegrationTests : TestWorkspace
{
    private string Reports => Path.Combine(Root, "reports");
    private string Log => Path.Combine(Reports, "XtraSync.log");

    [Fact]
    public async Task EveryComparisonCreatesARotatedLogWithAFinalScanAndComparisonSummary()
    {
        Write(Left, "first", "123");
        var model = CreateViewModel();
        Assert.False(model.GenerateHtmlReport);
        await model.CompareCommand.ExecuteAsync(null);
        Assert.Contains("Operation: Compare folders", File.ReadAllText(Log));
        Assert.Contains("Comparing entries: equal files / differences: 0 / 1", File.ReadAllText(Log));
        Assert.Contains("Scanning folders: files / folders: 1 / 0", File.ReadAllText(Log));
        Write(Right, "second", "12345");
        await model.CompareCommand.ExecuteAsync(null);
        Assert.Contains("Scanning folders: files / folders: 2 / 0", File.ReadAllText(Log));
        Assert.Contains("Scanning folders: files / folders: 1 / 0", File.ReadAllText(Path.Combine(Reports, "XtraSync.bak.log")));
        await model.CompareCommand.ExecuteAsync(null);
        Assert.Equal(2, Directory.GetFiles(Reports, "*.log").Length);
        Assert.Empty(Directory.GetFiles(Reports, "*.html"));
        Assert.EndsWith("=== END RUN SUMMARY ===", File.ReadAllText(Log).TrimEnd());
        Assert.Contains(Log, model.RunFiles);
    }

    [Theory]
    [InlineData(MirrorMode.Left)]
    [InlineData(MirrorMode.Right)]
    [InlineData(MirrorMode.Both)]
    public async Task RequestedHtmlContainsTheRealFinalStatisticsAndResetsTheOneShotOption(MirrorMode mode)
    {
        Write(Left, "left", "12345");
        Write(Right, "right", "123");
        var model = CreateViewModel();
        model.GenerateHtmlReport = true;
        model.ConfirmMirror = _ => true;
        await (mode == MirrorMode.Left ? model.MirrorLeftCommand : mode == MirrorMode.Right
            ? model.MirrorRightCommand : model.SynchronizeBothCommand).ExecuteAsync(null);
        Assert.False(model.GenerateHtmlReport);
        string htmlPath = Assert.Single(Directory.GetFiles(Reports, "*.html"));
        Assert.Contains(htmlPath, model.RunFiles);
        string html = File.ReadAllText(htmlPath);
        Assert.Contains("Files copied / planned", html);
        Assert.Contains($"{model.AppliedStatistics.CopyFiles} / {model.PlannedStatistics.CopyFiles}", html);
        Assert.Contains("Folders created / planned", html);
        Assert.Contains("Outcome", html);
        Assert.Contains("finished", html);
        Assert.Equal(model.PlannedStatistics, model.AppliedStatistics);
        await model.CompareCommand.ExecuteAsync(null);
        Assert.Single(Directory.GetFiles(Reports, "*.html"));
    }

    [Fact]
    public async Task ErrorsAreLoggedWithExceptionDetailsAndStillProduceARequestedFailureReport()
    {
        var model = CreateViewModel();
        model.GenerateHtmlReport = true;
        model.Tolerance = "invalid";
        await model.CompareCommand.ExecuteAsync(null);
        string log = File.ReadAllText(Log);
        Assert.Contains("[ERROR] Run failed at:", log);
        Assert.Contains("System.ArgumentException", log);
        Assert.Contains("Timestamp tolerance", log);
        Assert.Contains("Errors: 1", log);
        Assert.EndsWith("=== END RUN SUMMARY ===", log.TrimEnd());
        Assert.Single(Directory.GetFiles(Reports, "*.html"));
        Assert.False(model.GenerateHtmlReport);
        Assert.False(model.IsBusy);
    }

    [Fact]
    public async Task ConflictsAndIgnoredCleanupAreWarningsInBothOutputs()
    {
        Write(Left, "conflict", "left");
        Write(Right, "conflict", "right");
        var model = CreateViewModel();
        model.GenerateHtmlReport = true;
        model.DeleteExcludedFromDestination = true;
        model.ConfirmMirror = _ => false;
        await model.SynchronizeBothCommand.ExecuteAsync(null);
        string log = File.ReadAllText(Log);
        Assert.Contains("[WARNING] Delete excluded target items is ignored", log);
        Assert.Contains("[WARNING] Conflict left unchanged: conflict", log);
        Assert.Contains("Warnings: 2", log);
        Assert.Contains("Files copied / planned: 0 / 0", log);
        string html = File.ReadAllText(Assert.Single(Directory.GetFiles(Reports, "*.html")));
        Assert.Contains("Conflict left unchanged: conflict", html);
        Assert.Contains("Delete excluded target items is ignored", html);
        Assert.False(model.GenerateHtmlReport);
    }

    [Fact]
    public async Task DeclinedPreviewProducesAZeroAppliedSummaryAndReport()
    {
        Write(Left, "copy", "12345");
        Write(Right, "delete", "123");
        var model = CreateViewModel();
        model.GenerateHtmlReport = true;
        model.ConfirmMirror = _ => false;
        await model.MirrorRightCommand.ExecuteAsync(null);
        string log = File.ReadAllText(Log);
        Assert.Contains("Outcome: Synchronization cancelled. No files changed.", log);
        Assert.Contains("Files copied / planned: 0 / 1", log);
        Assert.Contains("Files deleted / planned: 0 / 1", log);
        Assert.Single(Directory.GetFiles(Reports, "*.html"));
        Assert.True(File.Exists(Path.Combine(Right, "delete")));
        Assert.False(model.GenerateHtmlReport);
    }

    [Fact]
    public async Task StopAndWaitFinalizesTheLogAndReportBeforeReturning()
    {
        Write(Left, "file");
        var model = CreateViewModel();
        model.GenerateHtmlReport = true;
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(model.Status) && model.Status == "Preparing synchronization preview…")
                model.PauseCommand.Execute(null);
        };
        Task run = model.MirrorRightCommand.ExecuteAsync(null);
        Assert.True(model.IsPaused);
        await model.StopAndWaitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await run;
        string log = File.ReadAllText(Log);
        Assert.Contains("[WARNING] Stopped.", log);
        Assert.EndsWith("=== END RUN SUMMARY ===", log.TrimEnd());
        Assert.Single(Directory.GetFiles(Reports, "*.html"));
        Assert.False(model.GenerateHtmlReport);
        Assert.False(model.IsBusy);
        Assert.False(File.Exists(Path.Combine(Reports, "XtraSync.run.lock")));
        await model.StopAndWaitAsync();
    }

    [Fact]
    public async Task PartialFailureSummaryKeepsCompletedDeletionsAndDoesNotClaimTheFailedCopy()
    {
        string source = Write(Left, "copy", "12345");
        Write(Right, "delete", "123");
        var model = CreateViewModel();
        model.GenerateHtmlReport = true;
        model.ConfirmMirror = _ => true;
        using (var locked = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.None))
            await model.MirrorRightCommand.ExecuteAsync(null);
        string log = File.ReadAllText(Log);
        Assert.Contains("Files deleted / planned: 1 / 1", log);
        Assert.Contains("Files copied / planned: 0 / 1", log);
        Assert.Contains("[ERROR]", log);
        Assert.Contains("(3 bytes)", log);
        Assert.False(model.GenerateHtmlReport);
        Assert.Single(Directory.GetFiles(Reports, "*.html"));
    }

    [Fact]
    public async Task NoChangesStillCreatesTheRequestedReportAndResetsTheOption()
    {
        var model = CreateViewModel();
        model.GenerateHtmlReport = true;
        await model.MirrorRightCommand.ExecuteAsync(null);
        Assert.Contains("already match", File.ReadAllText(Log));
        Assert.Single(Directory.GetFiles(Reports, "*.html"));
        Assert.False(model.GenerateHtmlReport);
    }

    [Fact]
    public async Task UnwritableLogDestinationPreventsSynchronizationAndResetsTheOption()
    {
        Write(Left, "copy");
        string blocked = Write(Root, "blocked", "not a directory");
        var model = new MainViewModel(new FolderHistory(Path.Combine(Root, "history.json")),
            new ExclusionList(Path.Combine(Root, "exclusions.json")), new RunLogStore(blocked))
            { LeftPath = Left, RightPath = Right, GenerateHtmlReport = true, ConfirmMirror = _ => true };
        await model.MirrorRightCommand.ExecuteAsync(null);
        Assert.StartsWith("Stopped:", model.Status);
        Assert.False(model.IsBusy);
        Assert.False(model.GenerateHtmlReport);
        Assert.Empty(Directory.GetFiles(Right));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RuntimeLogsInTheSourceOrDestinationAreNotCopiedDeletedOrAllowedToInvalidateThePlan(bool inSource)
    {
        string output = Path.Combine(inSource ? Left : Right, "runtime");
        Write(Left, "copy", "12345");
        Write(Right, "delete", "123");
        var model = new MainViewModel(new FolderHistory(Path.Combine(Root, "history.json")),
            new ExclusionList(Path.Combine(Root, "exclusions.json")), new RunLogStore(output))
            { LeftPath = Left, RightPath = Right, DeleteExcludedFromDestination = true, GenerateHtmlReport = true, ConfirmMirror = _ => true };
        await model.MirrorRightCommand.ExecuteAsync(null);
        Assert.Contains("finished", model.Status);
        Assert.True(File.Exists(Path.Combine(output, "XtraSync.log")));
        Assert.Single(Directory.GetFiles(output, "*.html"));
        Assert.True(File.Exists(Path.Combine(Right, "copy")));
        Assert.False(File.Exists(Path.Combine(Right, "delete")));
        if (inSource) Assert.False(File.Exists(Path.Combine(Right, "runtime", "XtraSync.log")));
        Assert.Equal(1, model.AppliedStatistics.CopyFiles);
    }

    [Fact]
    public async Task ReportFailureIsShownWithoutMisreportingTheSuccessfulOperation()
    {
        var store = new RunLogStore(Reports, new FixedReportClock());
        Directory.CreateDirectory(Path.Combine(Reports, "XtraSync-2026-11-18-14-15-16.html.tmp"));
        var model = new MainViewModel(new FolderHistory(Path.Combine(Root, "history.json")),
            new ExclusionList(Path.Combine(Root, "exclusions.json")), store)
            { LeftPath = Left, RightPath = Right, GenerateHtmlReport = true };
        await model.CompareCommand.ExecuteAsync(null);
        Assert.Contains("equal files", model.Status);
        Assert.Contains("HTML report could not be saved:", model.Status);
        Assert.Contains("Errors: 1", File.ReadAllText(Log));
        Assert.EndsWith("=== END RUN SUMMARY ===", File.ReadAllText(Log).TrimEnd());
        Assert.False(model.GenerateHtmlReport);
        Assert.False(model.IsBusy);
    }

    [Fact]
    public async Task SettingsWriteWarningsAreCapturedByTheActiveRun()
    {
        Write(Left, "file");
        var model = new MainViewModel(new FolderHistory(Root), new ExclusionList(Left), new RunLogStore(Reports))
            { LeftPath = Left, RightPath = Right, Exclusions = "*.tmp" };
        await model.CompareCommand.ExecuteAsync(null);
        string log = File.ReadAllText(Log);
        Assert.Contains("[WARNING] Preferences could not be saved:", log);
        Assert.Contains("[WARNING] Folder history could not be saved:", log);
        Assert.Contains("=== END RUN SUMMARY ===", log);
    }

    private sealed class FixedReportClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 18, 14, 15, 16, TimeSpan.Zero);
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
