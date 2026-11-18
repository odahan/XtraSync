using System.Windows;

namespace XtraSync;

public partial class ReviewDialog : Window
{
    public ReviewDialog(MirrorPlan plan)
    {
        InitializeComponent();
        MaxHeight = SystemParameters.WorkArea.Height;
        DataContext = new ReviewDialogViewModel(plan);
        SourceInitialized += (_, _) => DarkChrome.Apply(this);
    }

    private void Apply(object sender, RoutedEventArgs e) => DialogResult = true;
}
