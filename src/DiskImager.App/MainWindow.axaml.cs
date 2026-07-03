using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using DiskImagerX.ViewModels;

namespace DiskImagerX;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _vm.PickFile = PickFileAsync;
        _vm.Confirm = (t, m, a) => Dialogs.ConfirmAsync(this, t, m, a);
        _vm.Info = m => Dialogs.InfoAsync(this, "DiskImager", m);
        DataContext = _vm;
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
