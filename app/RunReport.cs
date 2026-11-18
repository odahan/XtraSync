using System.Globalization;
using System.Net;
using System.Text;

namespace XtraSync;

public sealed record RunContext(string Operation, string Left, string Right, string Exclusions, string Options);
public sealed record RunMessage(DateTimeOffset Time, string Level, string Message);
public sealed record RunSummary(RunContext Context, DateTimeOffset Started, DateTimeOffset Finished, string Outcome,
    OperationStatistics Planned, OperationStatistics Applied, long TransferredBytes,
    IReadOnlyList<OperationProgress> Phases, IReadOnlyList<RunMessage> Messages, OperationProblems? Problems = null);

/// <summary>Formats text and self-contained HTML from the same final run statistics.</summary>
public static class RunReport
{
    public static IReadOnlyList<(string Label, string Value)> Rows(RunSummary summary)
    {
        OperationProblems problems = summary.Problems ?? new();
        var rows = new List<(string, string)>
        {
            ("Operation", summary.Context.Operation), ("Outcome", summary.Outcome),
            ("Started", summary.Started.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)),
            ("Finished", summary.Finished.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)),
            ("Duration", (summary.Finished - summary.Started).ToString()),
            ("Left folder", summary.Context.Left), ("Right folder", summary.Context.Right),
            ("Exclusions", summary.Context.Exclusions), ("Options", summary.Context.Options),
            ("Files copied / planned", $"{summary.Applied.CopyFiles:N0} / {summary.Planned.CopyFiles:N0}"),
            ("Bytes copied / planned", Bytes(summary.Applied.CopyBytes, summary.Planned.CopyBytes)),
            ("Files deleted / planned", $"{summary.Applied.DeleteFiles:N0} / {summary.Planned.DeleteFiles:N0}"),
            ("Bytes deleted / planned", Bytes(summary.Applied.DeleteBytes, summary.Planned.DeleteBytes)),
            ("Files replaced / planned", $"{summary.Applied.ReplaceFiles:N0} / {summary.Planned.ReplaceFiles:N0}"),
            ("Old version bytes replaced / planned", Bytes(summary.Applied.ReplacedBytes, summary.Planned.ReplacedBytes)),
            ("Folders created / planned", $"{summary.Applied.CreateFolders:N0} / {summary.Planned.CreateFolders:N0}"),
            ("Folders deleted / planned", $"{summary.Applied.DeleteFolders:N0} / {summary.Planned.DeleteFolders:N0}"),
            ("Bytes transferred (includes partial copies)", Size(summary.TransferredBytes)),
            ("Failed files / folders", $"{problems.FailedFiles:N0} / {problems.FailedFolders:N0}"),
            ("Failed file bytes", Size(problems.FailedBytes)),
            ("Missing source files", problems.MissingFiles.ToString(CultureInfo.InvariantCulture)),
            ("Inaccessible or locked files", problems.InaccessibleFiles.ToString(CultureInfo.InvariantCulture)),
            ("Dependency-skipped files / folders", $"{problems.SkippedFiles:N0} / {problems.SkippedFolders:N0}"),
            ("Skipped file bytes", Size(problems.SkippedBytes)),
            ("Already absent files / folders", $"{problems.AlreadyAbsentFiles:N0} / {problems.AlreadyAbsentFolders:N0}"),
            ("Already absent file bytes (not deleted)", Size(problems.AlreadyAbsentBytes)),
            ("Unavailable scan entries", problems.UnavailableEntries.ToString(CultureInfo.InvariantCulture)),
            ("Retry attempts / recovered operations", $"{problems.Retries:N0} / {problems.Recovered:N0}"),
            ("Distinct files with I/O read errors (including recovered reads)", problems.ReadErrorFiles.ToString(CultureInfo.InvariantCulture)),
            ("Conflicts preserved", problems.Conflicts.ToString(CultureInfo.InvariantCulture)),
            ("File operations not processed", Math.Max(0, summary.Planned.CopyFiles + summary.Planned.DeleteFiles
                - summary.Applied.CopyFiles - summary.Applied.DeleteFiles - problems.FailedFiles - problems.SkippedFiles - problems.AlreadyAbsentFiles).ToString(CultureInfo.InvariantCulture)),
            ("Folder operations not processed", Math.Max(0, summary.Planned.CreateFolders + summary.Planned.DeleteFolders
                - summary.Applied.CreateFolders - summary.Applied.DeleteFolders - problems.FailedFolders - problems.SkippedFolders - problems.AlreadyAbsentFolders).ToString(CultureInfo.InvariantCulture)),
            ("Warnings", summary.Messages.Count(message => message.Level == "WARNING").ToString(CultureInfo.InvariantCulture)),
            ("Errors", summary.Messages.Count(message => message.Level == "ERROR").ToString(CultureInfo.InvariantCulture))
        };
        foreach (OperationProgress phase in summary.Phases)
        {
            rows.Add(($"{phase.Phase}: files / folders", $"{phase.Files:N0} / {phase.Folders:N0}"));
            rows.Add(($"{phase.Phase}: bytes", Size(phase.Bytes)));
            rows.Add(($"{phase.Phase}: processed / total", $"{phase.Completed:N0} / {phase.Total:N0}"));
            rows.Add(($"{phase.Phase}: equal files / differences", $"{phase.EqualFiles:N0} / {phase.DifferentItems:N0}"));
            rows.Add(($"{phase.Phase}: last path", phase.Path));
        }
        return rows;
    }

    private static string Size(long bytes) => $"{SizeFormatter.Format(bytes)} ({bytes.ToString(CultureInfo.InvariantCulture)} bytes)";
    private static string Bytes(long done, long planned) => $"{Size(done)} / {Size(planned)}";

    public static string Text(RunSummary summary)
    {
        var result = new StringBuilder("=== RUN SUMMARY ===\n");
        foreach (var row in Rows(summary)) result.AppendLine($"{row.Label}: {row.Value}");
        return result.AppendLine("=== END RUN SUMMARY ===").ToString();
    }

    public static string Html(RunSummary summary)
    {
        var result = new StringBuilder("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">");
        result.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><title>XtraSync run report</title>");
        result.Append("<style>body{font:15px system-ui;background:#191d24;color:#e3e7ef;margin:32px auto;max-width:1100px;padding:0 20px}table{border-collapse:collapse;width:100%}th,td{padding:10px;border-bottom:1px solid #414959;text-align:left;vertical-align:top;overflow-wrap:anywhere}th{background:#303846}tr:nth-child(even){background:#242831}pre{white-space:pre-wrap;overflow-wrap:anywhere}.WARNING{color:#fcd34d}.ERROR{color:#fca5a5}</style></head><body><h1>XtraSync run report</h1><h2>Run summary</h2><table><thead><tr><th>Statistic</th><th>Value</th></tr></thead><tbody>");
        foreach (var row in Rows(summary)) result.Append($"<tr><th>{WebUtility.HtmlEncode(row.Label)}</th><td>{WebUtility.HtmlEncode(row.Value)}</td></tr>");
        result.Append("</tbody></table><h2>Events, warnings and errors</h2>");
        foreach (RunMessage message in summary.Messages)
            result.Append($"<p class=\"{WebUtility.HtmlEncode(message.Level)}\">{WebUtility.HtmlEncode(message.Time.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture))} [{WebUtility.HtmlEncode(message.Level)}]</p><pre>{WebUtility.HtmlEncode(message.Message)}</pre>");
        return result.Append("</body></html>").ToString();
    }
}
