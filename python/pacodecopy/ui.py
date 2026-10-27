"""Tkinter user interface: main window, edit/paste dialog and settings dialog."""

from __future__ import annotations

import logging
import os
import sqlite3
import subprocess
import sys
import time
import tkinter as tk
from datetime import datetime, timedelta, timezone
from tkinter import filedialog, messagebox, ttk

from .config import MAX_DETECTION_THRESHOLD, MIN_DETECTION_THRESHOLD, AppSettings, SettingsStore
from .models import SaveOutcome, SaveResult, Snippet, SnippetSource
from .padcontext import detect_flow_name
from .service import SnippetService, compute_hash

APP_TITLE = "PA CodeCopy (Python) - Power Automate Desktop snippets"
ALL_CATEGORIES = "(All categories)"
POLL_INTERVAL_MS = 500
SEARCH_DELAY_MS = 300
SELF_COPY_WINDOW_S = 3.0
MONO_FONT = ("Consolas", 10) if sys.platform == "win32" else ("DejaVu Sans Mono", 10)


class MainWindow:
    def __init__(
        self,
        root: tk.Tk,
        service: SnippetService,
        settings_store: SettingsStore,
        settings: AppSettings,
        clipboard,
        logger: logging.Logger,
    ) -> None:
        self.root = root
        self._service = service
        self._settings_store = settings_store
        self._settings = settings
        self._clipboard = clipboard
        self._logger = logger
        self._snippets: dict[str, Snippet] = {}
        self._search_job: str | None = None
        self._self_copied_hash: str | None = None
        self._self_copied_at = 0.0
        # Baseline: text already on the clipboard at start-up is not captured (same as the C# app).
        self._last_sequence = clipboard.sequence()
        self._last_text = clipboard.get_text() if self._last_sequence is None else None

        root.title(APP_TITLE)
        root.geometry("1100x680")
        root.minsize(700, 400)
        self._build_layout()
        self._bind_keys()
        self._apply_auto_capture_look()
        self._reload_categories()
        self._reload_list()
        self._set_status(f"Snippets folder: {service.file_store.root_folder}")
        root.after(POLL_INTERVAL_MS, self._poll_clipboard)

    # ------------------------------------------------------------------ layout

    def _build_layout(self) -> None:
        toolbar = ttk.Frame(self.root, padding=(4, 4))
        toolbar.pack(side=tk.TOP, fill=tk.X)
        buttons = [
            ("Save from clipboard", self.save_from_clipboard),
            ("Paste & Save...", self.paste_and_save),
            None,
            ("Copy to clipboard", self.copy_selected_to_clipboard),
            ("Edit...", self.edit_selected),
            ("Delete", self.delete_selected),
            None,
            ("Open folder", self.open_folder),
            ("Rebuild index", self.rebuild_index),
            None,
        ]
        for item in buttons:
            if item is None:
                ttk.Separator(toolbar, orient=tk.VERTICAL).pack(side=tk.LEFT, fill=tk.Y, padx=4)
            else:
                ttk.Button(toolbar, text=item[0], command=item[1]).pack(side=tk.LEFT, padx=1)

        # tk.Button (not ttk) so the green/red text colour works with every theme.
        self._auto_button = tk.Button(toolbar, command=self.toggle_auto_capture, relief=tk.GROOVE,
                                      font=("TkDefaultFont", 9, "bold"), padx=8)
        self._auto_button.pack(side=tk.LEFT, padx=2)
        ttk.Button(toolbar, text="Settings...", command=self.open_settings).pack(side=tk.LEFT, padx=1)

        status = ttk.Frame(self.root, relief=tk.SUNKEN, padding=(4, 1))
        status.pack(side=tk.BOTTOM, fill=tk.X)
        self._status_var = tk.StringVar()
        self._count_var = tk.StringVar()
        ttk.Label(status, textvariable=self._count_var).pack(side=tk.RIGHT)
        ttk.Label(status, textvariable=self._status_var, anchor=tk.W).pack(side=tk.LEFT, fill=tk.X, expand=True)

        panes = ttk.PanedWindow(self.root, orient=tk.HORIZONTAL)
        panes.pack(side=tk.TOP, fill=tk.BOTH, expand=True, padx=6, pady=(0, 6))

        left = ttk.Frame(panes)
        ttk.Label(left, text="Search name, tags or code (Ctrl+F):").pack(anchor=tk.W)
        self._search_var = tk.StringVar()
        self._search_entry = ttk.Entry(left, textvariable=self._search_var)
        self._search_entry.pack(fill=tk.X, pady=(0, 4))
        self._search_var.trace_add("write", lambda *_: self._schedule_search())

        self._category_var = tk.StringVar(value=ALL_CATEGORIES)
        self._category_box = ttk.Combobox(left, textvariable=self._category_var, state="readonly")
        self._category_box.pack(fill=tk.X, pady=(0, 4))
        self._category_box.bind("<<ComboboxSelected>>", lambda _e: self._reload_list())

        tree_frame = ttk.Frame(left)
        tree_frame.pack(fill=tk.BOTH, expand=True)
        columns = ("category", "actions", "last")
        self._tree = ttk.Treeview(tree_frame, columns=columns, selectmode="browse")
        self._tree.heading("#0", text="Name", anchor=tk.W)
        self._tree.heading("category", text="Category", anchor=tk.W)
        self._tree.heading("actions", text="Actions", anchor=tk.E)
        self._tree.heading("last", text="Last copied", anchor=tk.W)
        self._tree.column("#0", width=230, stretch=True)
        self._tree.column("category", width=90, stretch=False)
        self._tree.column("actions", width=55, anchor=tk.E, stretch=False)
        self._tree.column("last", width=125, stretch=False)
        tree_scroll = ttk.Scrollbar(tree_frame, orient=tk.VERTICAL, command=self._tree.yview)
        self._tree.configure(yscrollcommand=tree_scroll.set)
        self._tree.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        tree_scroll.pack(side=tk.RIGHT, fill=tk.Y)
        self._tree.bind("<<TreeviewSelect>>", lambda _e: self._show_preview())
        self._tree.bind("<Double-1>", lambda _e: self.copy_selected_to_clipboard())
        self._tree.bind("<Delete>", lambda _e: self.delete_selected())

        right = ttk.Frame(panes)
        self._preview = tk.Text(right, wrap=tk.NONE, font=MONO_FONT, state=tk.DISABLED, undo=False)
        y_scroll = ttk.Scrollbar(right, orient=tk.VERTICAL, command=self._preview.yview)
        x_scroll = ttk.Scrollbar(right, orient=tk.HORIZONTAL, command=self._preview.xview)
        self._preview.configure(yscrollcommand=y_scroll.set, xscrollcommand=x_scroll.set)
        x_scroll.pack(side=tk.BOTTOM, fill=tk.X)
        y_scroll.pack(side=tk.RIGHT, fill=tk.Y)
        self._preview.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)

        panes.add(left, weight=2)
        panes.add(right, weight=3)

        # Set the list width once the window is really shown. Setting it earlier (while the window is
        # still hidden and 1 px wide) squeezes the list to 0 px on Windows.
        def place_sash(_event=None) -> None:
            if panes.winfo_width() > 600:
                panes.sashpos(0, 500)
                panes.unbind("<Configure>", bind_id)

        bind_id = panes.bind("<Configure>", place_sash, add="+")

    def _bind_keys(self) -> None:
        # Windows users expect Ctrl+A to select all text; Tk does not do that by default everywhere.
        for widget_class in ("TEntry", "TCombobox", "TSpinbox"):
            self.root.bind_class(widget_class, "<Control-a>", _select_all_entry)
        self.root.bind_class("Text", "<Control-a>", _select_all_text)
        self.root.bind_all("<Control-s>", lambda _e: self.save_from_clipboard())
        self.root.bind_all("<Control-f>", lambda _e: self._focus_search())
        self.root.bind_all("<Control-Shift-C>", lambda _e: self.copy_selected_to_clipboard())
        self.root.bind_all("<F2>", lambda _e: self.edit_selected())
        self.root.bind_all("<F5>", lambda _e: self._reload_list())

    def _focus_search(self) -> str:
        self._search_entry.focus_set()
        self._search_entry.select_range(0, tk.END)
        return "break"

    # ------------------------------------------------------------------ clipboard watching

    def _poll_clipboard(self) -> None:
        try:
            if self._settings.auto_capture:
                text = self._read_changed_clipboard()
                if text is not None and not self._is_own_copy(text):
                    self._auto_save(text)
            else:
                self._read_changed_clipboard()  # keep the baseline current while OFF
        except Exception:  # never let one bad read stop the polling loop
            self._logger.error("Clipboard check failed.", exc_info=True)
        finally:
            self.root.after(POLL_INTERVAL_MS, self._poll_clipboard)

    def _read_changed_clipboard(self) -> str | None:
        """Returns clipboard text if the clipboard changed since the last check, else None."""
        sequence = self._clipboard.sequence()
        if sequence is not None:
            if sequence == self._last_sequence:
                return None
            self._last_sequence = sequence
            return self._clipboard.get_text()

        text = self._clipboard.get_text()  # no sequence number: compare the text itself
        if text == self._last_text:
            return None
        self._last_text = text
        return text

    def _is_own_copy(self, text: str) -> bool:
        """True when the clipboard holds what this app copied a moment ago ("Copy to clipboard")."""
        if self._self_copied_hash is None:
            return False
        if time.monotonic() - self._self_copied_at <= SELF_COPY_WINDOW_S and compute_hash(text) == self._self_copied_hash:
            return True
        self._self_copied_hash = None
        return False

    def _auto_save(self, text: str) -> None:
        if not self._service.analyze(text).is_pad_script:
            return  # ordinary copies are ignored silently
        # Save into the folder of the PAD flow the code was copied from (default folder otherwise).
        result = self._service.save(text, SnippetSource.AUTO, require_pad_script=True, category=detect_flow_name())
        if result.outcome == SaveOutcome.SAVED:
            self._after_save(result)
        elif result.outcome == SaveOutcome.DUPLICATE:
            # One Ctrl+C can raise several clipboard changes; keep the "Saved …" message of a fresh save.
            if result.snippet and datetime.now(timezone.utc) - result.snippet.created_at < timedelta(seconds=5):
                return
            self._set_status(result.message)
            self._reload_list(select_id=result.snippet.id if result.snippet else None)
        elif result.outcome == SaveOutcome.FAILED:
            self._set_status(result.message)
        # NOT_PAD_SCRIPT / EMPTY: ordinary copies are ignored silently.

    # ------------------------------------------------------------------ actions

    def toggle_auto_capture(self) -> None:
        self._settings.auto_capture = not self._settings.auto_capture
        # Start from the current clipboard so turning ON does not capture an old copy.
        self._last_sequence = self._clipboard.sequence()
        self._last_text = self._clipboard.get_text() if self._last_sequence is None else None
        self._apply_auto_capture_look()
        self._save_settings()
        self._set_status(
            "Auto-capture is ON. Copy actions in PAD (Ctrl+C) and they are saved automatically."
            if self._settings.auto_capture
            else "Auto-capture is OFF. Use 'Save from clipboard' (Ctrl+S) to save manually."
        )

    def _apply_auto_capture_look(self) -> None:
        on = self._settings.auto_capture
        self._auto_button.configure(
            text="Auto-capture: ON" if on else "Auto-capture: OFF",
            fg="#1b6e1b" if on else "#b22222",
            activeforeground="#1b6e1b" if on else "#b22222",
        )

    def save_from_clipboard(self) -> None:
        text = self._clipboard.get_text()
        if text is None or not text.strip():
            self._set_status("The clipboard has no text.")
            return
        if not self._service.analyze(text).is_pad_script and not messagebox.askyesno(
            APP_TITLE,
            "The clipboard text does not look like Power Automate Desktop code.\n\nSave it anyway?",
            parent=self.root,
        ):
            return
        self._handle_manual_result(
            self._service.save(text, SnippetSource.MANUAL, require_pad_script=False, category=detect_flow_name())
        )

    def paste_and_save(self) -> None:
        dialog = SnippetDialog(self.root, "Paste & Save", self._service.get_categories(), "",
                               self._settings.default_category, "", show_code=True)
        if dialog.result is None:
            return
        name, category, tags, code = dialog.result
        self._handle_manual_result(
            self._service.save(code, SnippetSource.MANUAL, require_pad_script=False, name=name, category=category, tags=tags)
        )

    def _handle_manual_result(self, result: SaveResult) -> None:
        if result.outcome == SaveOutcome.SAVED:
            self._after_save(result)
            return
        if result.outcome == SaveOutcome.DUPLICATE and result.snippet:
            self._reload_list(select_id=result.snippet.id)
        self._set_status(result.message)
        if result.outcome == SaveOutcome.FAILED:
            messagebox.showerror(APP_TITLE, result.message, parent=self.root)

    def _after_save(self, result: SaveResult) -> None:
        self._reload_categories()
        self._reload_list(select_id=result.snippet.id if result.snippet else None)
        snippet = result.snippet
        self._set_status(f"Saved '{snippet.name}' in folder '{snippet.category}'." if snippet else result.message)

    def copy_selected_to_clipboard(self) -> str:
        snippet = self._selected()
        if snippet is None:
            return "break"
        content = self._service.read_content(snippet)
        self._self_copied_hash = compute_hash(content)
        self._self_copied_at = time.monotonic()
        if self._clipboard.set_text(content):
            self._set_status(f"Copied '{snippet.name}'. Paste it into the PAD designer with Ctrl+V.")
        else:
            self._self_copied_hash = None
            self._set_status("Could not write to the clipboard. Try again.")
        return "break"

    def edit_selected(self) -> None:
        snippet = self._selected()
        if snippet is None:
            return
        dialog = SnippetDialog(self.root, "Edit snippet", self._service.get_categories(), snippet.name,
                               snippet.category, snippet.tags, show_code=False)
        if dialog.result is None:
            return
        name, category, tags, _ = dialog.result
        try:
            self._service.update_details(snippet, name, category, tags)
            self._reload_categories()
            self._reload_list(select_id=snippet.id)
            self._set_status(f"Updated '{snippet.name}'.")
        except (OSError, sqlite3.Error, ValueError) as ex:
            self._logger.error("Failed to update snippet #%d.", snippet.id, exc_info=True)
            messagebox.showerror(APP_TITLE, f"Could not update the snippet:\n{ex}", parent=self.root)
            self._reload_list(select_id=snippet.id)

    def delete_selected(self) -> None:
        snippet = self._selected()
        if snippet is None:
            return
        if not messagebox.askyesno(APP_TITLE, f"Delete '{snippet.name}'?\nThe file will be removed from disk.",
                                   icon=messagebox.WARNING, default=messagebox.NO, parent=self.root):
            return
        try:
            self._service.delete(snippet)
            self._reload_categories()
            self._reload_list()
            self._set_status(f"Deleted '{snippet.name}'.")
        except sqlite3.Error as ex:
            self._logger.error("Failed to delete snippet #%d.", snippet.id, exc_info=True)
            messagebox.showerror(APP_TITLE, f"Could not delete the snippet:\n{ex}", parent=self.root)

    def open_folder(self) -> None:
        snippet = self._selected()
        try:
            if sys.platform == "win32":
                if snippet and os.path.exists(snippet.file_path):
                    subprocess.Popen(f'explorer /select,"{snippet.file_path}"')
                else:
                    self._service.file_store.root_folder.mkdir(parents=True, exist_ok=True)
                    os.startfile(self._service.file_store.root_folder)  # type: ignore[attr-defined]
            else:
                folder = os.path.dirname(snippet.file_path) if snippet else self._service.file_store.root_folder
                subprocess.Popen(["xdg-open", str(folder)])
        except OSError as ex:
            self._logger.warning("Could not open the snippets folder.", exc_info=True)
            self._set_status(f"Could not open the folder: {ex}")

    def rebuild_index(self) -> None:
        if not messagebox.askokcancel(
            APP_TITLE,
            f"Re-scan all .txt files in:\n{self._service.file_store.root_folder}\n\nand rebuild the search index? "
            "Snippets whose files are outside this folder will be removed from the list.",
            parent=self.root,
        ):
            return
        try:
            self.root.config(cursor="watch")
            self.root.update_idletasks()
            count = self._service.rebuild_index()
            self._reload_categories()
            self._reload_list()
            self._set_status(f"Index rebuilt: {count} snippets.")
        except (OSError, sqlite3.Error) as ex:
            self._logger.error("Rebuild index failed.", exc_info=True)
            messagebox.showerror(APP_TITLE, f"Rebuild failed:\n{ex}", parent=self.root)
        finally:
            self.root.config(cursor="")

    def open_settings(self) -> None:
        dialog = SettingsDialog(self.root, self._settings)
        if dialog.result is None:
            return
        updated: AppSettings = dialog.result
        folder_changed = os.path.normcase(os.path.abspath(updated.snippets_root_folder)) != os.path.normcase(
            str(self._service.file_store.root_folder)
        )
        auto_changed = updated.auto_capture != self._settings.auto_capture

        self._settings = updated
        self._service.default_category = updated.default_category
        self._service.set_detection_threshold(updated.detection_threshold)
        if folder_changed:
            self._service.change_snippets_folder(updated.snippets_root_folder)
        if auto_changed:
            self._last_sequence = self._clipboard.sequence()
            self._last_text = self._clipboard.get_text() if self._last_sequence is None else None
        self._apply_auto_capture_look()
        self._save_settings()
        self._set_status("Settings saved.")

        if folder_changed and messagebox.askyesno(
            APP_TITLE,
            "New snippets will be saved to the new folder.\n\nDo you also want to rebuild the index from that folder now?",
            parent=self.root,
        ):
            self.rebuild_index()

    def _save_settings(self) -> None:
        try:
            self._settings_store.save(self._settings)
        except OSError as ex:
            self._logger.error("Could not save settings.", exc_info=True)
            self._set_status(f"Could not save settings: {ex}")

    # ------------------------------------------------------------------ list & preview

    def _selected(self) -> Snippet | None:
        selection = self._tree.selection()
        return self._snippets.get(selection[0]) if selection else None

    def _schedule_search(self) -> None:
        if self._search_job is not None:
            self.root.after_cancel(self._search_job)
        self._search_job = self.root.after(SEARCH_DELAY_MS, self._run_search)

    def _run_search(self) -> None:
        self._search_job = None
        self._reload_list()

    def _reload_categories(self) -> None:
        current = self._category_var.get()
        values = [ALL_CATEGORIES, *self._service.get_categories()]
        self._category_box.configure(values=values)
        self._category_var.set(current if current in values else ALL_CATEGORIES)

    def _reload_list(self, select_id: int | None = None) -> None:
        if select_id is None and (selected := self._selected()):
            select_id = selected.id
        category = self._category_var.get()
        try:
            snippets = self._service.search(self._search_var.get(), None if category == ALL_CATEGORIES else category)
        except sqlite3.Error as ex:
            self._logger.error("Search failed.", exc_info=True)
            self._set_status(f"Search failed: {ex}")
            return

        self._tree.delete(*self._tree.get_children())
        self._snippets = {}
        for snippet in snippets:
            iid = str(snippet.id)
            self._snippets[iid] = snippet
            self._tree.insert("", tk.END, iid=iid, text=snippet.name, values=(
                snippet.category,
                snippet.action_count,
                snippet.last_copied_at.astimezone().strftime("%Y-%m-%d %H:%M"),
            ))
        if select_id is not None and str(select_id) in self._snippets:
            self._tree.selection_set(str(select_id))
            self._tree.see(str(select_id))
        self._count_var.set(f"{len(snippets)} snippet(s)")
        self._show_preview()

    def _show_preview(self) -> None:
        snippet = self._selected()
        self._preview.configure(state=tk.NORMAL)
        self._preview.delete("1.0", tk.END)
        if snippet is not None:
            # PAD copies use Windows line endings; Tk would draw each \r as a box.
            content = self._service.read_content(snippet).replace("\r\n", "\n").replace("\r", "\n")
            self._preview.insert("1.0", content)
        self._preview.configure(state=tk.DISABLED)

    def _set_status(self, message: str) -> None:
        self._status_var.set(message)


