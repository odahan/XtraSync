namespace XtraSync.Tests;

public sealed class ErrorPolicyTests : TestWorkspace
{
    [Theory]
    [InlineData(2, ErrorCategory.Missing)]
    [InlineData(3, ErrorCategory.Missing)]
    [InlineData(5, ErrorCategory.AccessDenied)]
    [InlineData(32, ErrorCategory.Locked)]
    [InlineData(33, ErrorCategory.Locked)]
    [InlineData(39, ErrorCategory.Storage)]
    [InlineData(112, ErrorCategory.Storage)]
    [InlineData(23, ErrorCategory.Storage)]
    [InlineData(1117, ErrorCategory.Storage)]
    [InlineData(1392, ErrorCategory.Storage)]
    [InlineData(1393, ErrorCategory.Storage)]
    [InlineData(1005, ErrorCategory.Storage)]
    [InlineData(123, ErrorCategory.InvalidPath)]
    [InlineData(206, ErrorCategory.InvalidPath)]
    [InlineData(145, ErrorCategory.ChangedDirectory)]
    [InlineData(80, ErrorCategory.ChangedDirectory)]
    [InlineData(183, ErrorCategory.ChangedDirectory)]
    [InlineData(53, ErrorCategory.RootUnavailable)]
    [InlineData(64, ErrorCategory.RootUnavailable)]
    [InlineData(121, ErrorCategory.RootUnavailable)]
    [InlineData(21, ErrorCategory.RootUnavailable)]
    [InlineData(19, ErrorCategory.Storage)]
    [InlineData(15, ErrorCategory.RootUnavailable)] [InlineData(20, ErrorCategory.RootUnavailable)]
    [InlineData(55, ErrorCategory.RootUnavailable)] [InlineData(67, ErrorCategory.RootUnavailable)]
    [InlineData(70, ErrorCategory.RootUnavailable)] [InlineData(72, ErrorCategory.RootUnavailable)]
    [InlineData(88, ErrorCategory.RootUnavailable)] [InlineData(1112, ErrorCategory.RootUnavailable)]
    [InlineData(1167, ErrorCategory.RootUnavailable)] [InlineData(1231, ErrorCategory.RootUnavailable)]
    [InlineData(1232, ErrorCategory.RootUnavailable)] [InlineData(27, ErrorCategory.Storage)]
    [InlineData(29, ErrorCategory.Storage)] [InlineData(30, ErrorCategory.Storage)] [InlineData(31, ErrorCategory.Storage)]
    public void WindowsErrorCodesHaveAnExplicitClassification(int code, ErrorCategory expected) =>
        Assert.Equal(expected, ErrorPolicy.Classify(FaultFileSystem.Io(code)));

