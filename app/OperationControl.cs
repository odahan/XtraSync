using System.Threading;
using System.IO;

namespace XtraSync;

public enum OperationStage { Scanning, Comparing, Planning, Review, Executing }

public sealed record OperationProgress(string Phase, string Path, long Files, long Folders,
    long Bytes, int Completed, int Total, OperationStage Stage = OperationStage.Scanning,
    OperationStatistics? Planned = null, OperationStatistics? Applied = null,
    long EqualFiles = 0, long DifferentItems = 0, long FileBytes = 0, long FileSize = 0, long Sequence = 0,
    OperationProblems? Problems = null, string? WaitingReason = null)
{
    public double Percentage => Total == 0 ? 0 : Math.Min(100,
        100.0 * (Completed + (FileSize > 0 ? Math.Min(0.99, (double)FileBytes / FileSize) : 0)) / Total);
}

/// <summary>Controls worker checkpoints and throttles progress notifications for the UI.</summary>
public sealed class OperationControl : IDisposable
{
    private readonly ManualResetEventSlim gate = new(true);
    private readonly CancellationTokenSource cancellation = new();
    private readonly Action<OperationProgress>? report;
    private readonly TimeProvider timeProvider;
    private readonly Action<OperationIssue>? issueHandler;
    private readonly HashSet<string> countedIssues = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> readErrorPaths = new(StringComparer.OrdinalIgnoreCase);
    public OperationProblems Problems { get; private set; } = new();
    private string phase = "";
    private string path = "";
    private long files, folders, bytes, lastReport;
    private int completed, total;
    private OperationStage stage;
    private OperationStatistics? planned, applied;
    private long equalFiles, differentItems, fileBytes, fileSize, sequence;
    private string? waitingReason;

    public OperationProgress Snapshot { get; private set; } = new("Ready", "", 0, 0, 0, 0, 0);
    public OperationStatistics AppliedStatistics => applied ?? new();

    /// <summary>Creates operation controls with an optional clock for deterministic progress reporting.</summary>
    public OperationControl(Action<OperationProgress>? report = null, TimeProvider? timeProvider = null,
        Action<OperationIssue>? issueHandler = null)
    {
        this.report = report;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.issueHandler = issueHandler;
        lastReport = this.timeProvider.GetTimestamp();
    }

    public void Pause() => gate.Reset();
    public void Resume() => gate.Set();
    public void Stop()
    {
        cancellation.Cancel();
        gate.Set();
    }

    public void Checkpoint()
    {
        cancellation.Token.ThrowIfCancellationRequested();
        gate.Wait(cancellation.Token);
        cancellation.Token.ThrowIfCancellationRequested();
    }

    /// <summary>Waits between retries while allowing Stop to cancel and Pause to hold the next attempt.</summary>
    public void Wait(TimeSpan delay)
    {
        Checkpoint();
        if (cancellation.Token.WaitHandle.WaitOne(delay)) cancellation.Token.ThrowIfCancellationRequested();
        Checkpoint();
    }

    public void Waiting(string? reason, string? currentPath = null)
    {
        waitingReason = reason;
        if (currentPath is not null) path = currentPath;
        Publish(true);
    }

    public void Issue(OperationIssue issue)
    {
        if (issue.Outcome is IssueOutcome.Retrying or IssueOutcome.Recovered or IssueOutcome.Warning
            || countedIssues.Add($"{issue.Outcome}|{(issue.Outcome == IssueOutcome.Unavailable ? "" : issue.Category.ToString())}|{issue.Operation}|{issue.Path}")) Problems = Problems.Add(issue);
        issueHandler?.Invoke(issue);
        Publish(true);
    }

    /// <summary>Accumulates distinct faulty file reads across all phases, including reads recovered by retries.</summary>
    public void ReadFailure(string file, Exception exception, int maximum)
    {
        if (ErrorPolicy.Classify(exception) != ErrorCategory.TransientIO) return;
        string fullPath = Path.GetFullPath(file);
        if (readErrorPaths.Add(fullPath))
            Issue(new(ErrorCategory.TransientIO, IssueOutcome.ReadError, phase, "Read file", fullPath,
                $"I/O read errors on {readErrorPaths.Count} / {maximum} distinct files. Successful retries do not reset this run counter.",
                false, Exception: exception));
        if (readErrorPaths.Count >= maximum)
            throw new RunAbortedException(ErrorCategory.ReadErrorLimit,
                $"Global read-error limit reached: {readErrorPaths.Count} distinct files (limit {maximum}). " +
                "Operation stopped to prevent further work on unreliable data. See the log.", exception);
    }

    public void Begin(string name, int operationCount = 0, OperationStage stage = OperationStage.Scanning,
        OperationStatistics? planned = null)
    {
        phase = name;
        files = folders = bytes = completed = 0;
        total = operationCount;
        path = "";
        this.stage = stage;
        this.planned = planned;
        applied = planned is null ? null : new();
        equalFiles = differentItems = fileBytes = fileSize = 0;
        Publish(true);
    }

    public void Scanned(string currentPath, Entry entry)
    {
        path = currentPath;
        if (entry.IsDirectory) folders++;
        else { files++; bytes += entry.Size; }
        Publish();
    }

    public void Current(string currentPath, long size = 0)
    {
        path = currentPath;
        fileBytes = 0;
        fileSize = size;
        Publish();
    }

    public void Copied(int count)
    {
        bytes += count;
        fileBytes += count;
        Publish();
    }

    public void Finished(FileAction action, OperationStatistics? statistics = null)
    {
        completed++;
        if (action.Kind is "Create folder" or "Delete folder") folders++;
        else files++;
        if (statistics is not null) applied = (applied ?? new()).Add(statistics);
        fileBytes = fileSize = 0;
        Publish();
    }

    public void Compared(string currentPath, Entry? left, Entry? right, string status)
    {
        foreach (Entry? entry in new[] { left, right })
        {
            if (entry is null) continue;
            if (entry.IsDirectory) folders++;
            else { files++; bytes += entry.Size; }
        }
        if (status == "Equal") equalFiles++;
        else if (status != "Folder") differentItems++;
        ProcessedItem(currentPath);
    }

    public void ProcessedItem(string currentPath)
    {
        path = currentPath;
        fileBytes = fileSize = 0;
        completed++;
        Publish();
    }

    public void Publish(bool force = false)
    {
        long now = timeProvider.GetTimestamp();
        if (!force && timeProvider.GetElapsedTime(lastReport, now) < TimeSpan.FromMilliseconds(80)) return;
        lastReport = now;
        Snapshot = new(phase, path, files, folders, bytes, completed, total, stage, planned, applied,
            equalFiles, differentItems, fileBytes, fileSize, ++sequence,
            Problems == new OperationProblems() ? null : Problems, waitingReason);
        report?.Invoke(Snapshot);
    }

    public void Dispose()
    {
        gate.Dispose();
        cancellation.Dispose();
    }
}

public static class SizeFormatter
{
    /// <summary>Formats binary-scaled sizes with units from bytes through terabytes.</summary>
    public static string Format(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:N2} {units[unit]}";
    }
}
