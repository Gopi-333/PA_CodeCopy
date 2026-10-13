"""Only one PA CodeCopy may run, so copies are not saved twice.

Uses the same mutex name as the C# app, so the Python and C# versions also exclude each other.
"""

from __future__ import annotations

import sys
from pathlib import Path

MUTEX_NAME = "Local\\PaCodeCopy.SingleInstance"
_ERROR_ALREADY_EXISTS = 183


class SingleInstance:
    def __init__(self, lock_folder: Path) -> None:
        self._handle = None
        self._lock_file = None
        self.is_first = self._acquire(lock_folder)

    def _acquire(self, lock_folder: Path) -> bool:
        if sys.platform == "win32":
            import ctypes

            kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
            kernel32.CreateMutexW.restype = ctypes.c_void_p
            self._handle = kernel32.CreateMutexW(None, False, MUTEX_NAME)
            return ctypes.get_last_error() != _ERROR_ALREADY_EXISTS

        import fcntl

        lock_folder.mkdir(parents=True, exist_ok=True)
        self._lock_file = open(lock_folder / "pacodecopy.lock", "w")
        try:
            fcntl.flock(self._lock_file, fcntl.LOCK_EX | fcntl.LOCK_NB)
            return True
        except OSError:
            return False