def _select_all_entry(event: tk.Event) -> str:
    event.widget.select_range(0, tk.END)
    event.widget.icursor(tk.END)
    return "break"


def _select_all_text(event: tk.Event) -> str:
    event.widget.tag_add(tk.SEL, "1.0", "end-1c")
    return "break"


class _Dialog(tk.Toplevel):
    """Modal dialog base: centred on the parent, Enter = OK (unless a code box has focus), Esc = Cancel."""

    def __init__(self, parent: tk.Misc, title: str) -> None:
        super().__init__(parent)
        self.withdraw()
        self.title(title)
        self.transient(parent)
        self.resizable(False, False)
        self.result = None
        self.body = ttk.Frame(self, padding=10)
        self.body.pack(fill=tk.BOTH, expand=True)
        self.protocol("WM_DELETE_WINDOW", self.destroy)
        self.bind("<Escape>", lambda _e: self.destroy())

    def add_buttons(self, row: int) -> None:
        buttons = ttk.Frame(self.body)
        buttons.grid(row=row, column=0, columnspan=3, sticky=tk.E, pady=(10, 0))
        ttk.Button(buttons, text="OK", command=self.on_ok).pack(side=tk.LEFT, padx=4)
        ttk.Button(buttons, text="Cancel", command=self.destroy).pack(side=tk.LEFT)

    def show(self, focus: tk.Widget) -> None:
        self.update_idletasks()
        parent = self.master
        x = parent.winfo_rootx() + (parent.winfo_width() - self.winfo_reqwidth()) // 2
        y = parent.winfo_rooty() + (parent.winfo_height() - self.winfo_reqheight()) // 3
        self.geometry(f"+{max(x, 0)}+{max(y, 0)}")
        self.deiconify()
        self.grab_set()
        focus.focus_set()
        if isinstance(focus, ttk.Entry):
            focus.select_range(0, tk.END)  # typing replaces the old value, like the C# app
        self.wait_window()

    def on_ok(self) -> None:  # pragma: no cover - overridden
        raise NotImplementedError


