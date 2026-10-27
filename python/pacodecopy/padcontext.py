"""Finds out which PAD flow a copy came from, so auto-capture can save it in that flow's folder.

Port of PadWindowTitle.cs / PadCopyContext.cs (C# app). The Python version reads only the flow name;
the open subflow would need UI Automation, which the standard library does not have.
"""

from __future__ import annotations

import sys

DESIGNER_PROCESS_PREFIX = "pad.designer"
_SEPARATORS = (" | ", " - ", " – ")


def parse_flow_name(title: str | None) -> str:
    """Flow name from a designer window title, e.g. 'Invoice processing | Power Automate' -> 'Invoice processing'."""
    if title is None or not title.strip():
        return "PAD flow"
    text = title.strip().lstrip("*").strip()  # "*" marks unsaved changes in some editors
    for separator in _SEPARATORS:
        index = text.find(separator)
        if index > 0:
            return text[:index].strip()
    return text


def detect_flow_name() -> str | None:
    """Flow name of the PAD designer window behind the current copy, or None (other app, or not Windows).

    Looks at the foreground window first (the user just pressed Ctrl+C in PAD), then at the process that owns
    the clipboard (covers 'Save from clipboard' clicked in this app).
    """
    if sys.platform != "win32":
        return None
    try:
        return _WindowsLookup().flow_name()
    except OSError:
        return None


class _WindowsLookup:
    PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
    GW_OWNER = 4

    def __init__(self) -> None:
        import ctypes
        from ctypes import wintypes

        self._ctypes = ctypes
        self._wintypes = wintypes
        self._user32 = ctypes.WinDLL("user32", use_last_error=True)
        self._kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        u, k = self._user32, self._kernel32
        u.GetForegroundWindow.restype = wintypes.HWND
        u.GetClipboardOwner.restype = wintypes.HWND
        u.GetWindowThreadProcessId.argtypes = (wintypes.HWND, ctypes.POINTER(wintypes.DWORD))
        u.GetWindowTextLengthW.argtypes = (wintypes.HWND,)
        u.GetWindowTextW.argtypes = (wintypes.HWND, wintypes.LPWSTR, ctypes.c_int)
        u.IsWindowVisible.argtypes = (wintypes.HWND,)
        u.GetWindow.argtypes = (wintypes.HWND, wintypes.UINT)
        u.GetWindow.restype = wintypes.HWND
        k.OpenProcess.restype = wintypes.HANDLE
        k.OpenProcess.argtypes = (wintypes.DWORD, wintypes.BOOL, wintypes.DWORD)
        k.QueryFullProcessImageNameW.argtypes = (wintypes.HANDLE, wintypes.DWORD, wintypes.LPWSTR, ctypes.POINTER(wintypes.DWORD))
        k.CloseHandle.argtypes = (wintypes.HANDLE,)

    def flow_name(self) -> str | None:
        foreground = self._user32.GetForegroundWindow()
        if foreground and self._is_designer(self._process_id(foreground)):
            title = self._title(foreground)
            if title:
                return parse_flow_name(title)

        # WPF apps (like PAD) own the clipboard through a hidden window: use the process's main window.
        owner_pid = self._process_id(self._user32.GetClipboardOwner())
        if self._is_designer(owner_pid):
            title = self._main_window_title(owner_pid)
            if title:
                return parse_flow_name(title)
        return None

    def _process_id(self, hwnd) -> int:
        if not hwnd:
            return 0
        pid = self._wintypes.DWORD()
        self._user32.GetWindowThreadProcessId(hwnd, self._ctypes.byref(pid))
        return pid.value

    def _is_designer(self, pid: int) -> bool:
        import os

        if pid == 0 or pid == os.getpid():
            return False
        handle = self._kernel32.OpenProcess(self.PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
        if not handle:
            return False
        try:
            size = self._wintypes.DWORD(1024)
            buffer = self._ctypes.create_unicode_buffer(size.value)
            if not self._kernel32.QueryFullProcessImageNameW(handle, 0, buffer, self._ctypes.byref(size)):
                return False
            exe_name = buffer.value.replace("/", "\\").rsplit("\\", 1)[-1].lower()
            return exe_name.startswith(DESIGNER_PROCESS_PREFIX)
        finally:
            self._kernel32.CloseHandle(handle)

    def _title(self, hwnd) -> str:
        length = self._user32.GetWindowTextLengthW(hwnd)
        if length <= 0:
            return ""
        buffer = self._ctypes.create_unicode_buffer(length + 1)
        self._user32.GetWindowTextW(hwnd, buffer, length + 1)
        return buffer.value.strip()

    def _main_window_title(self, pid: int) -> str | None:
        found: list[str] = []
        callback_type = self._ctypes.WINFUNCTYPE(self._wintypes.BOOL, self._wintypes.HWND, self._wintypes.LPARAM)

        def callback(hwnd, _lparam):
            if (self._user32.IsWindowVisible(hwnd) and not self._user32.GetWindow(hwnd, self.GW_OWNER)
                    and self._process_id(hwnd) == pid):
                title = self._title(hwnd)
                if title:
                    found.append(title)
                    return False  # stop
            return True

        self._user32.EnumWindows(callback_type(callback), 0)
        return found[0] if found else None
