using System.Windows;
using System.IO;

namespace XtraSync;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            string? screenshotPath = null;
            int screenshotIndex = Array.IndexOf(e.Args, "--screenshot");
            if (screenshotIndex >= 0)
            {
                if (screenshotIndex + 1 >= e.Args.Length ||
                    string.IsNullOrWhiteSpace(e.Args[screenshotIndex + 1]))
                {
                    throw new ArgumentException("Usage: XtraSync --screenshot <file.png>");
                }

                screenshotPath = Path.GetFullPath(e.Args[screenshotIndex + 1]);
            }

            Window window = screenshotPath is not null && e.Args.Contains("--filters")
                ? new FilterDialog(new FilterSettings())
                : screenshotPath is not null && e.Args.Contains("--review")
                ? ScreenshotCapture.CreateReviewWindow()
                : screenshotPath is not null && e.Args.Contains("--progress")
                ? ScreenshotCapture.CreateProgressWindow()
                : new MainWindow();
            MainWindow = window;
            if (screenshotPath is not null)
            {
                ScreenshotCapture.Configure(window, screenshotPath);
            }

            window.Show();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            Shutdown(1);
        }
    }
}
