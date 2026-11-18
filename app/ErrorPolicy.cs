using System.IO;

namespace XtraSync;

public enum ErrorCategory { Missing, AccessDenied, Locked, InvalidPath, ChangedDirectory, TransientIO, RootUnavailable, Storage, StalePlan, Safety, Dependency, Conflict, Configuration, ReadErrorLimit }
public enum IssueOutcome { Failed, Skipped, AlreadyAbsent, Unavailable, Retrying, Recovered, Conflict, Warning, ReadError }

public sealed record OperationIssue(ErrorCategory Category, IssueOutcome Outcome, string Phase, string Operation,
    string Path, string Message, bool? IsDirectory = null, long Bytes = 0, Exception? Exception = null);

/// <summary>Separates attempted, skipped and already absent work from successful filesystem changes.</summary>
public sealed record OperationProblems(long FailedFiles = 0, long FailedBytes = 0, long FailedFolders = 0,
    long SkippedFiles = 0, long SkippedBytes = 0, long SkippedFolders = 0,
    long AlreadyAbsentFiles = 0, long AlreadyAbsentBytes = 0, long AlreadyAbsentFolders = 0,
    long UnavailableEntries = 0, long Retries = 0, long Recovered = 0, long Conflicts = 0,
    long MissingFiles = 0, long InaccessibleFiles = 0, long Warnings = 0, long ReadErrorFiles = 0)
{
    public bool HasIssues => FailedFiles + FailedFolders + SkippedFiles + SkippedFolders + UnavailableEntries + Conflicts > 0;
    public bool HasWarnings => HasIssues || Retries > 0 || Warnings > 0 || ReadErrorFiles > 0;

    public OperationProblems Add(OperationIssue issue) => issue.Outcome switch
    {
        IssueOutcome.Failed => issue.IsDirectory == true ? this with { FailedFolders = FailedFolders + 1 }
            : this with
            {
                FailedFiles = FailedFiles + 1,
                FailedBytes = FailedBytes + issue.Bytes,
                MissingFiles = MissingFiles + (issue.Category == ErrorCategory.Missing ? 1 : 0),
                InaccessibleFiles = InaccessibleFiles + (issue.Category is ErrorCategory.AccessDenied or ErrorCategory.Locked ? 1 : 0)
            },
        IssueOutcome.Skipped => issue.IsDirectory == true ? this with { SkippedFolders = SkippedFolders + 1 }
            : this with { SkippedFiles = SkippedFiles + 1, SkippedBytes = SkippedBytes + issue.Bytes },
        IssueOutcome.AlreadyAbsent => issue.IsDirectory == true ? this with { AlreadyAbsentFolders = AlreadyAbsentFolders + 1 }
            : this with { AlreadyAbsentFiles = AlreadyAbsentFiles + 1, AlreadyAbsentBytes = AlreadyAbsentBytes + issue.Bytes },
        IssueOutcome.Unavailable => this with { UnavailableEntries = UnavailableEntries + 1 },
        IssueOutcome.Retrying => this with { Retries = Retries + 1 },
        IssueOutcome.Recovered => this with { Recovered = Recovered + 1 },
        IssueOutcome.Warning => this with { Warnings = Warnings + 1 },
        IssueOutcome.ReadError => this with { ReadErrorFiles = ReadErrorFiles + 1 },
        _ => this with { Conflicts = Conflicts + 1 }
    };
}

public sealed record ErrorPolicyOptions
{
    public IReadOnlyList<TimeSpan> FileRetryDelays { get; init; } = [TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(750)];
    public TimeSpan RootRecoveryTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan RootRetryInterval { get; init; } = TimeSpan.FromSeconds(2);
    public int MaximumReadErrorFiles { get; init; } = 10;
}

public sealed class RunAbortedException(ErrorCategory category, string message, Exception? inner = null) : IOException(message, inner)
{
    public ErrorCategory Category { get; } = category;
}

public sealed record AttemptResult<T>(T? Value, Exception? Error = null, ErrorCategory Category = ErrorCategory.TransientIO)
{
    public bool Succeeded => Error is null;
}

