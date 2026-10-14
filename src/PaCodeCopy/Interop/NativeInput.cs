using System.Runtime.InteropServices;

namespace PaCodeCopy.Interop;

/// <summary>Win32 helpers for driving another application (keyboard shortcuts, clicks, foreground window).</summary>
internal static class NativeInput
{
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const byte VK_CONTROL = 0x11;
    private const byte VK_MENU = 0x12;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const int SW_RESTORE = 9;

    /// <summary>Presses Ctrl + the given letter (e.g. 'A', 'C') in the foreground window.</summary>
    public static void SendCtrl(char letter)
    {
        var key = (byte)char.ToUpperInvariant(letter);
        keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
        keybd_event(key, 0, 0, UIntPtr.Zero);
        keybd_event(key, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    /// <summary>Single left click at a screen position; the mouse pointer is put back afterwards.</summary>
    public static void Click(int x, int y)
    {
        GetCursorPos(out var original);
        SetCursorPos(x, y);
        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(50);
        SetCursorPos(original.X, original.Y);
    }

    /// <summary>Restores (if minimised) and activates a window. Returns true when it is the foreground window.</summary>
    public static bool BringToFront(IntPtr hwnd, TimeSpan timeout)
    {
        if (IsIconic(hwnd))
        {
            ShowWindow(hwnd, SW_RESTORE);
        }

        var deadline = DateTime.UtcNow + timeout;
        var attempts = 0;
        while (DateTime.UtcNow < deadline)
        {
            if (GetForegroundWindow() == hwnd)
            {
                return true;
            }

            if (attempts++ == 3)
            {
                // Windows only lets the active app change the foreground window. A key event from this
                // process makes it count as active (standard workaround); Alt is used because it types nothing.
                keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
                keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            }

            SetForegroundWindow(hwnd);
            Thread.Sleep(100);
        }

        return GetForegroundWindow() == hwnd;
    }

    public static bool IsForeground(IntPtr hwnd) => GetForegroundWindow() == hwnd;

    public static uint ClipboardSequenceNumber() => GetClipboardSequenceNumber();

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();
}
