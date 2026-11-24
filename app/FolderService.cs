using System.IO;

namespace XtraSync;

public sealed record Entry(string RelativePath, bool IsDirectory, long Size, long FileTime);
public sealed record ComparisonRow(string RelativePath, string Status, string LeftSize,
    string RightSize, string LeftModified, string RightModified);
public sealed record FileAction(string Kind, string RelativePath, bool Reverse = false)
{
    public string Direction => Reverse ? "Right → Left" : "Source → Destination";
}
public sealed record MirrorPlan(string Source, string Destination, List<FileAction> Actions,
    Dictionary<string, Entry> SourceSnapshot, Dictionary<string, Entry> DestinationSnapshot,
    bool IncludeSubfolders = true, bool BothWays = false, List<string>? Conflicts = null,
    string Exclusions = "", string FileExclusions = "", string FolderExclusions = "",
    bool DeleteExcludedFromDestination = false, RunLogStore? RunArtifacts = null,
    ErrorPolicy? ErrorPolicy = null, IReadOnlySet<string>? UnavailablePaths = null);

public static class FolderService
{
    public static (string Left, string Right) ValidateRoots(string left, string right, FileSystemAccess? fileSystem = null)
    {
        fileSystem ??= new();
        left = Path.TrimEndingDirectorySeparator(Path.GetFullPath(left));
        right = Path.TrimEndingDirectorySeparator(Path.GetFullPath(right));
        if (!fileSystem.DirectoryExists(left) || !fileSystem.DirectoryExists(right))
            throw new RunAbortedException(ErrorCategory.RootUnavailable,
                $"Both folders must exist and be accessible before comparison: {left} | {right}");
        if (IsWithin(left, right) || IsWithin(right, left))
            throw new IOException("Choose separate folders that do not contain each other.");
        ResilientOperations.RejectLinkedAncestors(left, fileSystem);
        ResilientOperations.RejectLinkedAncestors(right, fileSystem);
        return (left, right);
    }