/// <summary>Retries transient item errors, bounds root recovery and never treats missing access as missing data.</summary>
public sealed class ErrorPolicy
{
    public FileSystemAccess FileSystem { get; }
    public ErrorPolicyOptions Options { get; }
    private readonly TimeProvider clock;
    private readonly Action<TimeSpan, OperationControl?> wait;

    public ErrorPolicy(FileSystemAccess? fileSystem = null, ErrorPolicyOptions? options = null,
        TimeProvider? clock = null, Action<TimeSpan, OperationControl?>? wait = null)
    {
        FileSystem = fileSystem ?? new();
        Options = options ?? new();
        if (Options.RootRecoveryTimeout < TimeSpan.Zero || Options.RootRetryInterval <= TimeSpan.Zero
            || Options.MaximumReadErrorFiles < 1
            || Options.FileRetryDelays.Any(delay => delay < TimeSpan.Zero)) throw new ArgumentOutOfRangeException(nameof(options));
        this.clock = clock ?? TimeProvider.System;
        this.wait = wait ?? ((delay, control) =>
        {
            if (control is null) Thread.Sleep(delay);
            else control.Wait(delay);
        });
    }

    public ErrorPolicy WithReadErrorLimit(int maximum) => new(FileSystem,
        Options with { MaximumReadErrorFiles = maximum }, clock, wait);

    public static ErrorCategory Classify(Exception exception)
    {
        if (exception is RunAbortedException aborted) return aborted.Category;
        if (exception is UnauthorizedAccessException) return ErrorCategory.AccessDenied;
        if (exception is FileNotFoundException or DirectoryNotFoundException) return ErrorCategory.Missing;
        if (exception is PathTooLongException) return ErrorCategory.InvalidPath;
        return (exception.HResult & 0xffff) switch
        {
            2 or 3 => ErrorCategory.Missing,
            5 => ErrorCategory.AccessDenied,
            32 or 33 => ErrorCategory.Locked,
            19 or 23 or 27 or 29 or 30 or 31 or 39 or 112 or 1117 or 1392 or 1393 or 1005 => ErrorCategory.Storage,
            15 or 20 or 21 or 53 or 55 or 64 or 67 or 70 or 72 or 88 or 121 or 1112 or 1167 or 1231 or 1232 => ErrorCategory.RootUnavailable,
            123 or 206 => ErrorCategory.InvalidPath,
            145 or 80 or 183 => ErrorCategory.ChangedDirectory,
            _ => ErrorCategory.TransientIO
        };
    }

