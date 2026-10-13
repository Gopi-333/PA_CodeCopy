"""PA CodeCopy (Python version) - start with:  python pa_codecopy.py"""

from __future__ import annotations

import logging
import sys
import tkinter as tk
from tkinter import messagebox

from pacodecopy.clipboard import create_clipboard
from pacodecopy.config import AppPaths, SettingsStore
from pacodecopy.detector import PadScriptDetector
from pacodecopy.filestore import SnippetFileStore
from pacodecopy.log import setup_logging
from pacodecopy.repository import SqliteSnippetRepository
from pacodecopy.service import SnippetService
from pacodecopy.single_instance import SingleInstance
from pacodecopy.ui import APP_TITLE, MainWindow


def _enable_high_dpi() -> None:
    """Sharp text on high-DPI Windows screens."""
    if sys.platform == "win32":
        try:
            import ctypes

            ctypes.windll.shcore.SetProcessDpiAwareness(1)
        except (OSError, AttributeError):
            pass


def main() -> int:
    paths = AppPaths.default()
    logger = setup_logging(paths.log_folder, retention_days=14)

    _enable_high_dpi()
    root = tk.Tk()
    root.withdraw()

    def report_error(exc_type, exc, tb) -> None:
        logger.error("Unexpected error.", exc_info=(exc_type, exc, tb))
        messagebox.showerror(
            APP_TITLE,
            f"An unexpected error occurred:\n{exc}\n\nDetails were written to:\n{paths.log_folder}\n\n"
            "The application will keep running.",
            parent=root,
        )

    root.report_callback_exception = report_error  # errors inside button handlers etc.

    instance = SingleInstance(paths.data_folder)
    if not instance.is_first:
        messagebox.showinfo(APP_TITLE, "PA CodeCopy is already running (the Python or the C# version).", parent=root)
        root.destroy()
        return 1

    try:
        settings_store = SettingsStore(paths.settings_path, paths.default_snippets_root, logger)
        settings = settings_store.load()

        repository = SqliteSnippetRepository(paths.database_path)
        repository.initialize()
        service = SnippetService(
            repository,
            SnippetFileStore(settings.snippets_root_folder),
            PadScriptDetector(settings.detection_threshold),
            logger,
        )
        service.default_category = settings.default_category

        logger.info("PA CodeCopy (Python) started. Index: '%s', snippets: '%s'.",
                    paths.database_path, service.file_store.root_folder)
        MainWindow(root, service, settings_store, settings, create_clipboard(root, logger), logger)
        root.deiconify()
        root.mainloop()
        logger.info("PA CodeCopy (Python) stopped.")
        return 0
    except Exception as ex:  # start-up failure: tell the user instead of disappearing silently
        logging.getLogger("pacodecopy").error("Start-up failed.", exc_info=True)
        messagebox.showerror(APP_TITLE, f"PA CodeCopy could not start:\n{ex}\n\nLog folder:\n{paths.log_folder}", parent=root)
        return 1
    finally:
        del instance


if __name__ == "__main__":
    sys.exit(main())
