# PA CodeCopy

A small Windows desktop app that **saves the code you copy from Power Automate Desktop (PAD)** to your computer, so you can find it and reuse it later.

When you select actions in the PAD designer and press **Ctrl+C**, PAD puts those actions on the clipboard as text (PAD's scripting language). PA CodeCopy watches the clipboard and saves that text as a snippet. Later you can copy a snippet back and paste it into any flow with **Ctrl+V**.

```
SET Counter TO 0
Excel.LaunchExcel.LaunchAndOpenUnderExistingProcess Path: $'''C:\Data\Input.xlsx''' Visible: True ReadOnly: False Instance=> ExcelInstance
LOOP FOREACH CurrentItem IN ExcelData
    Variables.IncreaseVariable Value: Counter IncrementValue: 1
END
```

## Features

| Feature | Details |
|---|---|
| Auto-capture | Copies of PAD actions are saved automatically. Normal copies (emails, URLs, other code) are ignored. |
| Manual save | **Save from clipboard** (Ctrl+S) or **Paste & Save…** (paste any code and give it a name, category and tags). |
| No duplicates | Copying the same actions again does not create a second file. It only updates "Last copied". |
| Search | Search by name, tags **or code** (e.g. `SendEmail`), and filter by category. |
| Reuse | **Copy to clipboard** (or double-click) → paste into the PAD designer. |
| Organise | Rename, change the category and add tags (F2). The file is renamed or moved to match. |
| Tray | Minimise to the system tray. A balloon shows each auto-saved snippet. |
| Rebuild index | Re-scan the snippets folder (after a restore, a sync, or files added by hand). |
| Extract PAD flow | One click copies **all subflows** of the flow open in the PAD designer (see below). |

## Where data is stored

| What | Location |
|---|---|
| Snippet files (one `.txt` per snippet) | `Documents\PaCodeCopy\Snippets\<Category>\yyyyMMdd_HHmmss_<Name>.txt` (can be changed in Settings) |
| Search index (SQLite) | `%LOCALAPPDATA%\PaCodeCopy\index.db` |
| Settings | `%LOCALAPPDATA%\PaCodeCopy\settings.json` |
| Logs (one file per day) | `%LOCALAPPDATA%\PaCodeCopy\logs\` |

The `.txt` files are the source of truth. You can back them up, sync them with OneDrive, or put them in Git. If the index is lost, **Rebuild index** creates it again from the files.

> **Security note:** PAD flows sometimes contain passwords or keys as plain text. Snippets are stored **unencrypted** on your disk, so keep the snippets folder somewhere private. Snippet code is never written to the log files.

## Download (no build needed)

1. Download **[PaCodeCopy-win-x64.zip](https://github.com/Gopi-333/PA_CodeCopy/raw/claude/vigilant-goodall-m6rat4/downloads/PaCodeCopy-win-x64.zip)** (1.1 MB, Windows 64-bit).
2. Install the free **.NET 8 Desktop Runtime (x64)** once: https://dotnet.microsoft.com/download/dotnet/8.0
3. Right-click the zip → **Extract All** → double-click **PaCodeCopy.exe**.
   If Windows says "Windows protected your PC", click **More info → Run anyway** (the exe is not code-signed).

## Python version

A Python version with the same features lives in [`python/`](python/README.md). It uses only the standard library, and you can also download it as one exe:
**[PaCodeCopy-py.exe](https://github.com/Gopi-333/PA_CodeCopy/releases/download/python-latest/PaCodeCopy-py.exe)**.
It shares the snippets folder, index and settings with this app, so both show the same snippets.

## Requirements

- Windows 10/11
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) to run, or the .NET 8 SDK to build

## Build and run

```powershell
git clone https://github.com/gopi-333/pa_codecopy.git
cd pa_codecopy
dotnet build PA_CodeCopy.sln
dotnet run --project src/PaCodeCopy
```

Run the unit tests:

```powershell
dotnet test PA_CodeCopy.sln
```

Publish a single `PaCodeCopy.exe` to share with colleagues (they need the .NET 8 Desktop Runtime):

```powershell
dotnet publish src/PaCodeCopy -c Release -r win-x64 --self-contained false `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

Add `--self-contained true` if the target PCs do not have .NET installed (the exe becomes larger, about 150 MB).

## How to use with PAD

1. Start **PaCodeCopy.exe**. Leave **Auto-capture** on.
2. In the PAD designer, select one or more actions and press **Ctrl+C**.
3. The snippet appears at the top of the list. Its default name is the first action, e.g. `Excel.LaunchExcel +5 more`.
4. Press **F2** to give it a clear name, a category (e.g. `Excel`, `Web`, `Outlook`) and tags.
5. To reuse it, select the snippet, press **Ctrl+Shift+C** (or double-click), then paste into PAD with **Ctrl+V**.

### Keyboard shortcuts

| Keys | Action |
|---|---|
| Ctrl+S | Save from clipboard |
| Ctrl+F | Search |
| Ctrl+Shift+C / double-click | Copy selected snippet to clipboard |
| F2 | Edit name / category / tags |
| Del | Delete selected snippet |
| F5 | Refresh list |

## Extract a whole flow with one click

**Extract PAD flow** (toolbar or tray menu) copies **every subflow** of the flow that is open in the PAD designer and saves
each one as a snippet: **category = flow name**, **name = subflow name** (`Main`, `Login`, …).

1. In Power Automate Desktop, open the flow (select it → **Edit**). If several flows are open, PA CodeCopy asks which one.
2. In PA CodeCopy, click **Extract PAD flow**.
3. Don't use the mouse or keyboard while it runs: it clicks each subflow tab and presses **Ctrl+A**, **Ctrl+C**.
   A small box in the bottom-right corner shows progress. Press **Esc** to cancel.
4. A summary lists each subflow: `[+]` saved, `[=]` already saved (unchanged), `[ ]` empty, `[!]` failed.
   The list then shows only that flow's snippets.

Your flow is **not changed**: only selection and copy shortcuts are sent. Your clipboard text is put back afterwards.
Run it again after you change the flow: unchanged subflows are skipped, and changed ones are saved as new snippets.

**If a subflow is missing:** PAD's window structure is not documented, so the extractor finds the subflow tabs
(the tab strip that contains **Main**) and the action list with general rules. If they do not match your PAD version,
it copies only the subflow that is open and says so. Click **Scan PAD window…** and send the file it creates in
`Documents\PaCodeCopy\Diagnostics` (check it first: it can contain text from your flow), so the rules can be adjusted.

## Settings

| Setting | Default | Meaning |
|---|---|---|
| Snippets folder | `Documents\PaCodeCopy\Snippets` | Where new snippet files are written |
| Default category | `General` | Category for auto-saved snippets |
| Detection strictness | `3` | Minimum score for text to count as PAD code. Raise it if non-PAD text is captured. Lower it if very small copies are missed. |
| Keep logs (days) | `14` | Older log files are deleted on start-up |
| Auto-capture / Minimise to tray / Notifications | On | |

## How PAD code is detected

`PadScriptDetector` gives each line a score:

| Pattern | Example | Score |
|---|---|---|
| Control statement | `SET x TO 1`, `IF … THEN`, `LOOP FOREACH x IN list`, `CALL Sub`, `WAIT 2`, `ON ERROR` | +3 |
| Action with parameters | `Excel.CloseExcel.Close Instance: ExcelInstance` | +3 |
| Markers on those lines | `=>` output, `$'''text'''`, `%Variable%` | +1 each |
| `END`, `ELSE`, bare `Module.Action` | | +1 |
| `@@` annotations / `# [ControlRepository]` UI-element block | | +2 / +3 |

Text is treated as PAD code when the score reaches the strictness setting **and** at least one action or statement is found. Keywords are matched in upper case only, as PAD writes them. This keeps SQL such as `SET NOCOUNT ON` and normal prose from matching. Disabled actions (`DISABLE …`) are recognised too.

The detector is a heuristic. If your copies are missed or something else is captured, change **Detection strictness**, or use **Save from clipboard**, which asks before saving text that does not look like PAD code.

## Project structure

```
PA_CodeCopy.sln
src/PaCodeCopy.Core/          net8.0 class library (no UI, fully unit-tested)
  Configuration/              AppSettings, AppPaths, SettingsStore (JSON)
  Detection/                  PadScriptDetector
  Logging/                    IAppLogger, FileLogger (daily rolling files)
  Models/                     Snippet, SaveResult
  Services/                   SnippetService (detect → de-duplicate → write file → index)
  Storage/                    SnippetFileStore (.txt files), SqliteSnippetRepository (index)
src/PaCodeCopy/               net8.0-windows WinForms app
  Interop/                    ClipboardMonitor (Win32 AddClipboardFormatListener), ClipboardHelper (retries)
  Forms/                      MainForm, SnippetEditForm, SettingsForm
  Program.cs                  single-instance start-up, global exception handling
tests/PaCodeCopy.Tests/       xUnit tests for the Core library
```

### Design notes

- **No polling.** The clipboard is watched with the Win32 `AddClipboardFormatListener` API (`WM_CLIPBOARDUPDATE`).
- **Clipboard locks.** Another app may hold the clipboard open briefly. Reads are retried 3 times, 100 ms apart.
- **File and index stay in sync.** The file is written first. If the index insert fails, the file is deleted again.
- **Duplicates.** A SHA-256 hash of the code is used, ignoring line endings and trailing spaces. The hash has a unique index in SQLite.
- **Safe file names.** Windows-invalid characters and reserved names (`CON`, `NUL`, …) are replaced, so a name or category can never escape the snippets folder.
- **Single instance.** A named mutex stops two copies from saving every clip twice.
- **Your own copies are ignored.** Copying a snippet back to the clipboard does not trigger auto-capture.

## Manual test checklist (Windows)

1. Copy 2–3 actions in PAD → a snippet appears and a `.txt` file is created in the category folder.
2. Copy a normal sentence in Notepad → nothing is saved.
3. Copy the same PAD actions again → still one snippet; "Last copied" is updated.
4. Select a snippet → **Copy to clipboard** → paste into a PAD flow → the actions appear.
5. Edit the name/category → the file is renamed/moved.
6. Delete the `.txt` file in Explorer → **Rebuild index** → the snippet disappears from the list.
7. Minimise → the window goes to the tray; copy PAD actions → a balloon appears.
8. Start a second instance → a message says it is already running.

## Troubleshooting

| Problem | Fix |
|---|---|
| PAD copies are not saved | Check that **Auto-capture** is on. Lower **Detection strictness**. Check the log in `%LOCALAPPDATA%\PaCodeCopy\logs`. |
| Other text is saved | Raise **Detection strictness**. |
| "Clipboard is locked" in the log | Another app was holding the clipboard. Copy again or use **Save from clipboard**. |
| Snippets missing after moving the folder | **Settings → Snippets folder**, then **Rebuild index**. |