    private static bool IsWithin(string path, string parent) =>
        path.Equals(parent, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    public static ScanSnapshot Scan(string root, bool includeSubfolders = true,
        OperationControl? control = null, string exclusions = "", HashSet<string>? protectedFolders = null,
        string fileExclusions = "", string folderExclusions = "", RunLogStore? runArtifacts = null,
        HashSet<string>? protectedRuntimeFolders = null, ErrorPolicy? errorPolicy = null)
    {
        using var owned = control is null ? new OperationControl() : null;
        control ??= owned!;
        return ResilientOperations.Scan(root, includeSubfolders, control, exclusions, protectedFolders,
            fileExclusions, folderExclusions, runArtifacts, protectedRuntimeFolders, errorPolicy ?? new());
    }
    public static string Status(Entry? left, Entry? right, ComparisonSettings settings)
    {
        if (left is null) return "Right only";
        if (right is null) return "Left only";
        if (left.IsDirectory != right.IsDirectory) return "Type conflict";
        if (left.IsDirectory) return "Folder";
        int time = settings.CompareTimestamps
            ? ComparisonRules.CompareFileTimes(left.FileTime, right.FileTime, settings.ToleranceSeconds,
                settings.IgnoreTimeZone ? (byte)2 : (byte)0) : 0;
        if (time < 0) return "Right newer";
        if (time > 0) return "Left newer";
        return settings.CompareSize && left.Size != right.Size ? "Different size" : "Equal";
    }

    internal static bool IsDifference(string status) => status is not ("Equal" or "Folder");

    public static List<ComparisonRow> Compare(string leftRoot, string rightRoot, ComparisonSettings settings,
        OperationControl? control = null) => Compare(leftRoot, rightRoot, settings, control, differencesOnly: false);

    internal static List<ComparisonRow> Compare(string leftRoot, string rightRoot, ComparisonSettings settings,
        OperationControl? control, bool differencesOnly)
    {
        using var owned = control is null ? new OperationControl() : null;
        control ??= owned!;
        control.Begin("Scanning folders");
        ErrorPolicy policy = settings.ErrorPolicy ?? new();
        (leftRoot, rightRoot) = ValidateRoots(leftRoot, rightRoot, policy.FileSystem);
        policy.EnsureRoots([leftRoot, rightRoot], control);
        var left = Scan(leftRoot, settings.IncludeSubfolders, control, settings.Exclusions,
            fileExclusions: settings.FileExclusions, folderExclusions: settings.FolderExclusions, runArtifacts: settings.RunArtifacts, errorPolicy: policy);
        var right = Scan(rightRoot, settings.IncludeSubfolders, control, settings.Exclusions,
            fileExclusions: settings.FileExclusions, folderExclusions: settings.FolderExclusions, runArtifacts: settings.RunArtifacts, errorPolicy: policy);
        string[] paths = left.Keys.Union(right.Keys, StringComparer.OrdinalIgnoreCase)
            .Union(left.UnavailablePaths, StringComparer.OrdinalIgnoreCase).Union(right.UnavailablePaths, StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        control.Begin("Comparing entries", paths.Length, OperationStage.Comparing);
        var rows = new List<ComparisonRow>();
        foreach (string path in paths)
        {
            control.Checkpoint();
            left.TryGetValue(path, out Entry? first);
            right.TryGetValue(path, out Entry? second);
            bool unavailable = left.UnavailablePaths.Concat(right.UnavailablePaths).Any(scope => ResilientOperations.Contains(path, scope));
            string status = unavailable ? "Unavailable" : Status(first, second, settings);
            control.Compared(Path.Combine(leftRoot, path), first, second, status);
            if (differencesOnly && !IsDifference(status)) continue;

            rows.Add(new ComparisonRow(path.Length == 0 ? "(root)" : path, status,
                unavailable ? "Unavailable" : Size(first), unavailable ? "Unavailable" : Size(second),
                Modified(first), Modified(second)));
        }
        control.Publish(true);
        return rows;
    }

    private static string Size(Entry? entry) => entry is null ? "—" : entry.IsDirectory ? "Folder" : SizeFormatter.Format(entry.Size);
    private static string Modified(Entry? entry) => entry is null ? "—" : DateTime.FromFileTimeUtc(entry.FileTime).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    public static MirrorPlan Plan(string left, string right, MirrorMode mode, ComparisonSettings settings,
        OperationControl? control = null)
    {
        using var owned = control is null ? new OperationControl() : null;
        control ??= owned!;
        control.Begin("Scanning for preview");
        ErrorPolicy policy = settings.ErrorPolicy ?? new();
        (left, right) = ValidateRoots(left, right, policy.FileSystem);
        policy.EnsureRoots([left, right], control);
        if (mode == MirrorMode.Both) return PlanBoth(left, right, settings, control);
        string source = mode == MirrorMode.Right ? left : right;
        string destination = mode == MirrorMode.Right ? right : left;
        var protectedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var runtimeFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sourceEntries = Scan(source, settings.IncludeSubfolders, control, settings.Exclusions,
            settings.DeleteExcludedFromDestination ? null : protectedFolders, settings.FileExclusions, settings.FolderExclusions,
            settings.RunArtifacts, runtimeFolders, policy);
        var destinationEntries = settings.DeleteExcludedFromDestination
            ? Scan(destination, settings.IncludeSubfolders, control, runArtifacts: settings.RunArtifacts, protectedRuntimeFolders: runtimeFolders, errorPolicy: policy)
            : Scan(destination, settings.IncludeSubfolders, control, settings.Exclusions, protectedFolders,
                settings.FileExclusions, settings.FolderExclusions, settings.RunArtifacts, runtimeFolders, policy);
        var actions = new List<FileAction>();
        control.Begin("Planning mirror", sourceEntries.Count + destinationEntries.Count, OperationStage.Planning);
        foreach (Entry entry in destinationEntries.Values.OrderByDescending(x => x.RelativePath.Length))
        {
            control.Checkpoint();
            control.ProcessedItem(Path.Combine(destination, entry.RelativePath));
            if (!sourceEntries.TryGetValue(entry.RelativePath, out Entry? other) || entry.IsDirectory != other.IsDirectory)
                actions.Add(new FileAction(entry.IsDirectory ? "Delete folder" : "Delete file", entry.RelativePath));
        }
        foreach (Entry entry in sourceEntries.Values.OrderBy(x => x.RelativePath.Length))
        {
            control.Checkpoint();
            control.ProcessedItem(Path.Combine(source, entry.RelativePath));
            destinationEntries.TryGetValue(entry.RelativePath, out Entry? other);
            if (entry.IsDirectory)
            {
                if (other is null || !other.IsDirectory) actions.Add(new FileAction("Create folder", entry.RelativePath));
            }
            else if (Status(entry, other, settings) != "Equal") actions.Add(new FileAction("Copy file", entry.RelativePath));
        }
        var conflicts = new List<string>();
        protectedFolders.UnionWith(runtimeFolders);
        foreach (string path in protectedFolders)
        {
            bool typeConflict = sourceEntries.TryGetValue(path, out var a) &&
                destinationEntries.TryGetValue(path, out var b) && a.IsDirectory != b.IsDirectory;
            if (typeConflict)
            {
                actions.RemoveAll(action => IsWithin(action.RelativePath, path));
                conflicts.Add(path + " (folder contains excluded items; type replacement skipped)");
            }
            else
            {
                actions.RemoveAll(action => action.Kind == "Delete folder" &&
                    action.RelativePath.Equals(path, StringComparison.OrdinalIgnoreCase));
            }
        }
        var plan = new MirrorPlan(source, destination, actions, sourceEntries, destinationEntries,
            settings.IncludeSubfolders, false, conflicts, settings.Exclusions,
            settings.FileExclusions, settings.FolderExclusions, settings.DeleteExcludedFromDestination, settings.RunArtifacts, policy);
        plan = ProtectUnknown(plan, sourceEntries, destinationEntries);
        PublishPreview(plan, control);
        return plan;
    }

    /// <summary>Builds a non-deleting bidirectional plan and preserves ambiguous conflicts.</summary>
    private static MirrorPlan PlanBoth(string left, string right, ComparisonSettings settings, OperationControl control)
    {
        ErrorPolicy policy = settings.ErrorPolicy ?? new();
        var first = Scan(left, settings.IncludeSubfolders, control, settings.Exclusions,
            fileExclusions: settings.FileExclusions, folderExclusions: settings.FolderExclusions, runArtifacts: settings.RunArtifacts, errorPolicy: policy);
        var second = Scan(right, settings.IncludeSubfolders, control, settings.Exclusions,
            fileExclusions: settings.FileExclusions, folderExclusions: settings.FolderExclusions, runArtifacts: settings.RunArtifacts, errorPolicy: policy);
        var actions = new List<FileAction>();
        var conflicts = new List<string>();
        var blocked = new List<string>();
        string[] paths = first.Keys.Union(second.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x.Length).ThenBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        control.Begin("Planning two-way synchronization", paths.Length, OperationStage.Planning);
        foreach (string path in paths)
        {
            control.Checkpoint();
            control.ProcessedItem(Path.Combine(left, path));
            if (blocked.Any(parent => path.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) continue;
            first.TryGetValue(path, out Entry? a);
            second.TryGetValue(path, out Entry? b);
            if (a is not null && b is not null && a.IsDirectory != b.IsDirectory)
            {
                conflicts.Add(path + " (file/folder conflict)");
                blocked.Add(path);
                continue;
            }
            if (a is null || b is null)
            {
                Entry existing = (a ?? b)!;
                actions.Add(new(existing.IsDirectory ? "Create folder" : "Copy file", path, a is null));
                continue;
            }
            string status = Status(a, b, settings);
            if (status is "Equal" or "Folder") continue;
            if (status is "Left newer" or "Right newer") actions.Add(new("Copy file", path, status == "Right newer"));
            else conflicts.Add(path + " (no newer version under the selected rules)");
        }
        var plan = new MirrorPlan(left, right, actions, first, second, settings.IncludeSubfolders, true, conflicts, settings.Exclusions,
            settings.FileExclusions, settings.FolderExclusions, RunArtifacts: settings.RunArtifacts, ErrorPolicy: policy);
        plan = ProtectUnknown(plan, first, second);
        PublishPreview(plan, control);
        return plan;
    }

    private static void PublishPreview(MirrorPlan plan, OperationControl control) =>
        control.Begin("Ready for review", plan.Actions.Count, OperationStage.Review, OperationStatistics.FromPlan(plan));

    private static MirrorPlan ProtectUnknown(MirrorPlan plan, ScanSnapshot first, ScanSnapshot second)
    {
        var unknown = new HashSet<string>(first.UnavailablePaths.Concat(second.UnavailablePaths), StringComparer.OrdinalIgnoreCase);
        plan.Actions.RemoveAll(action => unknown.Any(scope => ResilientOperations.Contains(action.RelativePath, scope)
            || (action.Kind != "Create folder" && ResilientOperations.Contains(scope, action.RelativePath))));
        foreach (string scope in unknown) plan.Conflicts!.Add($"{scope} (unavailable scope; changes are blocked)");
        return plan with { UnavailablePaths = unknown };
    }

    public static ExecutionResult Execute(MirrorPlan plan, OperationControl? control = null)
    {
        bool ownsControl = control is null;
        control ??= new OperationControl();
        try { return ResilientOperations.Execute(plan, control, plan.ErrorPolicy ?? new()); }
        finally { if (ownsControl) control.Dispose(); }
    }
}
