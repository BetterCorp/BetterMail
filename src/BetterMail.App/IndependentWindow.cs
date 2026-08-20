using Avalonia.Controls;

namespace BetterMail.App;

internal static class IndependentWindow
{
    public static void Show(Window window)
    {
        window.ShowInTaskbar = true;
        window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.Show();
    }

    public static Task ShowAsync(Window window)
    {
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult();
        Show(window);
        return closed.Task;
    }
}
