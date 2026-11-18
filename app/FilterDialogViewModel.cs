using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace XtraSync;

/// <summary>Edits a detached filter draft; only accepting the dialog changes application settings.</summary>
public partial class FilterDialogViewModel : ObservableObject
{
    [ObservableProperty] private string commonPatterns;
    [ObservableProperty] private string filePatterns;
    [ObservableProperty] private string folderPatterns;
    private readonly bool deleteExcludedFromDestination;

    public FilterDialogViewModel(FilterSettings settings)
    {
        commonPatterns = ExclusionLine.ToMultiline(settings.CommonPatterns);
        filePatterns = ExclusionLine.ToMultiline(settings.FilePatterns);
        folderPatterns = ExclusionLine.ToMultiline(settings.FolderPatterns);
        deleteExcludedFromDestination = settings.DeleteExcludedFromDestination;
    }

    /// <summary>Returns the draft as an immutable settings snapshot.</summary>
    public FilterSettings ToSettings() => new(string.Join("; ", ExclusionLine.Items(CommonPatterns)),
        string.Join("; ", ExclusionLine.Items(FilePatterns)), string.Join("; ", ExclusionLine.Items(FolderPatterns)),
        deleteExcludedFromDestination);

    [RelayCommand]
    private void Clear()
    {
        CommonPatterns = "";
        FilePatterns = "";
        FolderPatterns = "";
    }
}
