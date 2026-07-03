using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using DiskImagerX.Disk;

namespace DiskImagerX;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Contains("--selftest")) { Environment.Exit(SelfTest.Run()); return; }
        int e2e = Array.IndexOf(args, "--e2e");
        if (e2e >= 0 && e2e + 2 < args.Length)
        { Environment.Exit(E2ETest.Run(long.Parse(args[e2e + 1]), args[e2e + 2])); return; }
        if (args.Contains("--list")) { ListDisks(); return; }
        int shot = Array.IndexOf(args, "--shot");
        if (shot >= 0 && shot + 1 < args.Length)
        {
            int mode = shot + 2 < args.Length && int.TryParse(args[shot + 2], out var mm) ? mm : 0;
            RenderShot(args[shot + 1], mode); return;
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Offscreen render of the main window to a PNG (no visible window) — for UI review.
    private static void RenderShot(string outPath, int mode)
    {
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();
        var win = new MainWindow { Width = 620, Height = 676 };
        win.Show();
        Dispatcher.UIThread.RunJobs();
        for (int i = 0; i < 6; i++) { Dispatcher.UIThread.RunJobs(); System.Threading.Thread.Sleep(40); }
        // switch mode on the live, shown window, then let the fade settle
        if (win.DataContext is ViewModels.MainViewModel vm) vm.Mode = mode;
        for (int i = 0; i < 12; i++) { Dispatcher.UIThread.RunJobs(); System.Threading.Thread.Sleep(40); }
        if (win.FindControl<Avalonia.Controls.Border>("Card") is { } card) card.Opacity = 1;
        for (int i = 0; i < 4; i++) { Dispatcher.UIThread.RunJobs(); System.Threading.Thread.Sleep(40); }
        var frame = win.CaptureRenderedFrame();
        frame?.Save(outPath);
        Console.WriteLine(frame is null ? "no frame" : "saved " + outPath);
    }

    // Diagnostic (read-only): print the detected disks for the current OS.
    private static void ListDisks()
    {
        var backend = BackendFactory.Create();
        Console.WriteLine($"Platform : {backend.PlatformName}");
        Console.WriteLine($"Elevated : {backend.IsElevated()}   ({backend.ElevationHint})");
        var disks = backend.EnumerateAsync().GetAwaiter().GetResult();
        Console.WriteLine($"Disks    : {disks.Count}");
        foreach (var d in disks)
            Console.WriteLine($"  {d.DevicePath,-22} {d.SizeText,10}  {d.Model}{d.Tag}"
                              + (d.Volumes.Length > 0 ? $"   vols=[{string.Join(",", d.Volumes)}]" : ""));
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
