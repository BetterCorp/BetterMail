using System.Runtime.InteropServices;
using Avalonia.Platform;

namespace BetterMail.App;

internal static class LinuxWebViewFocus
{
    // GTK's grab_focus selects its widget, but doesn't move X11 keyboard focus into
    // the reparented WebView window. This also applies to GTK running under XWayland.
    // Keep native key/IME handling rather than synthesizing text through JavaScript.
    public static void Focus(IPlatformHandle? handle, IPlatformHandle? owner)
    {
        if (!OperatingSystem.IsLinux() || handle is not IGtkWebViewPlatformHandle ||
            handle.HandleDescriptor != "XID" || owner?.HandleDescriptor != "XID" ||
            handle.Handle == IntPtr.Zero || owner.Handle == IntPtr.Zero) return;
        var display = XOpenDisplay(IntPtr.Zero);
        if (display == IntPtr.Zero) return;
        try
        {
            XGetInputFocus(display, out var focused, out _);
            // Ignore delayed GTK notifications after the user has moved to another window.
            if (focused == owner.Handle || focused == new IntPtr(1) /* PointerRoot */)
                SetFocus(display, handle.Handle);
        }
        finally { XCloseDisplay(display); }
    }

    public static void RestoreOwner(IPlatformHandle? editor, IPlatformHandle? owner)
    {
        if (!OperatingSystem.IsLinux() || editor is not IGtkWebViewPlatformHandle ||
            editor.HandleDescriptor != "XID" || owner?.HandleDescriptor != "XID" ||
            editor.Handle == IntPtr.Zero || owner.Handle == IntPtr.Zero) return;
        var display = XOpenDisplay(IntPtr.Zero);
        if (display == IntPtr.Zero) return;
        try
        {
            XGetInputFocus(display, out var focused, out _);
            // Hand input back to To/Subject/the toolbar, but never reclaim focus from another app.
            if (focused == editor.Handle) SetFocus(display, owner.Handle);
        }
        finally { XCloseDisplay(display); }
    }

    private static void SetFocus(IntPtr display, IntPtr window)
    {
        XSetInputFocus(display, window, 2 /* RevertToParent */, IntPtr.Zero /* CurrentTime */);
        XFlush(display);
    }

    [DllImport("libX11.so.6")]
    private static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport("libX11.so.6")]
    private static extern int XSetInputFocus(IntPtr display, IntPtr window, int revertTo, IntPtr time);
    [DllImport("libX11.so.6")]
    private static extern int XGetInputFocus(IntPtr display, out IntPtr window, out int revertTo);
    [DllImport("libX11.so.6")]
    private static extern int XFlush(IntPtr display);
    [DllImport("libX11.so.6")]
    private static extern int XCloseDisplay(IntPtr display);
}
