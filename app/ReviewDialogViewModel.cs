namespace XtraSync;

public sealed record ReviewGroup(string Group, long Files, long Bytes)
{
    public string Size => SizeFormatter.Format(Bytes);
}

public sealed record ReviewAction(string Operation, string Direction, string RelativePath, long Bytes)
{
    public string Size => SizeFormatter.Format(Bytes);
}

/// <summary>Builds the confirmation from the exact plan that will be validated and executed.</summary>
public sealed class ReviewDialogViewModel
{
    public string Title { get; }
    public string Roots { get; }
    public string Warning { get; }
    public string ApplyLabel { get; }
    public OperationStatistics Statistics { get; }
    public IReadOnlyList<ReviewGroup> Groups { get; }
    public IReadOnlyList<ReviewAction> Actions { get; }
    public IReadOnlyList<string> Conflicts { get; }
    public bool CanApply => Actions.Count > 0;
    public string FolderSummary => $"Folders: {Statistics.CreateFolders:N0} to create · {Statistics.DeleteFolders:N0} to delete";
    public string ConflictSummary => $"{Conflicts.Count:N0} conflicts will be left unchanged";
    public string AffectedSummary => $"{Statistics.CopyFiles + Statistics.DeleteFiles:N0} file operations · {Actions.Count:N0} total operations";

    public ReviewDialogViewModel(MirrorPlan plan)
    {
        Title = plan.BothWays ? "Review two-way synchronization" : "Review mirror operations";
        Roots = plan.BothWays ? $"Left: {plan.Source}\nRight: {plan.Destination}"
            : $"Source: {plan.Source}\nDestination: {plan.Destination}";
        ApplyLabel = plan.BothWays ? "Apply synchronization" : "Apply mirror";
        Warning = plan.BothWays
            ? "Missing files and newer versions will be copied in either direction. Existing versions may be replaced. No deletions."
            : "The destination will be made to match the source. Deletions are permanent and replaced files are overwritten. " +
                (plan.DeleteExcludedFromDestination ? "Excluded target items are included in the deletions below."
                    : "Excluded target items are preserved.");
        Statistics = OperationStatistics.FromPlan(plan);
        Groups = [new("Files to copy (including replacements)", Statistics.CopyFiles, Statistics.CopyBytes),
            new("Files to delete", Statistics.DeleteFiles, Statistics.DeleteBytes),
            new("Existing versions to overwrite (included above)", Statistics.ReplaceFiles, Statistics.ReplacedBytes)];
        Actions = plan.Actions.Select(action =>
        {
            OperationStatistics item = OperationStatistics.ForAction(plan, action);
            return new ReviewAction(action.Kind,
                action.Kind.StartsWith("Delete", StringComparison.Ordinal) ? "Destination only" : action.Direction,
                action.RelativePath, item.CopyBytes + item.DeleteBytes);
        }).ToArray();
        Conflicts = plan.Conflicts?.ToArray() ?? [];
    }
}