class SnippetDialog(_Dialog):
    """Edit name/category/tags; with show_code=True also a code box for 'Paste & Save'."""

    def __init__(self, parent, title, categories, name, category, tags, show_code: bool) -> None:
        super().__init__(parent, title)
        self._show_code = show_code
        self._name_var = tk.StringVar(value=name)
        self._category_var = tk.StringVar(value=category)
        self._tags_var = tk.StringVar(value=tags)

        rows = (("Name:", ttk.Entry(self.body, textvariable=self._name_var, width=60)),
                ("Category:", ttk.Combobox(self.body, textvariable=self._category_var, values=categories, width=58)),
                ("Tags (comma separated):", ttk.Entry(self.body, textvariable=self._tags_var, width=60)))
        for row, (label, widget) in enumerate(rows):
            ttk.Label(self.body, text=label).grid(row=row, column=0, sticky=tk.W, pady=2, padx=(0, 8))
            widget.grid(row=row, column=1, sticky=tk.EW, pady=2)
        name_entry = rows[0][1]

        self._code: tk.Text | None = None
        if show_code:
            self.resizable(True, True)
            ttk.Label(self.body, text="Code (paste with Ctrl+V):").grid(row=3, column=0, columnspan=2, sticky=tk.W, pady=(8, 2))
            self._code = tk.Text(self.body, width=90, height=22, wrap=tk.NONE, font=MONO_FONT, undo=True)
            self._code.grid(row=4, column=0, columnspan=2, sticky=tk.NSEW)
            self.body.rowconfigure(4, weight=1)
        else:
            self.bind("<Return>", lambda _e: self.on_ok())
        self.body.columnconfigure(1, weight=1)
        self.add_buttons(row=5)
        self.show(self._code or name_entry)

    def on_ok(self) -> None:
        code = self._code.get("1.0", "end-1c") if self._code is not None else ""
        if self._show_code and not code.strip():
            messagebox.showinfo(self.title(), "Paste some code first.", parent=self)
            return
        # In paste mode an empty name is allowed: a default name is generated.
        if not self._show_code and not self._name_var.get().strip():
            messagebox.showinfo(self.title(), "Name is required.", parent=self)
            return
        self.result = (self._name_var.get().strip(), self._category_var.get().strip(), self._tags_var.get().strip(), code)
        self.destroy()


