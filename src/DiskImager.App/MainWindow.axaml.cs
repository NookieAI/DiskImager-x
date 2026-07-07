using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Transformation;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DiskImagerX.ViewModels;

namespace DiskImagerX;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    private Border? _card;
    private DispatcherTimer? _devDebounce;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _vm.PickFile = PickFileAsync;
        _vm.Confirm = (t, m, a) => Dialogs.ConfirmAsync(this, t, m, a);
        _vm.Info = m => Dialogs.InfoAsync(this, "DiskImager", m);
        _vm.PropertyChanged += OnVmChanged;
        DataContext = _vm;
        if (OperatingSystem.IsWindows()) HookDeviceChange();
    }

    // Live disk list: WM_DEVICECHANGE → debounced refresh (skipped while an operation runs).
    // Subclasses the Win32 window via comctl32 (Avalonia 11.0 has no WndProc hook API).
    private SubclassProcDelegate? _subclassProc;   // field: keeps the marshaled delegate alive
    private delegate IntPtr SubclassProcDelegate(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, IntPtr id, IntPtr refData);

    private void HookDeviceChange()
    {
        _devDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
        _devDebounce.Tick += (_, _) =>
        {
            _devDebounce!.Stop();
            if (!_vm.IsRunning && _vm.RefreshCommand.CanExecute(null)) _vm.RefreshCommand.Execute(null);
        };
        Opened += (_, _) =>
        {
            try
            {
                var h = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
                if (h == IntPtr.Zero) return;
                _subclassProc = SubclassProc;
                SetWindowSubclass(h, _subclassProc, 1, IntPtr.Zero);
            }
            catch { }   // non-Win32 windowing (headless --shot): live refresh simply stays off
        };
    }

    private IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, IntPtr id, IntPtr refData)
    {
        if (uMsg == 0x0219)   // WM_DEVICECHANGE
        {
            long wp = wParam.ToInt64();   // 0x8000 arrival · 0x8004 removal · 0x0007 devnodes changed
            if (wp is 0x8000 or 0x8004 or 0x0007) { _devDebounce!.Stop(); _devDebounce.Start(); }
        }
        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    [System.Runtime.InteropServices.DllImport("comctl32.dll")]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProcDelegate proc, IntPtr id, IntPtr refData);

    [System.Runtime.InteropServices.DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    // Fade + rise the card in whenever the mode changes (a settle transition, not a hard swap).
    // The hidden state must be SNAPPED with transitions detached — otherwise the set to 0 is
    // itself animated and the intended fade-in collapses into a one-frame flicker.
    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.Mode)) return;
        _card ??= this.FindControl<Border>("Card");
        if (_card is null) return;
        var t = _card.Transitions;
        _card.Transitions = null;
        _card.Opacity = 0;
        _card.RenderTransform = TransformOperations.Parse("translateY(8px)");
        _card.Transitions = t;
        Dispatcher.UIThread.Post(() =>
        {
            if (_card is null) return;
            _card.Opacity = 1;
            _card.RenderTransform = TransformOperations.Parse("translateY(0px)");
        }, DispatcherPriority.Background);
    }

    private async Task<string?> PickFileAsync(bool save, string suggested)
    {
        var sp = StorageProvider;
        if (save)
        {
            var f = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save backup image",
                SuggestedFileName = string.IsNullOrEmpty(suggested) ? "backup.img" : suggested,
            });
            return f?.TryGetLocalPath();
        }
        var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose an image",
            AllowMultiple = false,
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }
}
