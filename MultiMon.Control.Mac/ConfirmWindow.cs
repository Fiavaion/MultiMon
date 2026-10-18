using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace MultiMon.Control.Mac;

/// <summary>Yes/No confirmation dialog — Avalonia has no MessageBox, and one destructive action
/// (New project over unsaved work) needs one. Modal to its owner, like the Windows panel's MessageBox.</summary>
internal static class ConfirmWindow
{
    public static Task<bool> AskAsync(Window owner, string title, string message)
    {
        var result = new TaskCompletionSource<bool>();

        var yes = new Button { Content = "Yes", Width = 90, IsDefault = true };
        var no = new Button { Content = "No", Width = 90, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };

        var window = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(22),
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 18) },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { yes, no },
                    },
                },
            },
        };

        yes.Click += (_, _) => { result.TrySetResult(true); window.Close(); };
        no.Click += (_, _) => { result.TrySetResult(false); window.Close(); };
        window.Closed += (_, _) => result.TrySetResult(false);   // closed with the title-bar button = No

        window.ShowDialog(owner);
        return result.Task;
    }
}
