namespace XtraSync.Tests;

public sealed class ReadErrorBudgetTests : TestWorkspace
{
    [Fact]
    public void DefaultLimitIsTenAndCloningPreservesOtherPolicyDependencies()
    {
        Assert.Equal(10, new ErrorPolicyOptions().MaximumReadErrorFiles);
        Assert.Equal("No execution problems.", CreateViewModel().ProblemSummary);
        using var empty = new OperationControl();
        Assert.Equal(new OperationStatistics(), empty.AppliedStatistics);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ErrorPolicy(options: new() { MaximumReadErrorFiles = 0 }));
        int waits = 0, attempts = 0;
        var fs = new FaultFileSystem();
        var original = new ErrorPolicy(fs, options: new() { FileRetryDelays = [TimeSpan.Zero] }, wait: (_, _) => waits++);
        var policy = original.WithReadErrorLimit(3);
        Assert.Equal(10, original.Options.MaximumReadErrorFiles);
        Assert.Equal(3, policy.Options.MaximumReadErrorFiles);
        Assert.Same(fs, policy.FileSystem);
        Assert.True(policy.Try(() => ++attempts == 1 ? throw FaultFileSystem.Io(32) : true, [Left], null, "file", "Copy").Succeeded);
        Assert.Equal(1, waits);
    }

    [Fact]
    public void CounterIsUniqueByAbsoluteFileAndDoesNotResetAcrossPhasesOrSuccesses()
    {
        var issues = new List<OperationIssue>();
        using var control = new OperationControl(issueHandler: issues.Add);
        var error = FaultFileSystem.Io(12345);
        string first = Path.Combine(Left, "first");
        control.ReadFailure(first, error, 3);
        control.ReadFailure(first.ToUpperInvariant(), error, 3);
        control.Copied(123);
        control.Begin("A different phase");
        control.ReadFailure(Path.Combine(Left, "second"), error, 3);
        Assert.Equal(2, control.Problems.ReadErrorFiles);
        var abort = Assert.Throws<RunAbortedException>(() => control.ReadFailure(Path.Combine(Left, "third"), error, 3));
        Assert.Equal(ErrorCategory.ReadErrorLimit, abort.Category);
        Assert.Same(error, abort.InnerException);
        Assert.Contains("3 distinct files (limit 3)", abort.Message);
        Assert.Equal(3, issues.Count(issue => issue.Outcome == IssueOutcome.ReadError));
        Assert.True(control.Problems.HasWarnings);
        Assert.False(control.Problems.HasIssues);
        using var nextRun = new OperationControl();
        Assert.Equal(0, nextRun.Problems.ReadErrorFiles);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(32)]
    [InlineData(23)]
    [InlineData(53)]
    [InlineData(206)]
    public void MissingPermissionsLocksAndAlreadyFatalConditionsDoNotConsumeTheReadBudget(int code)
    {
        using var control = new OperationControl();
        control.ReadFailure(Path.Combine(Left, "file"), FaultFileSystem.Io(code), 1);
        Assert.Equal(0, control.Problems.ReadErrorFiles);
    }

    [Fact]
    public void TenDistinctRecoveredOrFailingReadsStopTheRunBeforeTheEleventhFile()
    {
        string[] sources = Enumerable.Range(0, 15).Select(index => Write(Left, $"file{index:D2}", "source-" + index, 10)).ToArray();
        Write(Right, "file09", "original");
        var fs = new FaultFileSystem { OpenFault = (_, count) => count == 1 ? FaultFileSystem.Io(12345) : null };
        var plan = FolderService.Plan(Left, Right, MirrorMode.Right, new(ErrorPolicy: fs.Policy()));
        plan.Actions.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.RelativePath, b.RelativePath));
        using var control = new OperationControl();
        var error = Assert.Throws<RunAbortedException>(() => FolderService.Execute(plan, control));
        Assert.Equal(ErrorCategory.ReadErrorLimit, error.Category);
        Assert.Equal(10, control.Problems.ReadErrorFiles);
        Assert.Equal(9, control.Problems.Recovered);
        Assert.Equal(9, control.Problems.Retries);
        Assert.Equal(9, control.AppliedStatistics.CopyFiles);
        Assert.Equal(1, control.Problems.FailedFiles);
        Assert.Equal(1, fs.Count("open", sources[9]));
        Assert.All(sources.Skip(10), path => Assert.Equal(0, fs.Count("open", path)));
        Assert.Equal("original", File.ReadAllText(Path.Combine(Right, "file09")));
        Assert.Empty(Directory.GetFiles(Right, "*.tmp"));
    }

    [Fact]
    public void MultipleFailuresOnOneFileCountOnceAndDoNotStopIndependentWork()
    {
        string bad = Write(Left, "bad", "source");
        Write(Left, "good");
        var fs = new FaultFileSystem { OpenFault = (path, _) => path == bad ? FaultFileSystem.Io(12345) : null };
        var plan = FolderService.Plan(Left, Right, MirrorMode.Right, new(ErrorPolicy: fs.Policy(2)));
        var result = FolderService.Execute(plan);
        Assert.Equal(3, fs.Count("open", bad));
        Assert.Equal(1, result.Problems.ReadErrorFiles);
        Assert.Equal(1, result.Problems.FailedFiles);
        Assert.Equal(1, result.Applied.CopyFiles);
        Assert.True(File.Exists(Path.Combine(Right, "good")));
    }

    [Fact]
    public void AnErrorInsideStreamReadCountsAndStopsWithoutCommittingTheTemporaryFile()
    {
        string source = Write(Left, "file", "", 10);
        using (var stream = File.OpenWrite(source)) stream.SetLength(3 * 1024 * 1024);
        Write(Right, "file", "original");
        var fs = new FaultFileSystem
        {
            InputFactory = path => new InterceptStream(File.OpenRead(path), beforeRead: call =>
            { if (call == 2) throw FaultFileSystem.Io(12345); })
        };
        var plan = FolderService.Plan(Left, Right, MirrorMode.Right, new(ErrorPolicy: fs.Policy(1)));
        using var control = new OperationControl();
        Assert.Equal(ErrorCategory.ReadErrorLimit, Assert.Throws<RunAbortedException>(() => FolderService.Execute(plan, control)).Category);
        Assert.Equal(1024 * 1024, control.Snapshot.Bytes);
        Assert.Equal(0, control.AppliedStatistics.CopyFiles);
        Assert.Equal(1, control.Problems.ReadErrorFiles);
        Assert.Equal("original", File.ReadAllText(Path.Combine(Right, "file")));
        Assert.Empty(Directory.GetFiles(Right, "*.tmp"));
    }

    [Fact]
    public void MetadataAndPayloadReadFaultsOnTheSameFileShareTheRunCounter()
    {
        string source = Write(Left, "file");
        var fs = new FaultFileSystem
        {
            LengthFault = (path, count) => path == source && count == 1 ? FaultFileSystem.Io(12345) : null,
            OpenFault = (path, count) => path == source && count == 1 ? FaultFileSystem.Io(12345) : null
        };
        using var control = new OperationControl();
        var plan = FolderService.Plan(Left, Right, MirrorMode.Right, new(ErrorPolicy: fs.Policy(2)), control);
        var result = FolderService.Execute(plan, control);
        Assert.Equal(1, result.Problems.ReadErrorFiles);
        Assert.Equal(2, result.Problems.Recovered);
        Assert.Equal(1, result.Applied.CopyFiles);
    }

    [Theory]
    [InlineData("scan")]
    [InlineData("compare")]
    [InlineData("plan")]
    public void StandaloneScanningApisAlsoEnforceTheLimitWithoutAnExplicitControl(string api)
    {
        for (int index = 0; index < 5; index++) Write(Left, $"file{index}");
        var fs = new FaultFileSystem { LengthFault = (_, count) => count == 1 ? FaultFileSystem.Io(12345) : null };
        var policy = fs.Policy(3);
        var error = Assert.Throws<RunAbortedException>(() =>
        {
            switch (api)
            {
                case "scan": FolderService.Scan(Left, errorPolicy: policy); break;
                case "compare": FolderService.Compare(Left, Right, new(ErrorPolicy: policy)); break;
                default: FolderService.Plan(Left, Right, MirrorMode.Right, new(ErrorPolicy: policy)); break;
            }
        });
        Assert.Equal(ErrorCategory.ReadErrorLimit, error.Category);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WriteAndCommitFailuresAreNotMisclassifiedAsReadErrors(bool commit)
    {
        Write(Left, "file");
        var fs = new FaultFileSystem();
        int writes = 0;
        if (commit) fs.MoveFault = (_, count) => count < 3 ? FaultFileSystem.Io(12345) : null;
        else fs.OutputFactory = path => new InterceptStream(File.Create(path), beforeWrite: _ =>
            { if (++writes < 3) throw FaultFileSystem.Io(12345); });
        var plan = FolderService.Plan(Left, Right, MirrorMode.Right, new(ErrorPolicy: fs.Policy(1)));
        var result = FolderService.Execute(plan);
        Assert.Equal(0, result.Problems.ReadErrorFiles);
        Assert.Equal(2, result.Problems.Retries);
        Assert.Equal(1, result.Applied.CopyFiles);
    }

    [Fact]
    public void ThresholdDuringTypeReplacementPreparationDoesNotDeleteTheDestinationTree()
    {
        string source = Write(Left, "parent", "source");
        string keep = Write(Right, "parent/keep", "original");
        var fs = new FaultFileSystem { OpenFault = (path, _) => path == source ? FaultFileSystem.Io(12345) : null };
        var plan = FolderService.Plan(Left, Right, MirrorMode.Right, new(ErrorPolicy: fs.Policy(1)));
        using var control = new OperationControl();
        Assert.Equal(ErrorCategory.ReadErrorLimit, Assert.Throws<RunAbortedException>(() => FolderService.Execute(plan, control)).Category);
        Assert.Equal("original", File.ReadAllText(keep));
        Assert.Equal(0, control.AppliedStatistics.DeleteFiles + control.AppliedStatistics.DeleteFolders);
    }

    [Fact]
    public async Task SuccessfulFilesBetweenFailuresDoNotResetTheBudgetAndTheNextRunStartsFromZero()
    {
        foreach (string name in new[] { "a", "b", "c", "d", "e" }) Write(Left, name);
        var fs = new FaultFileSystem
        {
            OpenFault = (path, count) => count == 1 && Path.GetFileName(path) is "a" or "b" or "d"
            ? FaultFileSystem.Io(12345) : null
        };
        string reports = Path.Combine(Root, "reports");
        var model = new MainViewModel(new FolderHistory(Path.Combine(Root, "history.json")),
            new ExclusionList(Path.Combine(Root, "exclusions.json")), new RunLogStore(reports), fs.Policy(3))
        { LeftPath = Left, RightPath = Right, ConfirmMirror = _ => true, GenerateHtmlReport = true };
        model.ConfirmMirror = plan => { plan.Actions.Sort((a, b) => StringComparer.Ordinal.Compare(a.RelativePath, b.RelativePath)); return true; };
        await model.MirrorRightCommand.ExecuteAsync(null);
        Assert.StartsWith("Stopped: Global read-error limit reached", model.Status);
        Assert.Equal(3, model.Problems.ReadErrorFiles);
        Assert.Equal(3, model.AppliedStatistics.CopyFiles);
        Assert.Equal(0, fs.Count("open", Path.Combine(Left, "e")));
        Assert.Contains("I/O read errors: 3 distinct files (limit 3)", model.ProblemSummary);
        model.ReadErrorLimit = "7";
        Assert.Contains("I/O read errors: 3 distinct files (limit 3)", model.ProblemSummary);
        model.ReadErrorLimit = "3";
        string log = File.ReadAllText(Path.Combine(reports, "XtraSync.log"));
        Assert.Contains("ReadErrorLimit", log);
        Assert.Contains("Distinct files with I/O read errors (including recovered reads): 3", log);
        Assert.EndsWith("=== END RUN SUMMARY ===", log.TrimEnd());
        string html = File.ReadAllText(Assert.Single(Directory.GetFiles(reports, "*.html")));
        Assert.Contains("Global read-error limit reached", html);
        Assert.Contains("Distinct files with I/O read errors", html);
        Assert.False(model.GenerateHtmlReport);
        await model.MirrorRightCommand.ExecuteAsync(null);
        Assert.Equal(0, model.Problems.ReadErrorFiles);
        Assert.DoesNotContain("Stopped:", model.Status);
        Assert.True(File.Exists(Path.Combine(Right, "e")));
        Assert.Equal("3", model.ReadErrorLimit);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("invalid")]
    [InlineData("")]
    public async Task InvalidUserLimitsAreRejectedBeforeSynchronizationWrites(string limit)
    {
        Write(Left, "file");
        var model = CreateViewModel();
        Assert.Equal("10", model.ReadErrorLimit);
        model.ReadErrorLimit = limit;
        model.ConfirmMirror = _ => true;
        await model.MirrorRightCommand.ExecuteAsync(null);
        Assert.StartsWith("Stopped: The read-error limit must be a positive whole number", model.Status);
        Assert.Empty(Directory.GetFiles(Right));
    }

    private sealed class InterceptStream(Stream inner, Action<int>? beforeRead = null, Action<int>? beforeWrite = null) : Stream
    {
        private int reads, writes;
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) { beforeRead?.Invoke(++reads); return inner.Read(buffer, offset, count); }
        public override void Write(byte[] buffer, int offset, int count) { beforeWrite?.Invoke(++writes); inner.Write(buffer, offset, count); }
        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
