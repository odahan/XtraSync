namespace XtraSync;

/// <summary>Counts planned or committed filesystem changes using immutable preview metadata.</summary>
public sealed record OperationStatistics(long CopyFiles = 0, long CopyBytes = 0,
    long DeleteFiles = 0, long DeleteBytes = 0, long CreateFolders = 0, long DeleteFolders = 0,
    long ReplaceFiles = 0, long ReplacedBytes = 0)
{
    public static OperationStatistics FromPlan(MirrorPlan plan)
    {
        var result = new OperationStatistics();
        foreach (FileAction action in plan.Actions) result = result.Add(ForAction(plan, action));
        return result;
    }

    public static OperationStatistics ForAction(MirrorPlan plan, FileAction action)
    {
        var source = action.Reverse ? plan.DestinationSnapshot : plan.SourceSnapshot;
        var destination = action.Reverse ? plan.SourceSnapshot : plan.DestinationSnapshot;
        switch (action.Kind)
        {
            case "Copy file":
                bool replace = destination.TryGetValue(action.RelativePath, out Entry? existing) && !existing.IsDirectory;
                return new(CopyFiles: 1, CopyBytes: source[action.RelativePath].Size,
                    ReplaceFiles: replace ? 1 : 0, ReplacedBytes: replace ? existing!.Size : 0);
            case "Delete file": return new(DeleteFiles: 1, DeleteBytes: destination[action.RelativePath].Size);
            case "Create folder": return new(CreateFolders: 1);
            case "Delete folder": return new(DeleteFolders: 1);
            default: throw new ArgumentException($"Unknown operation: {action.Kind}", nameof(action));
        }
    }

    public OperationStatistics Add(OperationStatistics other) => new(
        CopyFiles + other.CopyFiles, CopyBytes + other.CopyBytes,
        DeleteFiles + other.DeleteFiles, DeleteBytes + other.DeleteBytes,
        CreateFolders + other.CreateFolders, DeleteFolders + other.DeleteFolders,
        ReplaceFiles + other.ReplaceFiles, ReplacedBytes + other.ReplacedBytes);
}
