using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace DiskImagerX;

/// <summary>Small code-built modal dialogs (no MessageBox in Avalonia).</summary>
public static class Dialogs
{
    static readonly IBrush Bg = new SolidColorBrush(Color.Parse("#0C1626"));
    static readonly IBrush Text = new SolidColorBrush(Color.Parse("#DCEBFF"));
    static readonly IBrush Dim = new SolidColorBrush(Color.Parse("#6280A0"));
    static readonly IBrush Danger = new SolidColorBrush(Color.Parse("#E23B2E"));
    static readonly IBrush Surf = new SolidColorBrush(Color.Parse("#10203A"));
    static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#0058AC"));

    public static async Task InfoAsync(Window owner, string title, string message)
    {
        var ok = new Button { Content = "OK", Width = 90, Height = 32, HorizontalAlignment = HorizontalAlignment.Right, Foreground = Text, Background = Accent };
        var dlg = Shell(title, message, ok);
        ok.Click += (_, _) => dlg.Close();
        await dlg.ShowDialog(owner);
    }

    /// <summary>Yes/No confirm. If <paramref name="action"/> is "ERASE", the OK button unlocks
    /// only once the user types ERASE (used for the system-disk / destructive guard).</summary>
    public static async Task<bool> ConfirmAsync(Window owner, string title, string message, string action)
    {
        bool typed = action == "ERASE";
        var result = false;

        var ok = new Button
        {
            Content = typed ? "ERASE ANYWAY" : action.ToUpperInvariant(),
            Width = typed ? 150 : 110, Height = 34, Foreground = Text, Background = Danger, IsEnabled = !typed,
        };
        var cancel = new Button { Content = "Cancel", Width = 90, Height = 34, Foreground = Dim, Background = Surf };

        Control? extra = null;
        if (typed)
        {
            var box = new TextBox { Watermark = "type ERASE", Background = Surf, Foreground = Text, Margin = new Thickness(0, 8, 0, 0) };
            box.TextChanged += (_, _) => ok.IsEnabled = box.Text?.Trim().ToUpperInvariant() == "ERASE";
            extra = box;
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var dlg = Shell(title, message, buttons, extra, danger: true);
        ok.Click += (_, _) => { result = true; dlg.Close(); };
        cancel.Click += (_, _) => { result = false; dlg.Close(); };
        await dlg.ShowDialog(owner);
        return result;
    }

    static Window Shell(string title, string message, Control footer, Control? extra = null, bool danger = false)
    {
        var panel = new StackPanel { Margin = new Thickness(22), Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = danger ? Danger : Text });
        panel.Children.Add(new TextBlock { Text = message, Foreground = Text, TextWrapping = TextWrapping.Wrap });
        if (extra != null) panel.Children.Add(extra);
        panel.Children.Add(footer);

        return new Window
        {
            Title = title,
            Width = 440, SizeToContent = SizeToContent.Height,
            CanResize = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Bg, Content = panel,
        };
    }
}
