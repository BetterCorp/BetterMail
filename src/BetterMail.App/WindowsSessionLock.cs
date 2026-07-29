using System.Runtime.InteropServices;

namespace BetterMail.App;

internal static class WindowsSessionLock
{
    private const uint DesktopSwitchDesktop = 0x0100;

    public static bool IsLocked()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }
        var desktop = OpenInputDesktop(0, false, DesktopSwitchDesktop);
        if (desktop == 0)
        {
            return false;
        }
        try
        {
            return !SwitchDesktop(desktop);
        }
        finally
        {
            CloseDesktop(desktop);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint OpenInputDesktop(uint flags, bool inherit, uint desiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SwitchDesktop(nint desktop);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(nint desktop);
}
