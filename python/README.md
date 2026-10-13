# PA CodeCopy — Python version

Same tool as the C# app: it **saves the code you copy from Power Automate Desktop** and lets you search and reuse it.
It only uses the Python standard library (Tkinter, sqlite3, ctypes). There is nothing to `pip install`.

## Download the ready-made exe (no Python needed)

**[PaCodeCopy-py.exe](https://github.com/Gopi-333/PA_CodeCopy/releases/download/python-latest/PaCodeCopy-py.exe)**
(built automatically by GitHub Actions from this folder).

Double-click it. If Windows says "Windows protected your PC", click **More info → Run anyway** (the exe is not code-signed).
Some antivirus tools also flag unsigned PyInstaller exes by mistake. If yours does, run the `.py` version below instead.

## Run with Python

1. Install Python 3.10 or newer from python.org (tick **"Add python.exe to PATH"**; Tkinter is included).
2. Double-click **`run.bat`**, or in a terminal:
   ```
   cd python
   python pa_codecopy.py
   ```

## Build the exe yourself

Double-click **`build_exe.bat`**. It installs PyInstaller and creates `dist\PaCodeCopy-py.exe`.

## Works together with the C# version

Both versions use the **same data**, so you see the same snippets in either app:

| What | Location |
|---|---|
| Snippet files | `Documents\PaCodeCopy\Snippets\<Category>\yyyyMMdd_HHmmss_<Name>.txt` |
| Search index (SQLite, same table and date format) | `%LOCALAPPDATA%\PaCodeCopy\index.db` |
| Settings (same `settings.json`) | `%LOCALAPPDATA%\PaCodeCopy\settings.json` |
| Logs | `%LOCALAPPDATA%\PaCodeCopy\logs\pacodecopy-py-YYYYMMDD.log` |

Only **one** of the two apps can run at a time (they share a lock), so each copy is saved once.

## Differences from the C# version

| | C# | Python |
|---|---|---|
| Clipboard watching | Windows event (`AddClipboardFormatListener`) | Checks `GetClipboardSequenceNumber` every 0.5 s (very cheap) |
| System-tray icon and balloons | Yes | No (Tkinter has none; it would need the extra `pystray` package). Minimise goes to the taskbar |
| Runs on | Windows | Windows (macOS/Linux work for testing, using Tk's clipboard) |

Everything else (detector, duplicate check, file names, search, edit, delete, rebuild index, settings,
keyboard shortcuts, green/red **Auto-capture: ON/OFF** switch) behaves the same.

## Code structure

```
pa_codecopy.py            entry point
pacodecopy/
  detector.py             PAD code detection (same patterns and scores as PadScriptDetector.cs)
  filestore.py            .txt files, safe file names
  repository.py           SQLite index (.NET-compatible dates)
  service.py              save / duplicate check / rollback / edit / delete / rebuild
  clipboard.py            Win32 clipboard through ctypes, with retries
  single_instance.py      shared lock with the C# app
  config.py, log.py       settings.json and log files
  ui.py                   Tkinter window and dialogs
tests/                    unittest: python -m unittest discover -s tests -t .
```
