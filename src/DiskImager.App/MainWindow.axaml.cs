using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DiskImagerX.ViewModels;

namespace DiskImagerX;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    private Border? _card;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _vm.PickFile = PickFileAsync;
        _vm.Confirm = (t, m, a) => Dialogs.ConfirmAsync(this, t, m, a);
        _vm.Info = m => Dialogs.InfoAsync(this, "DiskImager", m);
        _vm.PropertyChanged += OnVmChanged;
        DataContext = _vm;
    }

    // Fade the card in whenever the mode changes (a settle transition, not a hard swap).
    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.Mode)) return;
        _card ??= this.FindControl<Border>("Card");
        if (_card is null) return;
        _card.Opacity = 0;
        Dispatcher.UIThread.Post(() => { if (_card != null) _card.Opacity = 1; }, DispatcherPriority.Background);
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
