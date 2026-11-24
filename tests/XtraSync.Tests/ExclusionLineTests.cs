using System.Text.Json;

namespace XtraSync.Tests;

public sealed class ExclusionLineTests : TestWorkspace
{
    [Fact]
    public void ParsesMixedLineIntoTypedListsAndRebuildsIt()
    {
        var filters = ExclusionLine.Parse(" *.tmp ; FILE:*.bak; folder:bin; .git; file:cache/*.log; FOLDER:obj ");
        Assert.Equal(new FilterSettings("*.tmp; .git", "*.bak; cache/*.log", "bin; obj"), filters);
        Assert.Equal("*.tmp; .git; file:*.bak; file:cache/*.log; folder:bin; folder:obj", ExclusionLine.Format(filters));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ;\r\n ; ")]
    [InlineData("file:; folder: ")]
    public void EmptyPatternsDoNotCreateFilters(string line) =>
        Assert.Equal(new FilterSettings(), ExclusionLine.Parse(line));

    [Fact]
    public void RoundTripPreservesSpacesCommasPathsAndWildcardTypes()
    {
        var original = new FilterSettings("folder with spaces; a,b", "cache\\*.log; file?.txt", "build/output");
        Assert.Equal(original, ExclusionLine.Parse(ExclusionLine.Format(original)));
    }

    [Fact]
    public void DialogShowsOneItemPerLineAndSerializesBackToTheMainLine()
    {
        var draft = new FilterDialogViewModel(ExclusionLine.Parse(".git; *.tmp; file:*.bak; file:*.log; folder:bin; folder:obj"));
        Assert.Equal(string.Join(Environment.NewLine, ".git", "*.tmp"), draft.CommonPatterns);
        Assert.Equal(string.Join(Environment.NewLine, "*.bak", "*.log"), draft.FilePatterns);
        Assert.Equal(string.Join(Environment.NewLine, "bin", "obj"), draft.FolderPatterns);
        Assert.DoesNotContain(';', draft.CommonPatterns);
        Assert.DoesNotContain(';', draft.FilePatterns);
        Assert.DoesNotContain(';', draft.FolderPatterns);
        draft.FilePatterns = "*.old\n*.cache";
        draft.FolderPatterns = " node_modules \r\n build ";
        Assert.Equal(".git; *.tmp; file:*.old; file:*.cache; folder:node_modules; folder:build",
            ExclusionLine.Format(draft.ToSettings()));
    }

    [Fact]
    public void ManualLineIsTheOnlySourceForDialogAndRuntimeSettings()
    {
        var model = CreateViewModel();
        model.Exclusions = "*.tmp; file:*.bak; folder:bin";
        model.EditFilters = filters =>
        {
            Assert.Equal(new FilterSettings("*.tmp", "*.bak", "bin"), filters);
            return new FilterSettings(".git", "*.old", "obj");
        };
        model.OpenFiltersCommand.Execute(null);
        Assert.Equal(".git; file:*.old; folder:obj", model.Exclusions);
        Assert.Equal(model.Exclusions, CreateViewModel().Exclusions);
        model.Exclusions = "manual";
        Assert.Equal("", model.FileExclusions);
        Assert.Equal("", model.FolderExclusions);
        model.EditFilters = filters =>
        {
            Assert.Equal(new FilterSettings("manual"), filters);
            return null;
        };
        model.OpenFiltersCommand.Execute(null);
        Assert.Equal("manual", model.Exclusions);
    }

    [Fact]
    public void DialogCannotChangeOrClearTheMainCleanupOption()
    {
        var model = CreateViewModel();
        model.DeleteExcludedFromDestination = true;
        model.EditFilters = filters =>
        {
            var draft = new FilterDialogViewModel(filters);
            draft.ClearCommand.Execute(null);
            return draft.ToSettings() with { DeleteExcludedFromDestination = false };
        };
        model.OpenFiltersCommand.Execute(null);
        Assert.True(model.DeleteExcludedFromDestination);
        Assert.True(CreateViewModel().DeleteExcludedFromDestination);
        Assert.Equal("", model.Exclusions);
    }

    [Fact]
    public void TypingSavesCurrentLineWithoutRecordingEveryKeystroke()
    {
        var model = CreateViewModel();
        model.Exclusions = "*";
        model.Exclusions = "*.";
        model.Exclusions = "*.tmp";
        Assert.Equal("*.tmp", CreateViewModel().Exclusions);
        Assert.Empty(model.ExclusionHistory);
        model.RememberExclusions();
        Assert.Equal(["*.tmp"], model.ExclusionHistory);
        Assert.Equal(["*.tmp"], CreateViewModel().ExclusionHistory);
        model.Exclusions = "";
        model.RememberExclusions();
        Assert.Equal("", CreateViewModel().Exclusions);
        Assert.Equal(["*.tmp"], CreateViewModel().ExclusionHistory);
    }

    [Fact]
    public void HistoryKeepsTenRecentLinesMovesDuplicatesToFrontAndPreservesCurrentText()
    {
        var model = CreateViewModel();
        for (int index = 0; index < 12; index++)
        {
            model.Exclusions = $"*.{index}";
            model.RememberExclusions();
        }
        Assert.Equal(10, model.ExclusionHistory.Count);
        Assert.Equal("*.11", model.ExclusionHistory[0]);
        Assert.Equal("*.2", model.ExclusionHistory[^1]);
        model.Exclusions = "*.5";
        model.RememberExclusions();
        model.RememberExclusions();
        Assert.Equal(10, model.ExclusionHistory.Count);
        Assert.Equal("*.5", model.ExclusionHistory[0]);
        Assert.Single(model.ExclusionHistory, line => line == "*.5");
        Assert.Equal("*.5", model.Exclusions);
        Assert.Equal(model.ExclusionHistory, CreateViewModel().ExclusionHistory);
    }

    [Fact]
    public void HistoryDeduplicatesCaseWhitespaceAndSlashSpelling()
    {
        var store = new ExclusionList(Path.Combine(Root, "filters.json"));
        Assert.True(store.Remember("*.tmp;file:cache\\*.log;folder:bin"));
        Assert.False(store.Remember(" *.TMP ; FILE:cache/*.LOG ; FOLDER:BIN "));
        Assert.True(store.Remember("*.bak"));
        Assert.True(store.Remember("*.TMP;file:cache/*.LOG;folder:BIN"));
        Assert.Equal(2, store.History.Count);
        Assert.False(store.Remember(" ; file: ; folder: "));
    }

    [Fact]
    public void LoadFiltersAndLimitsHistoryWithoutTrustingMalformedEntries()
    {
        object?[] history = [null, 12, " ", "file:", "*.tmp", "*.TMP",
            .. Enumerable.Range(0, 14).Select(index => (object)$"*.{index}")];
        string path = Write(Root, "filters.json", JsonSerializer.Serialize(new
        {
            ExclusionLine = " file:*.tmp ; folder:bin ", DeleteExcludedFromDestination = true, History = history
        }));
        var store = new ExclusionList(path);
        Assert.Equal(new FilterSettings("", "*.tmp", "bin", true), store.LoadSettings());
        Assert.Equal(" file:*.tmp ; folder:bin ", store.Line);
        Assert.Equal(10, store.History.Count);
        Assert.Equal("*.tmp", store.History[0]);
        Assert.Equal("*.8", store.History[^1]);
    }

    [Theory]
    [InlineData("{\"ExclusionLine\":\"*.tmp\",\"DeleteExcludedFromDestination\":false}")]
    [InlineData("{\"ExclusionLine\":\"*.tmp\",\"DeleteExcludedFromDestination\":false,\"History\":42}")]
    public void MissingOrInvalidHistoryDoesNotDiscardCurrentLine(string json)
    {
        var store = new ExclusionList(Write(Root, "filters.json", json));
        Assert.Equal(new FilterSettings("*.tmp"), store.LoadSettings());
        Assert.Empty(store.History);
    }

    [Theory]
    [InlineData("{\"ExclusionLine\":42,\"DeleteExcludedFromDestination\":false}")]
    [InlineData("{\"ExclusionLine\":\"*.tmp\"}")]
    [InlineData("{\"ExclusionLine\":\"*.tmp\",\"DeleteExcludedFromDestination\":\"yes\"}")]
    public void DamagedCurrentFormatFallsBackToSafeDefaults(string json)
    {
        var store = new ExclusionList(Write(Root, "filters.json", json));
        Assert.Equal(new FilterSettings(), store.LoadSettings());
        Assert.Equal("", store.Line);
        Assert.Empty(store.History);
    }

    [Fact]
    public void PreviousTypedSettingsMigrateIntoTheCompleteMainLine()
    {
        Write(Root, "exclusions.json", JsonSerializer.Serialize(new FilterSettings(".git", "*.tmp", "bin", true)));
        var model = CreateViewModel();
        Assert.Equal(".git; file:*.tmp; folder:bin", model.Exclusions);
        Assert.True(model.DeleteExcludedFromDestination);
        model.RememberExclusions();
        var restored = CreateViewModel();
        Assert.Equal(model.Exclusions, restored.Exclusions);
        Assert.Equal([model.Exclusions], restored.ExclusionHistory);
    }

    [Fact]
    public async Task RunningComparisonRemembersAndAppliesTheManualTypedLine()
    {
        Write(Left, "ignored.tmp");
        Write(Left, "cache/ignored");
        Write(Left, "visible");
        var model = CreateViewModel();
        model.Exclusions = "file:*.tmp; folder:cache";
        await model.CompareCommand.ExecuteAsync(null);
        Assert.Equal("visible", Assert.Single(model.Rows).RelativePath);
        Assert.Equal([model.Exclusions], CreateViewModel().ExclusionHistory);
    }

    [Fact]
    public void HistorySaveFailureIsReportedWithoutCrashing()
    {
        var model = new MainViewModel(new FolderHistory(Path.Combine(Root, "history.json")), new ExclusionList(Left), new RunLogStore(Path.Combine(Root, "reports")));
        model.Exclusions = "*.tmp";
        model.RememberExclusions();
        Assert.StartsWith("Preferences could not be saved:", model.Status);
    }
}
