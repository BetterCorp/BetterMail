using System.Runtime.InteropServices;
using Avalonia.Platform;

namespace BetterMail.App;

// Work around Avalonia WebView 12.1.0's GTK cancellation bug at WebKit's policy boundary.
// Returning TRUE alone does not reject a WebKit decision: an undecided decision defaults to use().
internal sealed class GtkExternalNavigationGuard : IDisposable
{
    private readonly IntPtr _webView;
    private readonly DecidePolicyCallback _callback; // Keep the native callback rooted until disconnect.
    private nuint _handler;

    private GtkExternalNavigationGuard(IntPtr webView, Action<Uri> open)
    {
        _webView = webView;
        _callback = (_, decision, type, _) =>
        {
            if (type is not (0 or 1)) return 0; // Navigation / new-window actions only; images still load normally.
            var action = webkit_navigation_policy_decision_get_navigation_action(decision);
            var request = action == IntPtr.Zero ? IntPtr.Zero : webkit_navigation_action_get_request(action);
            var value = request == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(webkit_uri_request_get_uri(request));
            var external = Uri.TryCreate(value, UriKind.Absolute, out var uri) && ReadOnlyWebViewLinks.IsExternal(uri);
            if (!external && type != 1) return 0; // Allow application-supplied about:blank / local documents.

            webkit_policy_decision_ignore(decision);
            // A redirect or script-initiated navigation must never create browser tabs.
            if (external && webkit_navigation_action_get_navigation_type(action) == 0 /* LINK_CLICKED */ &&
                webkit_navigation_action_is_user_gesture(action) != 0)
            {
                try { open(uri!); }
                catch (Exception exception)
                {
                    // Exceptions must not cross the native callback boundary.
                    System.Diagnostics.Trace.TraceWarning("Could not dispatch external link: {0}", exception.GetType().Name);
                }
            }
            return 1;
        };
        _handler = g_signal_connect_data(_webView, "decide-policy", _callback, IntPtr.Zero, IntPtr.Zero, 0);
        if (_handler == 0) throw new InvalidOperationException("Could not protect embedded content navigation.");
    }

    public static GtkExternalNavigationGuard? Attach(IPlatformHandle? handle, Action<Uri> open) =>
        OperatingSystem.IsLinux() && handle is IGtkWebViewPlatformHandle gtk && gtk.WebKitWebView != IntPtr.Zero
            ? new(gtk.WebKitWebView, open) : null;

    public void Dispose()
    {
        if (_handler == 0) return;
        g_signal_handler_disconnect(_webView, _handler);
        _handler = 0;
        GC.KeepAlive(_callback);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DecidePolicyCallback(IntPtr webView, IntPtr decision, int type, IntPtr data);

    [DllImport("libgobject-2.0.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern nuint g_signal_connect_data(IntPtr instance, string signal, DecidePolicyCallback callback, IntPtr data, IntPtr destroy, int flags);
    [DllImport("libgobject-2.0.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern void g_signal_handler_disconnect(IntPtr instance, nuint handler);
    [DllImport("libwebkit2gtk-4.1.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr webkit_navigation_policy_decision_get_navigation_action(IntPtr decision);
    [DllImport("libwebkit2gtk-4.1.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr webkit_navigation_action_get_request(IntPtr action);
    [DllImport("libwebkit2gtk-4.1.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern int webkit_navigation_action_is_user_gesture(IntPtr action);
    [DllImport("libwebkit2gtk-4.1.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern int webkit_navigation_action_get_navigation_type(IntPtr action);
    [DllImport("libwebkit2gtk-4.1.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr webkit_uri_request_get_uri(IntPtr request);
    [DllImport("libwebkit2gtk-4.1.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern void webkit_policy_decision_ignore(IntPtr decision);
}
