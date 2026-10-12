using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PaCodeCopy.Interop;

/// <summary>
/// Raises <see cref="ClipboardChanged"/> whenever any application changes the clipboard.
/// Uses the Win32 clipboard format listener (Windows Vista+), so there is no polling.
/// </summary>
internal sealed class ClipboardMonitor : NativeWindow, IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;

    // Message-only window: receives messages but is never shown.
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private bool _disposed;

    public ClipboardMonitor()
    {
        CreateHandle(new CreateParams { Parent = HWND_MESSAGE });

        if (!AddClipboardFormatListener(Handle))
        {
            var error = Marshal.GetLastWin32Error();
            DestroyHandle();
            throw new Win32Exception(error, "Could not start watching the clipboard.");
        }
    }

    public event EventHandler? ClipboardChanged;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_CLIPBOARDUPDATE)
        {
            ClipboardChanged?.Invoke(this, EventArgs.Empty);
        }

        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (Handle != IntPtr.Zero)
        {
            RemoveClipboardFormatListener(Handle);
            DestroyHandle();
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
}
