using Avalonia.Controls;

namespace BetterMail.App;

internal static class IndependentWindow
{
    public static void Show(Window window)
    {
        window.ShowInTaskbar = true;
        window.Show();
    }
}
