using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.IO;

namespace XtraSync;

public partial class MainViewModel : ObservableObject
{
    private readonly FolderHistory folderHistory;
    private readonly ExclusionList exclusionList;
    private readonly RunLogStore runLogs;
    private readonly ErrorPolicy errorPolicy;
    private readonly HelpDocument helpDocument;
    [ObservableProperty] private string helpError = "";
    private RunLogSession? activeRun;
    [ObservableProperty] private bool generateHtmlReport;
    [ObservableProperty] private string runFiles = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProblemSummary))]
    private OperationProblems problems = new();
    [ObservableProperty]
    private string readErrorLimit = "10";
    private string runReadErrorLimit = "10";

    public string ProblemSummary => Problems == new OperationProblems() ? "No execution problems." :
        $"Problems: {Problems.FailedFiles:N0} failed files ({SizeFormatter.Format(Problems.FailedBytes)}), " +
        $"{Problems.FailedFolders:N0} failed folders · {Problems.SkippedFiles:N0} files / {Problems.SkippedFolders:N0} folders skipped · " +
        $"{Problems.AlreadyAbsentFiles:N0} files / {Problems.AlreadyAbsentFolders:N0} folders already absent · " +
        $"{Problems.UnavailableEntries:N0} unavailable entries · {Problems.Retries:N0} retries · " +
        $"I/O read errors: {Problems.ReadErrorFiles:N0} distinct files (limit {runReadErrorLimit})";

    [ObservableProperty] private string exclusions = "";
    [ObservableProperty] private List<string> exclusionHistory = [];
    [ObservableProperty] private bool deleteExcludedFromDestination;
    [ObservableProperty] private bool compareAfterMirroring;

    public string FileExclusions
    {
        get => ExclusionLine.Parse(Exclusions).FilePatterns;
        set => Exclusions = ExclusionLine.Format(CurrentFilters() with { FilePatterns = value });
    }

    public string FolderExclusions
    {
        get => ExclusionLine.Parse(Exclusions).FolderPatterns;
        set => Exclusions = ExclusionLine.Format(CurrentFilters() with { FolderPatterns = value });
    }

    [ObservableProperty] private List<string> leftFolderHistory = [];
    [ObservableProperty] private List<string> rightFolderHistory = [];
    [ObservableProperty] private string leftPath = "";
    [ObservableProperty] private string rightPath = "";
    [ObservableProperty] private bool compareSize = true;
    [ObservableProperty] private bool compareTimestamps = true;
    [ObservableProperty] private bool ignoreTimeZone;
    [ObservableProperty] private string tolerance = "2";
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool includeSubfolders = true;
    [ObservableProperty] private bool isPaused;
    [ObservableProperty] private bool canControl;
    [ObservableProperty] private string currentFile = "No files scanned yet.";
    [ObservableProperty] private string statistics = "0 files · 0 subfolders · 0 B";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CopyStatistics), nameof(DeleteStatistics), nameof(FolderStatistics))]
    private OperationStatistics plannedStatistics = new();
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CopyStatistics), nameof(DeleteStatistics), nameof(FolderStatistics))]
    private OperationStatistics appliedStatistics = new();
    [ObservableProperty] private string transferStatistics = "";

    public string CopyStatistics => $"Copy: {AppliedStatistics.CopyFiles:N0} / {PlannedStatistics.CopyFiles:N0} files · " +
        $"{SizeFormatter.Format(AppliedStatistics.CopyBytes)} / {SizeFormatter.Format(PlannedStatistics.CopyBytes)} committed " +
        $"({AppliedStatistics.ReplaceFiles:N0} / {PlannedStatistics.ReplaceFiles:N0} replacements)";
    public string DeleteStatistics => $"Delete: {AppliedStatistics.DeleteFiles:N0} / {PlannedStatistics.DeleteFiles:N0} files · " +
        $"{SizeFormatter.Format(AppliedStatistics.DeleteBytes)} / {SizeFormatter.Format(PlannedStatistics.DeleteBytes)}";
    public string FolderStatistics => $"Folders: create {AppliedStatistics.CreateFolders:N0} / {PlannedStatistics.CreateFolders:N0} · " +
        $"delete {AppliedStatistics.DeleteFolders:N0} / {PlannedStatistics.DeleteFolders:N0}";
    [ObservableProperty] private string phase = "Ready";
    [ObservableProperty] private double progressValue;
    [ObservableProperty] private bool isIndeterminate;
    private OperationControl? control;
    private Task activeOperation = Task.CompletedTask;
    private MirrorMode? lastMode;
    private bool hasOperation;
    private bool restarting;
    private int generation;
    public bool CanEdit => !IsBusy;
    public string PauseLabel => IsPaused ? "Resume" : "Pause";
    [ObservableProperty] private string status = "Choose two folders to begin.";
    [ObservableProperty] private List<ComparisonRow> rows = [];

    public Func<MirrorPlan, bool>? ConfirmMirror { get; set; }
    public Func<string, string, string?>? ChooseFolder { get; set; }
    public Func<FilterSettings, FilterSettings?>? EditFilters { get; set; }

    public string FilterSummary
    {
        get
        {
            int count = ExclusionLine.Items(Exclusions).Length;
            return $"{count} exclusion patterns · " +
                (DeleteExcludedFromDestination ? "Mirror: delete excluded target items" : "Excluded target items are preserved");
        }
    }

    public MainViewModel() : this(new FolderHistory(), new ExclusionList(), RunLogStore.ForExecutable())
    {
    }

    /// <summary>Creates a view model with explicit stores for isolated settings and history.</summary>
    public MainViewModel(FolderHistory folderHistory, ExclusionList exclusionList, RunLogStore runLogs, ErrorPolicy? errorPolicy = null,
        HelpDocument? helpDocument = null)
    {
        ArgumentNullException.ThrowIfNull(folderHistory);
        ArgumentNullException.ThrowIfNull(exclusionList);
        ArgumentNullException.ThrowIfNull(runLogs);
        this.folderHistory = folderHistory;
        this.exclusionList = exclusionList;
        this.runLogs = runLogs;
        this.errorPolicy = errorPolicy ?? new();
        this.helpDocument = helpDocument ?? new();
        ReadErrorLimit = this.errorPolicy.Options.MaximumReadErrorFiles.ToString(System.Globalization.CultureInfo.InvariantCulture);
        runReadErrorLimit = ReadErrorLimit;
        FilterSettings filters = exclusionList.LoadSettings();
        exclusions = exclusionList.Line;
        ExclusionHistory = [.. exclusionList.History];
        deleteExcludedFromDestination = filters.DeleteExcludedFromDestination;
        compareAfterMirroring = exclusionList.CompareAfterMirroring;
        folderHistory.Load();
        LeftFolderHistory = [.. folderHistory.Left];
        RightFolderHistory = [.. folderHistory.Right];
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private void BrowseLeft() => Browse(true);

    [RelayCommand]
    private void Help()
    {
        try
        {
            helpDocument.Show();
            HelpError = "";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        {
            HelpError = $"Help could not be opened: {exception.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private void BrowseRight() => Browse(false);

    private void Browse(bool isLeft)
    {
        string? selected = ChooseFolder?.Invoke(isLeft ? "Choose left folder" : "Choose right folder",
            isLeft ? LeftPath : RightPath);
        if (selected is null)
        {
            return;
        }

        SetFolder(selected, isLeft);
    }

    /// <summary>Sets a chosen folder and persists it in the corresponding recent history.</summary>
    internal void SetFolder(string path, bool isLeft)
    {
        if (isLeft)
        {
            LeftPath = path;
        }
        else
        {
            RightPath = path;
        }

        RememberFolder(isLeft);
    }

    /// <summary>Commits a completed folder entry and persists its recent history.</summary>
    public void RememberFolder(bool isLeft)
    {
        if (!folderHistory.Remember(isLeft ? LeftPath : RightPath, isLeft))
        {
            return;
        }

        string currentPath = isLeft ? LeftPath : RightPath;
        if (isLeft)
        {
            LeftFolderHistory = [.. folderHistory.Left];
            LeftPath = currentPath;
        }
        else
        {
            RightFolderHistory = [.. folderHistory.Right];
            RightPath = currentPath;
        }

        try
        {
            folderHistory.Save();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Status = $"Folder history could not be saved: {exception.Message}";
            if (activeRun is not null) control?.Issue(new(ErrorCategory.Configuration, IssueOutcome.Warning,
                Phase, "Save folder history", "folder-history.json", Status, Exception: exception));
        }
    }

    private FilterSettings CurrentFilters() => ExclusionLine.Parse(Exclusions) with
    {
        DeleteExcludedFromDestination = DeleteExcludedFromDestination
    };

    [RelayCommand(CanExecute = nameof(CanRun))]
    private void OpenFilters()
    {
        FilterSettings? edited = EditFilters?.Invoke(CurrentFilters());
        if (edited is null) return;
        Exclusions = ExclusionLine.Format(edited);
        RememberExclusions();
        SavePreferences();
    }

    partial void OnExclusionsChanged(string value)
    {
        OnPropertyChanged(nameof(FileExclusions));
        OnPropertyChanged(nameof(FolderExclusions));
        SavePreferences();
    }
    partial void OnDeleteExcludedFromDestinationChanged(bool value) => SavePreferences();
    partial void OnCompareAfterMirroringChanged(bool value) => SavePreferences();

    private void SavePreferences()
    {
        OnPropertyChanged(nameof(FilterSummary));
        try
        {
            exclusionList.SaveLine(Exclusions, DeleteExcludedFromDestination, CompareAfterMirroring);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Status = $"Preferences could not be saved: {exception.Message}";
            if (activeRun is not null) control?.Issue(new(ErrorCategory.Configuration, IssueOutcome.Warning,
                Phase, "Save preferences", "exclusions.json", Status, Exception: exception));
        }
    }

    /// <summary>Remembers a completed entry, preserving the editable text when the item list refreshes.</summary>
    public void RememberExclusions()
    {
        if (!exclusionList.Remember(Exclusions)) return;
        string current = Exclusions;
        ExclusionHistory = [.. exclusionList.History];
        Exclusions = current;
        SavePreferences();
    }

    private ComparisonSettings Settings()
    {
        if (!int.TryParse(Tolerance, out int seconds) || seconds < 0)
        {
            throw new ArgumentException("Timestamp tolerance must be a non-negative whole number of seconds.");
        }

        FilterSettings filters = CurrentFilters();
        if (!int.TryParse(ReadErrorLimit, out int readLimit) || readLimit < 1)
            throw new ArgumentException("The read-error limit must be a positive whole number of distinct files.");
        return new ComparisonSettings(CompareSize, CompareTimestamps, seconds, IgnoreTimeZone, IncludeSubfolders,
            filters.CommonPatterns, filters.FilePatterns, filters.FolderPatterns, DeleteExcludedFromDestination, runLogs,
            errorPolicy.WithReadErrorLimit(readLimit));
    }

    private bool CanRun() => !IsBusy;

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEdit));
        CompareCommand.NotifyCanExecuteChanged();
        MirrorRightCommand.NotifyCanExecuteChanged();
        MirrorLeftCommand.NotifyCanExecuteChanged();
        SynchronizeBothCommand.NotifyCanExecuteChanged();
        BrowseLeftCommand.NotifyCanExecuteChanged();
        BrowseRightCommand.NotifyCanExecuteChanged();
        OpenFiltersCommand.NotifyCanExecuteChanged();
        RestartCommand.NotifyCanExecuteChanged();
    }

    partial void OnCanControlChanged(bool value)
    {
        PauseCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        RestartCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsPausedChanged(bool value) => OnPropertyChanged(nameof(PauseLabel));
    private bool CanRestart() => hasOperation && !restarting && (!IsBusy || CanControl);
    private bool CanPauseOrStop() => IsBusy && CanControl;

    [RelayCommand(CanExecute = nameof(CanPauseOrStop))]
    private void Pause()
    {
        IsPaused = !IsPaused;
        if (IsPaused) control?.Pause();
        else control?.Resume();
    }

    [RelayCommand(CanExecute = nameof(CanPauseOrStop))]
    private void Stop()
    {
        control?.Stop();
        IsPaused = false;
        CanControl = false;
    }

    /// <summary>Waits for cancellation, journal finalization and optional report output before window shutdown.</summary>
    public async Task StopAndWaitAsync()
    {
        if (IsBusy) Stop();
        await activeOperation;
    }

    [RelayCommand(CanExecute = nameof(CanRestart))]
    private async Task RestartAsync()
    {
        restarting = true;
        RestartCommand.NotifyCanExecuteChanged();
        try
        {
            var mode = lastMode;
            if (IsBusy) Stop();
            await activeOperation;
            await StartAsync(mode);
        }
        finally
        {
            restarting = false;
            RestartCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task CompareAsync() => StartAsync(null);

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task MirrorRightAsync() => StartAsync(MirrorMode.Right);

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task MirrorLeftAsync() => StartAsync(MirrorMode.Left);

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task SynchronizeBothAsync() => StartAsync(MirrorMode.Both);

    private Task StartAsync(MirrorMode? mode)
    {
        lastMode = mode;
        hasOperation = true;
        activeOperation = RunAsync(mode);
        return activeOperation;
    }

    private async Task RunAsync(MirrorMode? mode)
    {
        int currentGeneration = ++generation;
        long lastSequence = 0;
        bool completedSuccessfully = false;
        bool finalComparisonSkipped = false;
        bool compareAfterExecution = mode == MirrorMode.Both || CompareAfterMirroring;
        bool createHtml = GenerateHtmlReport;
        runReadErrorLimit = ReadErrorLimit;
        RunLogSession? run = null;
        void UpdateProgress(OperationProgress update)
        {
            if (currentGeneration != generation || update.Sequence <= lastSequence) return;
            lastSequence = update.Sequence;
            Phase = update.WaitingReason ?? update.Phase;
            CurrentFile = update.Path;
            Statistics = update.Stage switch
            {
                OperationStage.Comparing => $"Compared {update.Completed:N0} / {update.Total:N0} items · " +
                    $"{update.EqualFiles:N0} equal files · {update.DifferentItems:N0} differences · {SizeFormatter.Format(update.Bytes)} examined",
                OperationStage.Planning => $"Planned {update.Completed:N0} / {update.Total:N0} entries",
                OperationStage.Review => $"{update.Total:N0} operations awaiting review",
                OperationStage.Executing => $"{update.Completed:N0} / {update.Total:N0} operations · {update.Percentage:F1}%",
                _ => $"{update.Files:N0} files · {update.Folders:N0} subfolders · {SizeFormatter.Format(update.Bytes)} scanned"
            };
            if (update.Planned is not null) PlannedStatistics = update.Planned;
            if (update.Applied is not null) AppliedStatistics = update.Applied;
            if (update.Problems is not null) Problems = update.Problems;
            if (update.Stage == OperationStage.Executing)
                TransferStatistics = $"Transferred: {SizeFormatter.Format(update.Bytes)} (planned payload: {SizeFormatter.Format(PlannedStatistics.CopyBytes)})" +
                    (update.FileSize > 0 ? $" · Current file: {SizeFormatter.Format(update.FileBytes)} / {SizeFormatter.Format(update.FileSize)}" : "");
            IsIndeterminate = IsBusy && update.Stage == OperationStage.Scanning;
            ProgressValue = update.Percentage;
        }
        var progress = new Progress<OperationProgress>(UpdateProgress);
        using var operation = new OperationControl(update =>
        {
            run?.Observe(update);
            ((IProgress<OperationProgress>)progress).Report(update);
        }, issueHandler: issue => run?.Diagnostic(issue));
        control = operation;
        IsBusy = true;
        IsPaused = false;
        CanControl = true;
        IsIndeterminate = true;
        ProgressValue = 0;
        CurrentFile = "";
        Statistics = "0 files · 0 subfolders · 0 B";
        PlannedStatistics = new();
        AppliedStatistics = new();
        TransferStatistics = "";
        RunFiles = "";
        Problems = new();
        Rows = [];
        try
        {
            string operationName = mode switch
            {
                MirrorMode.Left => "Mirror to left",
                MirrorMode.Right => "Mirror to right",
                MirrorMode.Both => "Synchronize both ways",
                _ => "Compare folders"
            };
            run = runLogs.Start(new RunContext(operationName, LeftPath, RightPath, Exclusions,
                $"Subfolders={IncludeSubfolders}; CompareSize={CompareSize}; CompareTimestamps={CompareTimestamps}; " +
                $"ToleranceSeconds={Tolerance}; IgnoreTimeZone={IgnoreTimeZone}; DeleteExcludedTarget={DeleteExcludedFromDestination}; " +
                $"FileRetries={errorPolicy.Options.FileRetryDelays.Count}; RootRecoverySeconds={errorPolicy.Options.RootRecoveryTimeout.TotalSeconds}; " +
                $"MaximumReadErrorFiles={ReadErrorLimit}"));
            activeRun = run;
            RunFiles = $"Log: {runLogs.LogPath}";
            if (mode == MirrorMode.Both && DeleteExcludedFromDestination)
                operation.Issue(new(ErrorCategory.Configuration, IssueOutcome.Warning, "Starting", "Options", "",
                    "Delete excluded target items is ignored in two-way synchronization. No deletions will be performed."));
            RememberExclusions();
            RememberFolder(true);
            RememberFolder(false);
            var settings = Settings();
            string left = LeftPath;
            string right = RightPath;
            if (mode is null)
            {
                Status = "Comparing folders…";
                Rows = await Task.Run(() => FolderService.Compare(left, right, settings, operation, differencesOnly: true));
                OperationProgress comparison = operation.Snapshot;
                Status = $"{comparison.Total:N0} items · {comparison.EqualFiles:N0} equal files · " +
                    $"{comparison.DifferentItems:N0} differences";
            }
            else
            {
                Status = "Preparing synchronization preview…";
                MirrorPlan plan = await Task.Run(() => FolderService.Plan(left, right, mode.Value, settings, operation));
                UpdateProgress(operation.Snapshot);
                await Task.Run(operation.Checkpoint);
                int conflicts = plan.Conflicts?.Count ?? 0;
                foreach (string conflict in plan.Conflicts ?? []) operation.Issue(new(ErrorCategory.Conflict, IssueOutcome.Conflict,
                    operation.Snapshot.Phase, "Plan", conflict, $"Conflict left unchanged: {conflict}"));
                if (plan.Actions.Count == 0 && conflicts == 0)
                {
                    Status = "The folders already match the comparison rules.";
                    completedSuccessfully = true;
                    return;
                }
                CanControl = false;
                Status = "Review the synchronization preview.";
                bool confirmed = ConfirmMirror?.Invoke(plan) == true;
                CanControl = true;
                if (!confirmed)
                {
                    Status = "Synchronization cancelled. No files changed.";
                    return;
                }
                Status = mode == MirrorMode.Both ? "Synchronizing both ways…" : "Mirroring folders…";
                ExecutionResult result = await Task.Run(() => FolderService.Execute(plan, operation));
                UpdateProgress(operation.Snapshot);
                if (compareAfterExecution)
                {
                    Status = "Comparing folders after execution…";
                    Rows = await Task.Run(() => FolderService.Compare(left, right, settings, operation, differencesOnly: true));
                }
                else
                {
                    finalComparisonSkipped = true;
                }
                Status = $"{(mode == MirrorMode.Both ? "Synchronization" : "Mirror")} finished · " +
                    $"{result.Applied.CopyFiles + result.Applied.DeleteFiles + result.Applied.CreateFolders + result.Applied.DeleteFolders:N0} operations completed · {conflicts:N0} conflicts left unchanged.";
            }
            completedSuccessfully = true;
        }
        catch (OperationCanceledException)
        {
            Status = "Stopped. Completed operations are kept; restart scans and previews the remaining changes.";
            try { run?.Warning(Status); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { Status += $" Log write failed: {exception.Message}"; }
        }
        catch (Exception exception)
        {
            Status = $"Stopped: {exception.Message}";
            try
            {
                run?.Error($"Run failed at: {operation.Snapshot.Path}" +
                (exception is RunAbortedException aborted ? $" ({aborted.Category})" : ""), exception);
            }
            catch (Exception logException) when (logException is IOException or UnauthorizedAccessException)
            { Status += $" Log write failed: {logException.Message}"; }
        }
        finally
        {
            try { operation.Publish(true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { Status += $" Log write failed: {exception.Message}"; }
            UpdateProgress(operation.Snapshot);
            if (completedSuccessfully) ProgressValue = 100;
            if (completedSuccessfully && Problems.HasIssues)
                Status = $"Completed with issues: {AppliedStatistics.CopyFiles:N0} files copied, {AppliedStatistics.DeleteFiles:N0} deleted; " +
                    $"{Problems.FailedFiles:N0} failed files, {Problems.FailedFolders:N0} failed folders, " +
                    $"{Problems.SkippedFiles + Problems.SkippedFolders:N0} dependent operations skipped, {Problems.UnavailableEntries:N0} unavailable entries, " +
                    $"{Problems.Conflicts:N0} conflicts. See the log.";
            else if (completedSuccessfully && Problems.HasWarnings)
                Status += $" Completed with warnings: {Problems.Retries:N0} retries, {Problems.Recovered:N0} recovered operations, {Problems.Warnings:N0} other warnings. See the log.";
            if (finalComparisonSkipped)
                Status += " Final comparison skipped.";
            try
            {
                if (run is not null)
                {
                    var result = run.Complete(Status, createHtml);
                    if (result.HtmlPath is not null) RunFiles += $" · HTML: {result.HtmlPath}";
                    if (result.ReportError is not null) Status += $" {result.ReportError}";
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { Status += $" Run log could not be finalized: {exception.Message}"; }
            finally
            {
                activeRun = null;
                try { run?.Dispose(); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                { Status += $" Run log could not be closed: {exception.Message}"; }
                GenerateHtmlReport = false;
            }
            control = null;
            IsPaused = false;
            CanControl = false;
            IsBusy = false;
            IsIndeterminate = false;
        }
    }
}
