using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace XtraSync;

/// <summary>Renders an unattended window to PNG without activating or raising it.</summary>
internal static class ScreenshotCapture
{
    private const int ExtendedStyle = -20;
    private const int NoActivateStyle = 0x08000000;
    private const int WindowPositionChanging = 0x0046;
    private const uint NoActivate = 0x0010;
    private static readonly IntPtr Bottom = new(1);

    /// <summary>Creates a review example entirely from metadata, without scanning or changing files.</summary>
    public static Window CreateReviewWindow()
    {
        long time = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();
        var source = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase)
        {
            ["report.pdf"] = new("report.pdf", false, 12 * 1024 * 1024, time),
            ["photo.jpg"] = new("photo.jpg", false, 5 * 1024 * 1024, time),
            ["archive"] = new("archive", true, 0, time)
        };
        var destination = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase)
        {
            ["photo.jpg"] = new("photo.jpg", false, 4 * 1024 * 1024, time),
            ["obsolete.tmp"] = new("obsolete.tmp", false, 512 * 1024, time),
            ["old-folder"] = new("old-folder", true, 0, time)
        };
        return new ReviewDialog(new MirrorPlan(@"C:\Example\Source", @"C:\Example\Destination",
            [new("Copy file", "report.pdf"), new("Copy file", "photo.jpg"), new("Delete file", "obsolete.tmp"),
                new("Create folder", "archive"), new("Delete folder", "old-folder")], source, destination));
    }

    /// <summary>Renders representative progress using isolated stores and no filesystem operation.</summary>
    public static Window CreateProgressWindow()
    {
        string settingsRoot = Path.Combine(Path.GetTempPath(), "XtraSync-screenshot", Guid.NewGuid().ToString("N"));
        const long megabyte = 1024 * 1024;
        var model = new MainViewModel(new FolderHistory(Path.Combine(settingsRoot, "history.json")),
            new ExclusionList(Path.Combine(settingsRoot, "exclusions.json")), new RunLogStore(settingsRoot))
        {
            LeftPath = @"C:\Example\Source", RightPath = @"C:\Example\Destination", IsBusy = true,
            Phase = "Mirroring", Statistics = "51 / 105 operations processed · 49.0%", ProgressValue = 49.0,
            PlannedStatistics = new(80, 640 * megabyte, 20, 40 * megabyte, 3, 2, 12, 96 * megabyte),
            AppliedStatistics = new(32, 256 * megabyte, 10, 20 * megabyte, 3, 1, 6, 48 * megabyte),
            Problems = new(FailedFiles: 3, FailedBytes: 24 * megabyte, SkippedFiles: 2, SkippedBytes: 16 * megabyte,
                Retries: 2, Recovered: 1, MissingFiles: 1, InaccessibleFiles: 2),
            TransferStatistics = "Transferred: 260 MB (planned payload: 640 MB) · Current file: 4 MB / 8 MB",
            CurrentFile = @"C:\Example\Destination\current-file.bin", Status = "Mirroring folders…"
        };
        return new MainWindow(model);
    }

    public static void Configure(Window window, string path)
    {
        window.ShowActivated = false;
        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            SetWindowLong(handle, ExtendedStyle, GetWindowLong(handle, ExtendedStyle) | NoActivateStyle);
            HwndSource.FromHwnd(handle).AddHook(KeepBehindOtherWindows);
            SetWindowPos(handle, Bottom, 0, 0, 0, 0, NoActivate | 0x0001 | 0x0002);
        };
        window.Loaded += (_, _) => StartRendering(window, path);
    }

    private static IntPtr KeepBehindOtherWindows(IntPtr handle, int message, IntPtr wParam,
        IntPtr lParam, ref bool handled)
    {
        if (message == WindowPositionChanging)
        {
            var position = Marshal.PtrToStructure<WindowPosition>(lParam);
            position.InsertAfter = Bottom;
            position.Flags = (position.Flags & ~0x0004u) | NoActivate;
            Marshal.StructureToPtr(position, lParam, false);
        }

        return IntPtr.Zero;
    }

    /// <summary>Explicitly renders frames even while the window is covered or inactive.</summary>
    private static void StartRendering(Window window, string path)
    {
        int frames = 0;
        var timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(1000.0 / 60)
        };
        window.Closed += (_, _) => timer.Stop();
        timer.Tick += (_, _) =>
        {
            try
            {
                window.UpdateLayout();
                var dpi = VisualTreeHelper.GetDpi(window);
                var frame = new RenderTargetBitmap(
                    Math.Max(1, (int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX)),
                    Math.Max(1, (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY)),
                    dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);

                frame.Render(window);
                if (++frames < 120)
                {
                    return;
                }

                timer.Stop();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(frame));
                using (var output = File.Create(path))
                {
                    encoder.Save(output);
                }

                Application.Current.Shutdown(0);
            }
            catch (Exception exception)
            {
                timer.Stop();
                Console.Error.WriteLine(exception);
                Application.Current.Shutdown(1);
            }
        };
        timer.Start();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPosition
    {
        public IntPtr Handle;
        public IntPtr InsertAfter;
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public uint Flags;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr handle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr handle, int index, int value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr handle, IntPtr insertAfter, int x, int y,
        int width, int height, uint flags);
}
