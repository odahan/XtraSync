using System.IO;
using System.Text;
using System.Globalization;

namespace XtraSync;

/// <summary>Owns the two rotating logs and timestamped reports beside the actual executable.</summary>
public sealed class RunLogStore(string directory, TimeProvider? timeProvider = null)
{
    public string DirectoryPath { get; } = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    public string LogPath => Path.Combine(DirectoryPath, "XtraSync.log");
    public string BackupPath => Path.Combine(DirectoryPath, "XtraSync.bak.log");
    public string LockPath => Path.Combine(DirectoryPath, "XtraSync.run.lock");

    public static RunLogStore ForExecutable() => new(Path.GetDirectoryName(Environment.ProcessPath
        ?? throw new IOException("The executable location is unavailable."))!);

    public RunLogSession Start(RunContext context)
    {
        Directory.CreateDirectory(DirectoryPath);
        var runLock = new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
            1, FileOptions.DeleteOnClose);
        try
        {
            if (File.Exists(LogPath))
            {
                File.Delete(BackupPath);
                File.Move(LogPath, BackupPath);
            }
            return new RunLogSession(this, clock, context, runLock);
        }
        catch
        {
            runLock.Dispose();
            throw;
        }
    }

    /// <summary>Prevents active logs and generated reports from participating in synchronization.</summary>
    public bool IsArtifact(string path)
    {
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), DirectoryPath, StringComparison.OrdinalIgnoreCase)) return false;
        string name = Path.GetFileName(path);
        return name.Equals("XtraSync.log", StringComparison.OrdinalIgnoreCase)
            || name.Equals(HelpDocument.FileName, StringComparison.OrdinalIgnoreCase)
            || name.Equals("XtraSync.bak.log", StringComparison.OrdinalIgnoreCase)
            || name.Equals("XtraSync.run.lock", StringComparison.OrdinalIgnoreCase)
            || System.Text.RegularExpressions.Regex.IsMatch(name,
                @"^XtraSync-\d{4}-\d{2}-\d{2}-\d{2}-\d{2}-\d{2}(?:-\d+)?\.html(?:\.tmp)?$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }
}

/// <summary>Streams diagnostics immediately and always writes the final summary after report generation.</summary>
public sealed class RunLogSession : IDisposable
{
    private readonly RunLogStore store;
    private readonly TimeProvider clock;
    private readonly RunContext context;
    private readonly FileStream runLock;
    private readonly StreamWriter writer;
    private readonly object gate = new();
    private readonly DateTimeOffset started;
    private readonly List<RunMessage> messages = [];
    private readonly Dictionary<string, OperationProgress> phases = [];
    private OperationStatistics planned = new(), applied = new();
    private long transferred;
    private OperationProblems problems = new();
    private string lastPhase = "";

    internal RunLogSession(RunLogStore store, TimeProvider clock, RunContext context, FileStream runLock)
    {
        this.store = store;
        this.clock = clock;
        this.context = context;
        this.runLock = runLock;
        started = clock.GetLocalNow();
        writer = new StreamWriter(new FileStream(store.LogPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
        try { Info($"Started {context.Operation}\nLeft: {context.Left}\nRight: {context.Right}\nExclusions: {context.Exclusions}\nOptions: {context.Options}"); }
        catch { writer.Dispose(); throw; }
    }

    public void Info(string message) => Write("INFO", message);
    public void Warning(string message) => Write("WARNING", message);
    public void Error(string message, Exception exception) => Write("ERROR", message + Environment.NewLine + exception);

    public void Diagnostic(OperationIssue issue)
    {
        string message = $"{issue.Message}\nCategory: {issue.Category}; Result: {issue.Outcome}; Phase: {issue.Phase}; " +
            $"Operation: {issue.Operation}; Path: {issue.Path}; Entry type: {(issue.IsDirectory is null ? "unknown" : issue.IsDirectory.Value ? "folder" : "file")}; Bytes: {issue.Bytes}";
        if (issue.Outcome == IssueOutcome.Failed) Error(message, issue.Exception ?? new IOException(issue.Message));
        else if (issue.Outcome is IssueOutcome.Recovered or IssueOutcome.AlreadyAbsent) Info(message);
        else Warning(message + (issue.Exception is null ? "" : Environment.NewLine + issue.Exception));
    }

    private void Write(string level, string message)
    {
        lock (gate)
        {
            var entry = new RunMessage(clock.GetLocalNow(), level, message);
            messages.Add(entry);
            writer.WriteLine($"{entry.Time.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture)} [{level}] {message}");
        }
    }

    public void Observe(OperationProgress progress)
    {
        lock (gate)
        {
            phases[progress.Phase] = progress;
            if (progress.Planned is not null) planned = progress.Planned;
            if (progress.Applied is not null) applied = progress.Applied;
            if (progress.Stage == OperationStage.Executing) transferred = progress.Bytes;
            if (progress.Problems is not null) problems = progress.Problems;
            if (progress.Phase == lastPhase) return;
            lastPhase = progress.Phase;
            Info($"Phase: {progress.Phase}");
        }
    }

    public (string? HtmlPath, string? ReportError) Complete(string outcome, bool createHtml)
    {
        lock (gate)
        {
            Info(outcome);
            string? reportPath = null, reportError = null;
            RunSummary Summary() => new(context, started, clock.GetLocalNow(), outcome,
                planned, applied, transferred, phases.Values.ToArray(), messages.ToArray(), problems);
            if (createHtml)
            {
                try
                {
                    string stem = Path.Combine(store.DirectoryPath, "XtraSync-" + started.ToString("yyyy-MM-dd-HH-mm-ss", CultureInfo.InvariantCulture));
                    reportPath = stem + ".html";
                    for (int suffix = 2; File.Exists(reportPath); suffix++) reportPath = $"{stem}-{suffix}.html";
                    string temporary = reportPath + ".tmp";
                    bool created = false;
                    try
                    {
                        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        {
                            created = true;
                            using var output = new StreamWriter(stream, new UTF8Encoding(false));
                            output.Write(RunReport.Html(Summary()));
                        }
                        File.Move(temporary, reportPath);
                    }
                    finally { if (created && File.Exists(temporary)) File.Delete(temporary); }
                    Info($"HTML report: {reportPath}");
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    reportPath = null;
                    reportError = $"HTML report could not be saved: {exception.Message}";
                    Error(reportError, exception);
                }
            }
            writer.Write(RunReport.Text(Summary()));
            return (reportPath, reportError);
        }
    }

    public void Dispose()
    {
        try { writer.Dispose(); }
        finally { runLock.Dispose(); }
    }
}