    [Fact]
    public void ExceptionTypesAndDefaultsAreClassified()
    {
        Assert.Equal(ErrorCategory.AccessDenied, ErrorPolicy.Classify(new UnauthorizedAccessException()));
        Assert.Equal(ErrorCategory.Missing, ErrorPolicy.Classify(new FileNotFoundException()));
        Assert.Equal(ErrorCategory.Missing, ErrorPolicy.Classify(new DirectoryNotFoundException()));
        Assert.Equal(ErrorCategory.InvalidPath, ErrorPolicy.Classify(new PathTooLongException()));
        Assert.Equal(ErrorCategory.Safety, ErrorPolicy.Classify(new RunAbortedException(ErrorCategory.Safety, "link")));
        var defaults = new ErrorPolicy();
        Assert.Equal([TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(750)], defaults.Options.FileRetryDelays);
        Assert.Equal(TimeSpan.FromSeconds(30), defaults.Options.RootRecoveryTimeout);
        Assert.Equal(TimeSpan.FromSeconds(2), defaults.Options.RootRetryInterval);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ErrorPolicy(options: new() { RootRetryInterval = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ErrorPolicy(options: new() { RootRecoveryTimeout = TimeSpan.FromSeconds(-1) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ErrorPolicy(options: new() { FileRetryDelays = [TimeSpan.FromSeconds(-1)] }));
    }

    [Fact]
    public void TransientFailureRetriesThenRecoversWithoutBecomingAFailedFile()
    {
        var fs = new FaultFileSystem();
        using var control = new OperationControl();
        int attempts = 0;
        var result = fs.Policy().Try(() => ++attempts < 3 ? throw FaultFileSystem.Io(32) : "ok", [Left, Right], control, "file", "Copy");
        Assert.True(result.Succeeded);
        Assert.Equal("ok", result.Value);
        Assert.Equal(3, attempts);
        Assert.Equal(2, control.Problems.Retries);
        Assert.Equal(1, control.Problems.Recovered);
        Assert.False(control.Problems.HasIssues);
        Assert.True(control.Problems.HasWarnings);
    }

    [Theory]
    [InlineData(5, 1)]
    [InlineData(32, 3)]
    [InlineData(123, 1)]
    [InlineData(145, 1)]
    [InlineData(2, 1)]
    [InlineData(12345, 3)]
    public void PermanentErrorsAreNotRetriedAndTransientAttemptsAreBounded(int code, int expectedAttempts)
    {
        int attempts = 0;
        var result = new FaultFileSystem().Policy().Try<int>(() => { attempts++; throw FaultFileSystem.Io(code); }, [Left], null, "file", "Copy");
        Assert.False(result.Succeeded);
        Assert.Equal(expectedAttempts, attempts);
        Assert.Equal(ErrorPolicy.Classify(FaultFileSystem.Io(code)), result.Category);
    }

    [Fact]
    public void FatalStorageSafetyAndUnexpectedExceptionsAreNeverSwallowed()
    {
        var fs = new FaultFileSystem();
        var policy = fs.Policy();
        Assert.Equal(ErrorCategory.Storage, Assert.Throws<RunAbortedException>(() =>
            policy.Try<int>(() => throw FaultFileSystem.Io(112), [Left], null, "file", "Copy")).Category);
        Assert.Equal(0, fs.Count("probe", Left));
        var safety = new RunAbortedException(ErrorCategory.Safety, "link");
        Assert.Same(safety, Assert.Throws<RunAbortedException>(() => policy.Try<int>(() => throw safety, [Left], null, "file", "Copy")));
        Assert.Throws<InvalidOperationException>(() => policy.Try<int>(() => throw new InvalidOperationException(), [Left], null, "file", "Copy"));
        fs.ProbeFault = (_, _) => FaultFileSystem.Io(23);
        Assert.Equal(ErrorCategory.Storage, Assert.Throws<RunAbortedException>(() => policy.EnsureRoots([Left], null)).Category);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RootRecoveryIsBoundedAndEvenRestoredRootsRequireAFreshComparison(bool recover)
    {
        var clock = new ManualTimeProvider();
        var fs = new FaultFileSystem { ProbeFault = (_, count) => !recover || count == 1 ? new DirectoryNotFoundException("disconnected") : null };
        var policy = new ErrorPolicy(fs, clock: clock, wait: (delay, control) => { clock.Advance((int)delay.TotalMilliseconds); control?.Checkpoint(); });
        using var control = new OperationControl();
        var failure = Assert.Throws<RunAbortedException>(() => policy.EnsureRoots([Left], control));
        Assert.Equal(recover ? ErrorCategory.StalePlan : ErrorCategory.RootUnavailable, failure.Category);
        Assert.Equal(recover ? 2000 : 30000, clock.GetTimestamp());
        Assert.Equal(1, control.Problems.Retries);
        Assert.Contains(recover ? "restored" : "remained unavailable", failure.Message);
        Assert.Contains("disconnected", failure.InnerException!.Message);
        if (!recover) Assert.Contains(Left, failure.Message);
    }

    [Fact]
    public void ATransportFailureRequiresRecoveryEvenIfCachedRootMetadataStillLooksHealthy()
    {
        var fs = new FaultFileSystem();
        var clock = new ManualTimeProvider();
        var policy = new ErrorPolicy(fs, clock: clock, wait: (delay, _) => clock.Advance((int)delay.TotalMilliseconds));
        using var control = new OperationControl();
        int attempts = 0;
        var failure = Assert.Throws<RunAbortedException>(() => policy.Try<int>(() =>
        {
            attempts++;
            throw FaultFileSystem.Io(53);
        }, [Left, Right], control, "file", "Copy"));
        Assert.Equal(ErrorCategory.StalePlan, failure.Category);
        Assert.Equal(1, attempts);
        Assert.Equal(2000, clock.GetTimestamp());
        Assert.Null(control.Snapshot.WaitingReason);
    }

    [Fact]
    public void RecoveryWithoutAProgressObserverStillHonorsTheTimeoutAndPreservesTheCause()
    {
        var fs = new FaultFileSystem { ProbeFault = (_, _) => new DirectoryNotFoundException("offline") };
        var clock = new ManualTimeProvider();
        var policy = new ErrorPolicy(fs, options: new() { RootRecoveryTimeout = TimeSpan.FromSeconds(2) },
            clock: clock, wait: (delay, _) => clock.Advance((int)delay.TotalMilliseconds));
        var error = Assert.Throws<RunAbortedException>(() => policy.EnsureRoots([Left], null));
        Assert.Equal(ErrorCategory.RootUnavailable, error.Category);
        Assert.Contains("offline", error.InnerException!.Message);
        Assert.Equal(2000, clock.GetTimestamp());
    }

    [Fact]
    public void StopInterruptsRetryAndRootRecoveryWaits()
    {
        using var control = new OperationControl();
        var fs = new FaultFileSystem { ProbeFault = (_, _) => new DirectoryNotFoundException() };
        var policy = new ErrorPolicy(fs, wait: (_, current) => { current!.Stop(); current.Checkpoint(); });
        Assert.ThrowsAny<OperationCanceledException>(() => policy.EnsureRoots([Left], control));
        using var other = new OperationControl();
        var retry = new ErrorPolicy(wait: (_, current) => { current!.Stop(); current.Checkpoint(); });
        Assert.ThrowsAny<OperationCanceledException>(() => retry.Try<int>(() => throw FaultFileSystem.Io(32), [Left], other, "file", "Copy"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DefaultWaitSupportsControlledAndStandaloneCalls(bool controlled)
    {
        using var control = new OperationControl();
        int attempts = 0;
        var policy = new ErrorPolicy(options: new() { FileRetryDelays = [TimeSpan.Zero] });
        Assert.True(policy.Try(() => ++attempts == 1 ? throw FaultFileSystem.Io(32) : true,
            [Left], controlled ? control : null, "file", "Copy").Succeeded);
    }

    [Fact]
    public async Task StopCancelsTheRealWaitWithoutWaitingForItsFullDuration()
    {
        using var control = new OperationControl();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task waiting = Task.Run(() => { entered.SetResult(); control.Wait(TimeSpan.FromSeconds(30)); });
        await entered.Task;
        control.Stop();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void IssueCountersDistinguishEveryOutcomeAndDeduplicateRepeatedUnavailableScopes()
    {
        using var control = new OperationControl();
        foreach (bool directory in new[] { false, true })
        {
            foreach (IssueOutcome outcome in new[] { IssueOutcome.Failed, IssueOutcome.Skipped, IssueOutcome.AlreadyAbsent })
                control.Issue(new(ErrorCategory.Missing, outcome, "phase", "Copy", directory ? "folder" : "file", "problem", directory, 10));
        }
        control.Issue(new(ErrorCategory.AccessDenied, IssueOutcome.Failed, "phase", "Copy", "denied", "denied", false, 5));
        var unavailable = new OperationIssue(ErrorCategory.AccessDenied, IssueOutcome.Unavailable, "phase", "Scan", "unknown", "denied");
        control.Issue(unavailable); control.Issue(unavailable);
        control.Issue(unavailable with { Category = ErrorCategory.Missing });
        control.Issue(new(ErrorCategory.Conflict, IssueOutcome.Conflict, "phase", "Plan", "conflict", "conflict"));
        control.Issue(new(ErrorCategory.Configuration, IssueOutcome.Warning, "phase", "Options", "", "warning"));
        Assert.Equal(new OperationProblems(2, 15, 1, 1, 10, 1, 1, 10, 1, 1, Conflicts: 1,
            MissingFiles: 1, InaccessibleFiles: 1, Warnings: 1), control.Problems);
        Assert.True(control.Problems.HasIssues);
        Assert.True(control.Problems.HasWarnings);
        Assert.Equal(control.Problems, control.Snapshot.Problems);
    }
}
