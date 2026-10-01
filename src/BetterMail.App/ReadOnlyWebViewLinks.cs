using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Threading;

namespace BetterMail.App;

internal sealed class ReadOnlyWebViewLinks
{
    private readonly Action<Uri> _open;
    private GtkExternalNavigationGuard? _gtk;

    public ReadOnlyWebViewLinks(NativeWebView view, Action<Uri>? open = null)
    {
        _open = open ?? (uri => { using var process = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); });
        view.AdapterCreated += (_, args) =>
        {
            _gtk?.Dispose();
            _gtk = GtkExternalNavigationGuard.Attach(args.TryGetPlatformHandle(),
                uri => Dispatcher.UIThread.Post(() => Open(uri, _open)));
        };
        view.AdapterDestroyed += (_, _) => { _gtk?.Dispose(); _gtk = null; };
        view.NavigationStarted += (_, args) =>
        {
            // GTK 12.1.0 returns TRUE for Cancel without calling policy_decision_ignore.
            // Let our following native handler reject the decision and identify user clicks.
            if (_gtk is null) CancelNavigation(args, _open);
        };
        view.NewWindowRequested += (_, args) =>
        {
            if (_gtk is not null) return;
            args.Handled = true;
            if (IsExternal(args.Request)) Open(args.Request!, _open);
        };
    }

    internal static bool IsExternal(Uri? uri) => uri?.Scheme is "http" or "https" or "mailto";

    internal static void CancelNavigation(WebViewNavigationStartingEventArgs args, Action<Uri> open)
    {
        if (!IsExternal(args.Request)) return;
        args.Cancel = true; // Independent of OS launch success or a returned Process handle.
        Open(args.Request!, open);
    }

    private static void Open(Uri uri, Action<Uri> open)
    {
        try { open(uri); }
        catch (Exception exception)
        {
            // Never fall back to loading a website inside the email if the OS handler fails.
            Trace.TraceWarning("Could not open external link: {0}", exception.GetType().Name);
        }
    }
}
