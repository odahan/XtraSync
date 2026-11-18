using System.IO;

namespace XtraSync;

/// <summary>Applies retry decisions without allowing unknown data or failed prerequisites to authorize writes.</summary>
internal static class ResilientOperations
{
    internal static bool Contains(string path, string scope) => scope.Length == 0
        || path.Equals(scope, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(Path.EndsInDirectorySeparator(scope) ? scope : scope + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    internal static bool Overlaps(string first, string second) => Contains(first, second) || Contains(second, first);

    internal static void RejectLinkedAncestors(string path, FileSystemAccess fs)
    {
        for (string? directory = path; directory is not null; directory = Path.GetDirectoryName(directory))
            if ((fs.Attributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new RunAbortedException(ErrorCategory.Safety, $"Linked folders are not supported: {directory}");
    }

    private static T Read<T>(Func<T> read, string path, ErrorPolicy policy, OperationControl control)
    {
        try { return read(); }
        catch (IOException exception)
        {
            control.ReadFailure(path, exception, policy.Options.MaximumReadErrorFiles);
            throw;
        }
    }

    private static Entry ReadEntry(FileSystemAccess fs, string root, string path, ErrorPolicy policy, OperationControl control)
    {
        FileAttributes attributes = fs.Attributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new RunAbortedException(ErrorCategory.Safety, $"A linked item is not supported: {path}");
        bool folder = (attributes & FileAttributes.Directory) != 0;
        return new(Path.GetRelativePath(root, path), folder,
            folder ? 0 : Read(() => fs.Length(path), path, policy, control),
            folder ? fs.FileTime(path) : Read(() => fs.FileTime(path), path, policy, control));
    }

    internal static ScanSnapshot Scan(string root, bool recursive, OperationControl control,
        string exclusions, HashSet<string>? protectedFolders, string fileExclusions, string folderExclusions,
        RunLogStore? artifacts, HashSet<string>? runtimeFolders, ErrorPolicy policy)
    {
        var common = ExclusionList.Matcher(exclusions);
        var files = ExclusionList.Matcher(fileExclusions);
        var folders = ExclusionList.Matcher(folderExclusions);
        var result = new ScanSnapshot();
        var pending = new Stack<string>();
        pending.Push(root);
        void Protect(string relative, HashSet<string>? protectedSet)
        {
            for (string? parent = Path.GetDirectoryName(relative); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
                protectedSet?.Add(parent);
        }
        void Unavailable(string path, Exception exception, ErrorCategory category, bool? folder)
        {
            string relative = Path.GetRelativePath(root, path);
            if (relative == ".") relative = "";
            result.UnavailablePaths.Add(relative);
            control.Issue(new(category, IssueOutcome.Unavailable, control.Snapshot.Phase, "Scan", path,
                "Entry could not be scanned. This scope is unknown and will be protected from synchronization.", folder, Exception: exception));
        }
        while (pending.TryPop(out string? directory))
        {
            control.Checkpoint();
            var listing = policy.Try(() => policy.FileSystem.Enumerate(directory).ToArray(), [root], control, directory, "List folder");
            if (!listing.Succeeded) { Unavailable(directory, listing.Error!, listing.Category, true); continue; }
            foreach (string path in listing.Value!)
            {
                control.Checkpoint();
                string relative = Path.GetRelativePath(root, path);
                if (System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(path), @"\.xtrasync-[a-f0-9]{32}\.tmp$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)
                    || artifacts?.IsArtifact(path) == true) { Protect(relative, runtimeFolders); continue; }
                if (common(relative)) { Protect(relative, protectedFolders); continue; }
                var attributes = policy.Try(() => policy.FileSystem.Attributes(path), [root], control, path, "Read attributes");
                if (!attributes.Succeeded) { Unavailable(path, attributes.Error!, attributes.Category, null); continue; }
                bool folder = (attributes.Value & FileAttributes.Directory) != 0;
                if (folder ? folders(relative) : files(relative)) { Protect(relative, protectedFolders); continue; }
                if (!recursive && folder) continue;
                if ((attributes.Value & FileAttributes.ReparsePoint) != 0)
                    throw new RunAbortedException(ErrorCategory.Safety, $"Linked files and folders are not supported: {path}");
                var metadata = policy.Try(() => ReadEntry(policy.FileSystem, root, path, policy, control), [root], control, path, "Read metadata");
                if (!metadata.Succeeded) { Unavailable(path, metadata.Error!, metadata.Category, folder); continue; }
                result.Add(relative, metadata.Value!);
                control.Scanned(path, metadata.Value!);
                if (folder) pending.Push(path);
            }
        }
        control.Publish(true);
        return result;
    }

    private static HashSet<string> Revalidate(MirrorPlan plan, OperationControl control, ErrorPolicy policy)
    {
        var unknown = new HashSet<string>(plan.UnavailablePaths ?? new HashSet<string>(), StringComparer.OrdinalIgnoreCase);
        bool cleanup = plan.DeleteExcludedFromDestination && !plan.BothWays;
        foreach (var side in new[] { (Root: plan.Source, Before: plan.SourceSnapshot, Filter: true),
            (Root: plan.Destination, Before: plan.DestinationSnapshot, Filter: !cleanup) })
        {
            ScanSnapshot after = FolderService.Scan(side.Root, plan.IncludeSubfolders, control,
                side.Filter ? plan.Exclusions : "", fileExclusions: side.Filter ? plan.FileExclusions : "",
                folderExclusions: side.Filter ? plan.FolderExclusions : "", runArtifacts: plan.RunArtifacts, errorPolicy: policy);
            unknown.UnionWith(after.UnavailablePaths);
            foreach (Entry old in side.Before.Values.OrderBy(entry => entry.RelativePath.Length))
            {
                if (unknown.Any(scope => Contains(old.RelativePath, scope))) continue;
                if (!after.TryGetValue(old.RelativePath, out Entry? current))
                {
                    bool referencedFile = !old.IsDirectory && plan.Actions.Any(action => action.RelativePath.Equals(old.RelativePath, StringComparison.OrdinalIgnoreCase));
                    if (referencedFile) continue;
                    unknown.Add(old.RelativePath);
                    control.Issue(new(ErrorCategory.Missing, IssueOutcome.Unavailable, control.Snapshot.Phase, "Scan",
                        Path.Combine(side.Root, old.RelativePath), "Entry disappeared after preview. Its scope is protected.", old.IsDirectory, old.Size));
                    continue;
                }
                if (old.IsDirectory != current.IsDirectory || (!old.IsDirectory && old != current)) Stale(old.RelativePath);
            }
            foreach (string path in after.Keys)
                if (!side.Before.ContainsKey(path) && !unknown.Any(scope => Overlaps(path, scope))) Stale(path);
        }
        return unknown;
    }

    private static void Stale(string path) => throw new RunAbortedException(ErrorCategory.StalePlan,
        $"The folders changed after preview at {path}. Compare and preview again.");

    private static Entry? OptionalEntry(FileSystemAccess fs, string root, string path, ErrorPolicy policy,
        IEnumerable<string> roots, OperationControl control)
    {
        try { return ReadEntry(fs, root, path, policy, control); }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            policy.EnsureRoots(roots, control);
            return null;
        }
    }

    internal static ExecutionResult Execute(MirrorPlan plan, OperationControl control, ErrorPolicy policy)
    {
        FileSystemAccess fs = policy.FileSystem;
        string[] roots = [plan.Source, plan.Destination];
        control.Begin("Validating preview");
        FolderService.ValidateRoots(plan.Source, plan.Destination, fs);
        policy.EnsureRoots(roots, control);
        HashSet<string> unknown = Revalidate(plan, control, policy);
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var removed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var staged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var failedPreparations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var replacements = plan.Actions.Where(action => action.Kind == "Copy file"
                && (action.Reverse ? plan.SourceSnapshot : plan.DestinationSnapshot)
                    .TryGetValue(action.RelativePath, out var existing) && existing.IsDirectory)
            .ToDictionary(action => Path.Combine(action.Reverse ? plan.Source : plan.Destination, action.RelativePath),
                action => action, StringComparer.OrdinalIgnoreCase);
        control.Begin(plan.BothWays ? "Synchronizing" : "Mirroring", plan.Actions.Count,
            OperationStage.Executing, OperationStatistics.FromPlan(plan));
        void Problem(FileAction action, string target, ErrorCategory category, IssueOutcome outcome, string message, Exception? exception = null)
        {
            OperationStatistics count = OperationStatistics.ForAction(plan, action);
            control.Issue(new(category, outcome, control.Snapshot.Phase, action.Kind, target, message,
                action.Kind.EndsWith("folder", StringComparison.Ordinal), count.CopyBytes + count.DeleteBytes, exception));
        }
        AttemptResult<T> Attempt<T>(Func<T> work, FileAction action, string target, string path, string operation)
        {
            try
            {
                return policy.Try(() => { policy.EnsureRoots(roots, control); return work(); },
                    roots, control, path, operation);
            }
            catch (RunAbortedException exception)
            {
                Problem(action, target, exception.Category, IssueOutcome.Failed, "Run aborted; a fresh comparison is required before further writes.", exception);
                throw;
            }
        }
        try
        {
            foreach (FileAction action in plan.Actions)
            {
                control.Checkpoint();
                string sourceRoot = action.Reverse ? plan.Destination : plan.Source;
                string targetRoot = action.Reverse ? plan.Source : plan.Destination;
                string target = Path.Combine(targetRoot, action.RelativePath);
                var sourceSnapshot = action.Reverse ? plan.DestinationSnapshot : plan.SourceSnapshot;
                var targetSnapshot = action.Reverse ? plan.SourceSnapshot : plan.DestinationSnapshot;
                OperationStatistics statistics = OperationStatistics.ForAction(plan, action);
                control.Current(target, statistics.CopyBytes);
                if (unknown.Any(scope => Contains(action.RelativePath, scope)
                        || (action.Kind != "Create folder" && Contains(scope, action.RelativePath)))
                    || blocked.Any(scope => Overlaps(target, scope)))
                {
                    if (!failedPreparations.Contains(target) || action.Kind != "Copy file")
                        Problem(action, target, ErrorCategory.Dependency, IssueOutcome.Skipped,
                            "Skipped because a scope is unknown or a prerequisite failed. Existing destination data is preserved.");
                    control.ProcessedItem(target);
                    continue;
                }

                FileAction? replacement = null;
                for (string? ancestor = target; ancestor is not null && Contains(ancestor, targetRoot); ancestor = Path.GetDirectoryName(ancestor))
                    if (replacements.TryGetValue(ancestor, out replacement)) break;
                if (replacement is not null && !staged.ContainsKey(Path.Combine(targetRoot, replacement.RelativePath)))
                {
                    string replacementTarget = Path.Combine(targetRoot, replacement.RelativePath);
                    string replacementSource = Path.Combine(sourceRoot, replacement.RelativePath);
                    var preparation = Attempt(() => PrepareCopy(replacementSource, replacementTarget,
                        sourceSnapshot[replacement.RelativePath], fs, control, policy), replacement, replacementTarget, replacementSource, "Prepare replacement");
                    if (!preparation.Succeeded)
                    {
                        blocked.Add(replacementTarget);
                        failedPreparations.Add(replacementTarget);
                        Problem(replacement, replacementTarget, preparation.Category, IssueOutcome.Failed,
                            "Replacement source could not be prepared. Its destination subtree will not be deleted.", preparation.Error);
                        Problem(action, target, ErrorCategory.Dependency, IssueOutcome.Skipped, "Replacement preparation failed.");
                        control.ProcessedItem(target);
                        continue;
                    }
                    staged[replacementTarget] = preparation.Value!;
                }

                var result = Attempt(() =>
                {
                    control.Current(target, statistics.CopyBytes);
                    RejectLinkedAncestors(Path.GetDirectoryName(target)!, fs);
                    Entry? actual = OptionalEntry(fs, targetRoot, target, policy, roots, control);
                    targetSnapshot.TryGetValue(action.RelativePath, out Entry? expected);
                    if (removed.Contains(target)) expected = null;
                    if (actual is not null && (expected is null || expected.IsDirectory != actual.IsDirectory
                        || (!actual.IsDirectory && expected != actual))) Stale(target);
                    if (action.Kind.StartsWith("Delete", StringComparison.Ordinal) && actual is null) return (Absent: true, Replaced: false);
                    try
                    {
                        switch (action.Kind)
                        {
                            case "Delete file": fs.DeleteFile(target); break;
                            case "Delete folder": fs.DeleteDirectory(target); break;
                            case "Create folder": fs.CreateDirectory(target); break;
                            case "Copy file":
                                if (!staged.TryGetValue(target, out string? temporary))
                                    temporary = PrepareCopy(Path.Combine(sourceRoot, action.RelativePath), target,
                                        sourceSnapshot[action.RelativePath], fs, control, policy);
                                try
                                {
                                    policy.EnsureRoots(roots, control);
                                    RejectLinkedAncestors(Path.GetDirectoryName(target)!, fs);
                                    Entry? commitTarget = OptionalEntry(fs, targetRoot, target, policy, roots, control);
                                    if ((actual is null) != (commitTarget is null)
                                        || (actual is not null && (actual.IsDirectory != commitTarget!.IsDirectory
                                            || (!actual.IsDirectory && actual != commitTarget)))) Stale(target);
                                    fs.Move(temporary, target);
                                }
                                finally { if (!staged.ContainsKey(target)) Cleanup(temporary, fs, control); }
                                break;
                        }
                    }
                    catch (IOException exception) when (action.Kind.StartsWith("Delete", StringComparison.Ordinal)
                        && ErrorPolicy.Classify(exception) == ErrorCategory.Missing)
                    {
                        policy.EnsureRoots(roots, control);
                        return (Absent: true, Replaced: false);
                    }
                    return (Absent: false, Replaced: actual is { IsDirectory: false } && action.Kind == "Copy file");
                }, action, target, target, action.Kind);
                if (!result.Succeeded)
                {
                    blocked.Add(target);
                    Problem(action, target, result.Category, IssueOutcome.Failed, "Operation failed; independent operations will continue.", result.Error);
                    control.ProcessedItem(target);
                    continue;
                }
                if (action.Kind.StartsWith("Delete", StringComparison.Ordinal)) removed.Add(target);
                if (result.Value.Absent)
                {
                    Problem(action, target, ErrorCategory.Missing, IssueOutcome.AlreadyAbsent,
                        "Destination was already absent; no deletion or deleted bytes were counted.");
                    control.ProcessedItem(target);
                }
                else
                {
                    if (action.Kind == "Copy file" && !result.Value.Replaced) statistics = statistics with { ReplaceFiles = 0, ReplacedBytes = 0 };
                    control.Finished(action, statistics);
                }
            }
            return new(control.Problems, control.AppliedStatistics);
        }
        finally
        {
            foreach (string temporary in staged.Values) Cleanup(temporary, fs, control);
            control.Publish(true);
        }
    }

    private static string PrepareCopy(string source, string destination, Entry expected, FileSystemAccess fs, OperationControl control, ErrorPolicy policy)
    {
        RejectLinkedAncestors(Path.GetDirectoryName(source)!, fs);
        Entry actual = ReadEntry(fs, Path.GetDirectoryName(source)!, source, policy, control) with { RelativePath = expected.RelativePath };
        if (actual != expected) Stale(source);
        fs.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporary = destination + ".xtrasync-" + Guid.NewGuid().ToString("N") + ".tmp";
        bool complete = false;
        bool created = false;
        try
        {
            long copied = 0;
            using (Stream input = Read(() => fs.OpenRead(source), source, policy, control))
            using (Stream output = fs.CreateNew(temporary))
            {
                created = true;
                byte[] buffer = new byte[1024 * 1024];
                while (true)
                {
                    control.Checkpoint();
                    int count = Read(() => input.Read(buffer, 0, buffer.Length), source, policy, control);
                    if (count == 0) break;
                    output.Write(buffer, 0, count);
                    copied += count;
                    control.Copied(count);
                }
            }
            control.Checkpoint();
            if (copied != expected.Size || Read(() => fs.Length(source), source, policy, control) != expected.Size
                || Read(() => fs.FileTime(source), source, policy, control) != expected.FileTime) Stale(source);
            fs.SetFileTime(temporary, expected.FileTime);
            complete = true;
            return temporary;
        }
        finally { if (!complete && created) Cleanup(temporary, fs, control); }
    }

    private static void Cleanup(string temporary, FileSystemAccess fs, OperationControl control)
    {
        try
        {
            try { fs.Attributes(temporary); }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) { return; }
            fs.DeleteFile(temporary);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            control.Issue(new(ErrorCategory.TransientIO, IssueOutcome.Warning, control.Snapshot.Phase,
                "Temporary file cleanup", temporary, "Temporary file could not be removed.", false, Exception: exception));
        }
    }
}
