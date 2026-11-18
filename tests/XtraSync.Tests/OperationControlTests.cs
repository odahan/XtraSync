using System.Globalization;

namespace XtraSync.Tests;

public sealed class OperationControlTests
{
    [Fact]
    public void ReportsScanTotalsAndResetsEveryPhase()
    {
        var updates = new List<OperationProgress>();
        using var control = new OperationControl(updates.Add);
        control.Begin("Scan");
        control.Scanned("a.txt", new Entry("a.txt", false, 12, 0));
        control.Scanned("folder", new Entry("folder", true, 0, 0));
        control.Publish(true);
        Assert.Equal(new OperationProgress("Scan", "folder", 1, 1, 12, 0, 0, Sequence: updates[^1].Sequence), updates[^1]);
        control.Begin("Copy", 3);
        Assert.Equal(new OperationProgress("Copy", "", 0, 0, 0, 0, 3, Sequence: updates[^1].Sequence), updates[^1]);
        control.Current("destination");
        control.Copied(42);
        control.Finished(new FileAction("Copy file", "a"));
        control.Finished(new FileAction("Create folder", "b"));
        control.Finished(new FileAction("Delete folder", "c"));
        control.Publish(true);
        Assert.Equal(new OperationProgress("Copy", "destination", 1, 2, 42, 3, 3, Sequence: updates[^1].Sequence), updates[^1]);
    }

    [Fact]
    public async Task PauseBlocksCheckpointUntilResume()
    {
        using var control = new OperationControl();
        using var entered = new ManualResetEventSlim();
        control.Pause();
        Task checkpoint = Task.Run(() => { entered.Set(); control.Checkpoint(); });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            Assert.NotSame(checkpoint, await Task.WhenAny(checkpoint, Task.Delay(100)));
        }
        finally
        {
            control.Resume();
        }
        await checkpoint.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task StopUnblocksPausedCheckpointAndRemainsCancelled()
    {
        using var control = new OperationControl();
        using var entered = new ManualResetEventSlim();
        control.Pause();
        Task checkpoint = Task.Run(() => { entered.Set(); control.Checkpoint(); });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        control.Stop();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => checkpoint.WaitAsync(TimeSpan.FromSeconds(5)));
        control.Resume();
        Assert.ThrowsAny<OperationCanceledException>(control.Checkpoint);
    }

    [Fact]
    public void ReportsAreThrottledAtEightyMilliseconds()
    {
        var clock = new ManualTimeProvider();
        var updates = new List<OperationProgress>();
        using var control = new OperationControl(updates.Add, clock);
        control.Begin("scan");
        clock.Advance(79);
        control.Current("suppressed");
        Assert.Single(updates);
        clock.Advance(1);
        control.Current("reported");
        Assert.Equal(2, updates.Count);
        Assert.Equal("reported", updates[^1].Path);
        control.Publish();
        Assert.Equal(2, updates.Count);
    }

    [Fact]
    public void ForcedReportsBypassThrottlingAndCallbackIsOptional()
    {
        var updates = new List<OperationProgress>();
        using var control = new OperationControl(updates.Add);
        control.Begin("phase");
        control.Publish(true);
        Assert.Equal(2, updates.Count);
        using var silent = new OperationControl();
        silent.Begin("phase");
        silent.Checkpoint();
        silent.Publish(true);
    }

    [Theory]
    [InlineData(0, "0.00 B")]
    [InlineData(1023, "1,023.00 B")]
    [InlineData(1024, "1.00 KB")]
    [InlineData(1536, "1.50 KB")]
    [InlineData(1048576, "1.00 MB")]
    [InlineData(1073741824, "1.00 GB")]
    [InlineData(1099511627776, "1.00 TB")]
    [InlineData(1125899906842624, "1,024.00 TB")]
    public void SizesUseBinaryUnitsAndStopAtTerabytes(long bytes, string expected)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            Assert.Equal(expected, SizeFormatter.Format(bytes));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
