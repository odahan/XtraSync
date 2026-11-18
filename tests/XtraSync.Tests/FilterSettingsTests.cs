using System.Text.Json;

namespace XtraSync.Tests;

public sealed class FilterSettingsTests : TestWorkspace
{
    [Fact]
    public void DefaultsDoNotDeleteExcludedTargetItems()
    {
        Assert.Equal(new FilterSettings("", "", "", false), new FilterSettings());
        Assert.False(new ComparisonSettings().DeleteExcludedFromDestination);
        var draft = new FilterDialogViewModel(new());
        Assert.False(draft.ToSettings().DeleteExcludedFromDestination);
    }

    [Fact]
    public void LegacyStringMigratesWithoutChangingPatternsOrEnablingCleanup()
    {
        string path = Write(Root, "legacy.json", JsonSerializer.Serialize("*.tmp; .git"));
        var store = new ExclusionList(path);
        Assert.Equal(new FilterSettings("*.tmp; .git"), store.LoadSettings());
        store.SaveSettings(store.LoadSettings() with { FilePatterns = "*.bak", FolderPatterns = "bin" });
        Assert.Equal(new FilterSettings("*.tmp; .git", "*.bak", "bin"), store.LoadSettings());
        Assert.Equal("*.tmp; .git", store.Load());
    }

    [Fact]
    public void StoresAllFiltersAndCleanupOptionTogether()
    {
        var store = new ExclusionList(Path.Combine(Root, "settings.json"));
        var settings = new FilterSettings("shared", "*.bak\r\n*.tmp", "bin;obj", true);
        store.SaveSettings(settings);
        Assert.Equal(new FilterSettings("shared", "*.bak; *.tmp", "bin; obj", true), store.LoadSettings());
        store.SaveSettings(new());
        Assert.Equal(new FilterSettings(), store.LoadSettings());
    }

    [Theory]
    [InlineData("{\"CommonPatterns\":null,\"FilePatterns\":null,\"FolderPatterns\":null}")]
    [InlineData("{\"DeleteExcludedFromDestination\":\"yes\"}")]
    [InlineData("[]")]
    public void NullOrMalformedTypedSettingsFallBackToDefaults(string json)
    {
        Assert.Equal(new FilterSettings(), new ExclusionList(Write(Root, "settings.json", json)).LoadSettings());
    }

    [Fact]
    public void DialogEditsAnIndependentDraftAndClearResetsEverything()
    {
        var original = new FilterSettings("common", "files", "folders", true);
        var draft = new FilterDialogViewModel(original);
        Assert.Equal(original, draft.ToSettings());
        draft.CommonPatterns = "new common";
        draft.FilePatterns = "new files";
        draft.FolderPatterns = "new folders";
        Assert.Equal(new FilterSettings("new common", "new files", "new folders", true), draft.ToSettings());
        Assert.Equal("common", original.CommonPatterns);
        draft.ClearCommand.Execute(null);
        Assert.Equal(new FilterSettings(DeleteExcludedFromDestination: true), draft.ToSettings());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancelledOrUnavailableFilterEditorDoesNotChangeSettings(bool attach)
    {
        var model = CreateViewModel();
        model.Exclusions = "keep";
        if (attach) model.EditFilters = settings => { Assert.Equal("keep", settings.CommonPatterns); return null; };
        model.OpenFiltersCommand.Execute(null);
        Assert.Equal("keep", model.Exclusions);
        Assert.Equal("keep", CreateViewModel().Exclusions);
    }

    [Fact]
    public void AcceptedDialogPersistsAndNotifiesEveryFilter()
    {
        var model = CreateViewModel();
        var notifications = new List<string?>();
        model.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        model.DeleteExcludedFromDestination = true;
        model.EditFilters = _ => new(".git", "*.tmp; *.bak", "bin\nobj", true);
        model.OpenFiltersCommand.Execute(null);
        var restored = CreateViewModel();
        Assert.Equal(".git; file:*.tmp; file:*.bak; folder:bin; folder:obj", restored.Exclusions);
        Assert.Equal("*.tmp; *.bak", restored.FileExclusions);
        Assert.Equal("bin; obj", restored.FolderExclusions);
        Assert.True(restored.DeleteExcludedFromDestination);
        Assert.Equal("5 exclusion patterns · Mirror: delete excluded target items", restored.FilterSummary);
        Assert.Contains(nameof(model.Exclusions), notifications);
        Assert.Contains(nameof(model.FileExclusions), notifications);
        Assert.Contains(nameof(model.FolderExclusions), notifications);
        Assert.Contains(nameof(model.DeleteExcludedFromDestination), notifications);
        Assert.Contains(nameof(model.FilterSummary), notifications);
        model.IsBusy = true;
        Assert.False(model.OpenFiltersCommand.CanExecute(null));
    }

    [Fact]
    public void EditingTypedPropertiesPersistsAndSummaryIgnoresEmptyPatterns()
    {
        var model = CreateViewModel();
        Assert.Contains("0 exclusion patterns", model.FilterSummary);
        model.FileExclusions = " *.tmp; ; \n";
        model.FolderExclusions = "bin";
        model.DeleteExcludedFromDestination = true;
        var restored = CreateViewModel();
        Assert.Equal(model.FileExclusions, restored.FileExclusions);
        Assert.Equal("bin", restored.FolderExclusions);
        Assert.True(restored.DeleteExcludedFromDestination);
        model.DeleteExcludedFromDestination = false;
        Assert.Equal("2 exclusion patterns · Excluded target items are preserved", model.FilterSummary);
    }

    [Theory]
    [InlineData(MirrorMode.Left)]
    [InlineData(MirrorMode.Right)]
    [InlineData(MirrorMode.Both)]
    public async Task ViewModelPassesAllFiltersAndCleanupOptionIntoPlans(MirrorMode mode)
    {
        Write(Left, "visible");
        Write(Right, "other");
        var model = CreateViewModel();
        model.Exclusions = ".git";
        model.FileExclusions = "*.tmp";
        model.FolderExclusions = "bin";
        model.DeleteExcludedFromDestination = true;
        bool reviewed = false;
        model.ConfirmMirror = plan =>
        {
            reviewed = true;
            Assert.Equal(".git", plan.Exclusions);
            Assert.Equal("*.tmp", plan.FileExclusions);
            Assert.Equal("bin", plan.FolderExclusions);
            Assert.Equal(mode != MirrorMode.Both, plan.DeleteExcludedFromDestination);
            return false;
        };
        await (mode == MirrorMode.Left ? model.MirrorLeftCommand : mode == MirrorMode.Right
            ? model.MirrorRightCommand : model.SynchronizeBothCommand).ExecuteAsync(null);
        Assert.True(reviewed);
    }
}
