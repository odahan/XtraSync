using System.Windows;

namespace XtraSync;

public partial class FilterDialog : Window
{
    public FilterDialog(FilterSettings settings)
    {
        InitializeComponent();
        MaxHeight = SystemParameters.WorkArea.Height;
        DataContext = new FilterDialogViewModel(settings);
        SourceInitialized += (_, _) => DarkChrome.Apply(this);
    }

    public FilterSettings Settings => ((FilterDialogViewModel)DataContext).ToSettings();

    private void SaveFilters(object sender, RoutedEventArgs e) => DialogResult = true;
}
