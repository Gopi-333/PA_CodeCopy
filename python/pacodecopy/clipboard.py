"""Clipboard access.

Windows: Win32 API through ctypes (no extra packages). GetClipboardSequenceNumber() changes on every
copy, so checking it twice a second is very cheap. Other systems (used for testing): Tk's clipboard.
"""

from __future__ import annotations

import logging
import sys
import time

_MAX_ATTEMPTS = 3
_RETRY_DELAY_S = 0.1


class TkClipboard:
    """Fallback for non-Windows systems. Has no sequence number, so callers compare the text."""

    def __init__(self, root, logger: logging.Logger) -> None:
        self._root = root
        self._logger = logger

    def sequence(self) -> int | None:
        return None

    def get_text(self) -> str | None:
        try:
            return self._root.clipboard_get()
        except Exception:  # TclError when the clipboard is empty or holds no text
            return None

    def set_text(self, text: str) -> bool:
        try:
            self._root.clipboard_clear()
            self._root.clipboard_append(text)
            self._root.update()
            return True
        except Exception:
            self._logger.warning("Could not write to the clipboard.", exc_info=True)
            return False


class WindowsClipboard:
    CF_UNICODETEXT = 13
    GMEM_MOVEABLE = 0x0002

    def __init__(self, logger: logging.Logger) -> None:
        import ctypes
        from ctypes import wintypes

        self._ctypes = ctypes
        self._logger = logger
        user32 = ctypes.WinDLL("user32", use_last_error=True)
        kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)

        def fn(dll, name, restype, *argtypes):
            f = getattr(dll, name)
            f.restype = restype
            f.argtypes = argtypes
            return f

        self._open = fn(user32, "OpenClipboard", wintypes.BOOL, wintypes.HWND)
        self._close = fn(user32, "CloseClipboard", wintypes.BOOL)
        self._empty = fn(user32, "EmptyClipboard", wintypes.BOOL)
        self._get_data = fn(user32, "GetClipboardData", wintypes.HANDLE, wintypes.UINT)
        self._set_data = fn(user32, "SetClipboardData", wintypes.HANDLE, wintypes.UINT, wintypes.HANDLE)
        self._is_available = fn(user32, "IsClipboardFormatAvailable", wintypes.BOOL, wintypes.UINT)
        self._sequence = fn(user32, "GetClipboardSequenceNumber", wintypes.DWORD)
        self._alloc = fn(kernel32, "GlobalAlloc", wintypes.HGLOBAL, wintypes.UINT, ctypes.c_size_t)
        self._lock = fn(kernel32, "GlobalLock", wintypes.LPVOID, wintypes.HGLOBAL)
        self._unlock = fn(kernel32, "GlobalUnlock", wintypes.BOOL, wintypes.HGLOBAL)
        self._free = fn(kernel32, "GlobalFree", wintypes.HGLOBAL, wintypes.HGLOBAL)

    def sequence(self) -> int | None:
        return int(self._sequence())

    def _open_with_retry(self) -> bool:
        # Another app (often the one that just copied) can hold the clipboard open for a moment.
        for attempt in range(_MAX_ATTEMPTS):
            if self._open(None):
                return True
            if attempt < _MAX_ATTEMPTS - 1:
                time.sleep(_RETRY_DELAY_S)
        self._logger.warning("Clipboard is locked by another application; operation skipped.")
        return False

    def get_text(self) -> str | None:
        if not self._is_available(self.CF_UNICODETEXT) or not self._open_with_retry():
            return None
        try:
            handle = self._get_data(self.CF_UNICODETEXT)
            if not handle:
                return None
            pointer = self._lock(handle)
            if not pointer:
                return None
            try:
                return self._ctypes.wstring_at(pointer)
            finally:
                self._unlock(handle)
        finally:
            self._close()

    def set_text(self, text: str) -> bool:
        data = text.encode("utf-16-le") + b"\x00\x00"
        handle = self._alloc(self.GMEM_MOVEABLE, len(data))
        if not handle:
            return False
        pointer = self._lock(handle)
        if not pointer:
            self._free(handle)
            return False
        self._ctypes.memmove(pointer, data, len(data))
        self._unlock(handle)

        if not self._open_with_retry():
            self._free(handle)
            return False
        try:
            self._empty()
            if not self._set_data(self.CF_UNICODETEXT, handle):
                self._free(handle)  # still ours when SetClipboardData fails
                self._logger.warning("SetClipboardData failed.")
                return False
            return True  # the clipboard owns the memory now
        finally:
            self._close()


def create_clipboard(root, logger: logging.Logger):
    if sys.platform == "win32":
        try:
            return WindowsClipboard(logger)
        except (OSError, AttributeError):
            logger.warning("Win32 clipboard unavailable; using Tk clipboard.", exc_info=True)
    return TkClipboard(root, logger)