class SettingsDialog(_Dialog):
    def __init__(self, parent, current: AppSettings) -> None:
        super().__init__(parent, "Settings")
        self._current = current
        self._folder_var = tk.StringVar(value=current.snippets_root_folder)
        self._category_var = tk.StringVar(value=current.default_category)
        self._threshold_var = tk.IntVar(value=current.detection_threshold)
        self._retention_var = tk.IntVar(value=current.log_retention_days)
        self._auto_var = tk.BooleanVar(value=current.auto_capture)

        ttk.Label(self.body, text="Snippets folder:").grid(row=0, column=0, sticky=tk.W, pady=2)
        folder_entry = ttk.Entry(self.body, textvariable=self._folder_var, width=55)
        folder_entry.grid(row=0, column=1, sticky=tk.EW, pady=2)
        ttk.Button(self.body, text="Browse...", command=self._browse).grid(row=0, column=2, padx=(4, 0))

        ttk.Label(self.body, text="Default category:").grid(row=1, column=0, sticky=tk.W, pady=2)
        ttk.Entry(self.body, textvariable=self._category_var, width=25).grid(row=1, column=1, sticky=tk.W, pady=2)
        ttk.Label(self.body, text="Detection strictness:").grid(row=2, column=0, sticky=tk.W, pady=2)
        ttk.Spinbox(self.body, from_=MIN_DETECTION_THRESHOLD, to=MAX_DETECTION_THRESHOLD, textvariable=self._threshold_var,
                    width=6).grid(row=2, column=1, sticky=tk.W, pady=2)
        ttk.Label(self.body, text="Keep logs (days):").grid(row=3, column=0, sticky=tk.W, pady=2)
        ttk.Spinbox(self.body, from_=1, to=365, textvariable=self._retention_var, width=6).grid(row=3, column=1, sticky=tk.W, pady=2)
        ttk.Checkbutton(self.body, text="Auto-capture PAD code from the clipboard", variable=self._auto_var).grid(
            row=4, column=1, sticky=tk.W, pady=(6, 2))
        ttk.Label(self.body, foreground="gray",
                  text="Strictness: higher = fewer false captures, lower = catches very small copies (default 3).").grid(
            row=5, column=0, columnspan=3, sticky=tk.W, pady=(6, 0))
        self.body.columnconfigure(1, weight=1)
        self.bind("<Return>", lambda _e: self.on_ok())
        self.add_buttons(row=6)
        self.show(folder_entry)

    def _browse(self) -> None:
        folder = filedialog.askdirectory(parent=self, initialdir=self._folder_var.get() or None,
                                         title="Choose the folder where snippets are saved")
        if folder:
            self._folder_var.set(os.path.normpath(folder))

    def on_ok(self) -> None:
        folder = self._folder_var.get().strip()
        try:
            if not folder or not os.path.isabs(folder):
                raise ValueError("Enter a full folder path, for example C:\\Users\\me\\Documents\\PaSnippets.")
            os.makedirs(folder, exist_ok=True)
            threshold, retention = int(self._threshold_var.get()), int(self._retention_var.get())
        except (ValueError, OSError, tk.TclError) as ex:
            messagebox.showwarning(self.title(), f"The settings cannot be used:\n{ex}", parent=self)
            return

        c = self._current
        self.result = AppSettings(
            snippets_root_folder=folder,
            auto_capture=self._auto_var.get(),
            detection_threshold=threshold,
            default_category=self._category_var.get().strip(),
            minimize_to_tray=c.minimize_to_tray,        # used by the C# app only; kept unchanged
            show_notifications=c.show_notifications,  # used by the C# app only; kept unchanged
            log_retention_days=retention,
            extra=dict(c.extra),
        )
        self.destroy()