    public AttemptResult<T> Try<T>(Func<T> operation, IEnumerable<string> roots, OperationControl? control,
        string path, string action)
    {
        int retries = 0;
        while (true)
        {
            control?.Checkpoint();
            try
            {
                T result = operation();
                if (retries > 0) control?.Issue(new(ErrorCategory.TransientIO, IssueOutcome.Recovered,
                    control.Snapshot.Phase, action, path, $"Operation succeeded after {retries} retries."));
                return new(result);
            }
            catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException) && exception is not RunAbortedException)
            {
                ErrorCategory category = Classify(exception);
                if (category == ErrorCategory.Storage)
                    throw new RunAbortedException(category, $"Storage failure at {path}. No further changes will be made.", exception);
                EnsureRoots(roots, control, category == ErrorCategory.RootUnavailable, exception);
                if (category is not (ErrorCategory.Locked or ErrorCategory.TransientIO) || retries >= Options.FileRetryDelays.Count)
                    return new(default, exception, category);
                control?.Issue(new(category, IssueOutcome.Retrying, control.Snapshot.Phase, action, path,
                    $"Retry {retries + 1} of {Options.FileRetryDelays.Count}: {exception.Message}", Exception: exception));
                control?.Waiting($"Waiting to retry {action} on {path}", path);
                try { wait(Options.FileRetryDelays[retries++], control); }
                finally { control?.Waiting(null); }
            }
        }
    }

    private bool RootsAvailable(IEnumerable<string> roots, out string? failedRoot, out Exception? failure)
    {
        failedRoot = null;
        failure = null;
        foreach (string root in roots)
        {
            try { FileSystem.ProbeRoot(root); }
            catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException) && exception is not RunAbortedException)
            {
                if (Classify(exception) == ErrorCategory.Storage)
                    throw new RunAbortedException(ErrorCategory.Storage, $"Root storage failure at {root}. Operation stopped.", exception);
                failedRoot = root;
                failure = exception;
                return false;
            }
        }
        return true;
    }

    public void EnsureRoots(IEnumerable<string> roots, OperationControl? control, bool forceRecovery = false, Exception? cause = null)
    {
        string[] paths = roots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (RootsAvailable(paths, out string? failedRoot, out Exception? failure) && !forceRecovery) return;
        failure ??= cause;
        long started = clock.GetTimestamp();
        control?.Issue(new(ErrorCategory.RootUnavailable, IssueOutcome.Retrying, control.Snapshot.Phase,
            "Check roots", failedRoot ?? string.Join(" | ", paths), "A root is unavailable. Waiting for recovery; writes are suspended.", Exception: failure));
        bool restored;
        try { restored = WaitForRecovery(paths, control, started, ref failedRoot, ref failure); }
        finally { control?.Waiting(null); }
        throw new RunAbortedException(restored ? ErrorCategory.StalePlan : ErrorCategory.RootUnavailable,
            restored ? "Root access was restored. Restart comparison and review a fresh plan before making further changes."
                : $"Source or destination remained unavailable for {Options.RootRecoveryTimeout.TotalSeconds:N0} seconds: {failedRoot ?? string.Join(" | ", paths)}. Operation stopped.", failure);
    }

    private bool WaitForRecovery(string[] paths, OperationControl? control, long started, ref string? failedRoot, ref Exception? failure)
    {
        while (clock.GetElapsedTime(started) < Options.RootRecoveryTimeout)
        {
            TimeSpan remaining = Options.RootRecoveryTimeout - clock.GetElapsedTime(started);
            control?.Waiting($"Waiting for root recovery: {remaining.TotalSeconds:N0} s remaining. Writes are suspended.");
            wait(remaining < Options.RootRetryInterval ? remaining : Options.RootRetryInterval, control);
            control?.Checkpoint();
            if (RootsAvailable(paths, out string? latestRoot, out Exception? latestFailure)) return true;
            failedRoot = latestRoot;
            failure = latestFailure;
        }
        return false;
    }
}

/// <summary>Provides filesystem operations as replaceable boundaries for deterministic failure/recovery tests.</summary>
public class FileSystemAccess
{
    public virtual bool DirectoryExists(string path) => Directory.Exists(path);
    public virtual IEnumerable<string> Enumerate(string directory) => Directory.EnumerateFileSystemEntries(directory);
    public virtual FileAttributes Attributes(string path) => File.GetAttributes(path);
    public virtual long Length(string path) => new FileInfo(path).Length;
    public virtual long FileTime(string path) => File.GetLastWriteTimeUtc(path).ToFileTimeUtc();
    public virtual void CreateDirectory(string path) => Directory.CreateDirectory(path);
    public virtual void DeleteFile(string path) => File.Delete(path);
    public virtual void DeleteDirectory(string path) => Directory.Delete(path, false);
    public virtual Stream OpenRead(string path) => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    public virtual Stream CreateNew(string path) => new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    public virtual void SetFileTime(string path, long time) => File.SetLastWriteTimeUtc(path, DateTime.FromFileTimeUtc(time));
    public virtual void Move(string source, string destination) => File.Move(source, destination, true);

    public virtual void ProbeRoot(string path)
    {
        FileAttributes attributes = Attributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new RunAbortedException(ErrorCategory.Safety, $"Linked folders are not supported: {path}");
        if ((attributes & FileAttributes.Directory) == 0) throw new DirectoryNotFoundException(path);
        using var entries = Enumerate(path).GetEnumerator();
        entries.MoveNext();
    }
}

/// <summary>Retains unknown scopes beside known entries so an incomplete scan cannot authorize deletions.</summary>
public sealed class ScanSnapshot : Dictionary<string, Entry>
{
    public HashSet<string> UnavailablePaths { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ScanSnapshot() : base(StringComparer.OrdinalIgnoreCase) { }
}

public sealed record ExecutionResult(OperationProblems Problems, OperationStatistics Applied);
