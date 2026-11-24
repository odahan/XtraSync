using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Input;
using System.IO;
using Microsoft.Win32;

namespace XtraSync;

public partial class MainWindow : Window
{
    private bool closingAfterRun;
    private bool closingRequested;
    public MainWindow() : this(new MainViewModel())
    {
    }

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        viewModel.ConfirmMirror = ConfirmMirror;
        viewModel.ChooseFolder = ChooseFolder;
        viewModel.EditFilters = EditFilters;
        DataContext = viewModel;
        Closing += async (_, e) =>
        {
            var viewModel = (MainViewModel)DataContext;
            if (viewModel.IsBusy && !closingAfterRun)
            {
                e.Cancel = true;
                if (closingRequested) return;
                closingRequested = true;
                await viewModel.StopAndWaitAsync();
                closingAfterRun = true;
                _ = Dispatcher.BeginInvoke(new Action(Close));
                return;
            }
            viewModel.RememberFolder(true);
            viewModel.RememberFolder(false);
            viewModel.RememberExclusions();
        };
        SourceInitialized += (_, _) => DarkChrome.Apply(this);
    }

    /// <summary>Opens the optional donation page in the default browser.</summary>
    private void OpenDonationPage(object sender, RoutedEventArgs e)
    {
        const string donationUrl = "https://buymeacoffee.com/knhsynths";
        try
        {
            Process.Start(new ProcessStartInfo(donationUrl) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(this, $"The donation page could not be opened. Visit {donationUrl} in your browser.",
                "Buy Me a Coffee", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private FilterSettings? EditFilters(FilterSettings settings)
    {
        var dialog = new FilterDialog(settings) { Owner = this };
        return dialog.ShowDialog() == true ? dialog.Settings : null;
    }

    private string? ChooseFolder(string title, string currentPath)
    {
        var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
        if (Directory.Exists(currentPath))
        {
            dialog.InitialDirectory = Path.GetFullPath(currentPath);
        }

        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    /// <summary>Shows the drop effect for a single directory from File Explorer.</summary>
    private void FolderPreviewDragOver(object sender, DragEventArgs e)
    {
        e.Effects = GetDroppedFolder(sender, e) is null ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;
    }

    /// <summary>Applies the dropped directory to the folder field under the pointer.</summary>
    private void FolderPreviewDrop(object sender, DragEventArgs e)
    {
        string? path = GetDroppedFolder(sender, e);
        e.Effects = path is null ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;

        if (path is not null)
        {
            ((MainViewModel)DataContext).SetFolder(path, sender == LeftFolder);
        }
    }

    private static string? GetDroppedFolder(object sender, DragEventArgs e)
    {
        if (((ComboBox)sender).IsEnabled && (e.AllowedEffects & DragDropEffects.Copy) != 0 &&
            e.Data.GetData(DataFormats.FileDrop, autoConvert: false) is string[] { Length: 1 } paths &&
            Directory.Exists(paths[0]))
        {
            return paths[0];
        }

        return null;
    }

    private void FolderLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        var comboBox = (ComboBox)sender;
        if (!comboBox.IsKeyboardFocusWithin && !comboBox.IsDropDownOpen)
        {
            ((MainViewModel)DataContext).RememberFolder(comboBox == LeftFolder);
        }
    }

    private void FolderPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !((ComboBox)sender).IsDropDownOpen)
        {
            ((MainViewModel)DataContext).RememberFolder(sender == LeftFolder);
            e.Handled = true;
        }
    }

    private void ExclusionLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!ExclusionEntry.IsKeyboardFocusWithin && !ExclusionEntry.IsDropDownOpen)
            ((MainViewModel)DataContext).RememberExclusions();
    }

    private void ExclusionPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !ExclusionEntry.IsDropDownOpen)
        {
            ((MainViewModel)DataContext).RememberExclusions();
            e.Handled = true;
        }
    }

    private void ExclusionDropDownClosed(object? sender, EventArgs e) =>
        ((MainViewModel)DataContext).RememberExclusions();

    private bool ConfirmMirror(MirrorPlan plan)
    {
        var dialog = new ReviewDialog(plan) { Owner = this };
        return dialog.ShowDialog() == true;
    }
}

internal static class DarkChrome
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    public static void Apply(Window window)
    {
        int enabled = 1;
        DwmSetWindowAttribute(new WindowInteropHelper(window).Handle, 20, ref enabled, sizeof(int));
    }
}
