"""One .txt file per snippet: <root>/<Category>/yyyyMMdd_HHmmss_<Name>.txt (port of SnippetFileStore.cs)."""

from __future__ import annotations

import os
import re
from datetime import datetime
from pathlib import Path

FILE_EXTENSION = ".txt"
TIMESTAMP_FORMAT = "%Y%m%d_%H%M%S"
MAX_NAME_LENGTH = 60

# Windows rules on every OS, so files stay portable to Windows.
_INVALID_CHARS = set('<>:"/\\|?*')
_RESERVED_NAMES = {"CON", "PRN", "AUX", "NUL", *(f"COM{i}" for i in range(1, 10)), *(f"LPT{i}" for i in range(1, 10))}
_TIMESTAMP_PREFIX = re.compile(r"^(?P<ts>\d{8}_\d{6})_(?P<name>.+)$")


def sanitize_file_name(value: str | None, max_length: int = MAX_NAME_LENGTH) -> str:
    """Makes text safe to use as a Windows file or folder name."""
    if value is None or not value.strip():
        return "Snippet"

    chars: list[str] = []
    last_was_space = False
    for c in value:
        # Whitespace (tabs, new lines) collapses to one space; other control chars are invalid.
        if c.isspace():
            if not last_was_space:
                chars.append(" ")
            last_was_space = True
            continue
        last_was_space = False
        chars.append("_" if (ord(c) < 32 or ord(c) == 127 or c in _INVALID_CHARS) else c)

    result = "".join(chars).strip().strip(".").strip()
    if len(result) > max_length:
        result = result[:max_length].rstrip(" .")
    if not result or set(result) == {"_"}:
        return "Snippet"

    stem = result.split(".")[0]
    return "_" + result if stem.upper() in _RESERVED_NAMES else result


class SnippetFileStore:
    def __init__(self, root_folder: str | os.PathLike[str]) -> None:
        if not str(root_folder).strip():
            raise ValueError("Snippets folder must not be empty.")
        self.root_folder = Path(os.path.abspath(root_folder))

    def write(self, category: str, name: str, timestamp: datetime, content: str) -> Path:
        """Writes a new file (never overwrites) and returns its path."""
        folder = self.root_folder / sanitize_file_name(category)
        folder.mkdir(parents=True, exist_ok=True)
        path = self._unique_path(folder, self._build_file_name(name, timestamp))
        # "x" = create only; newline="" keeps the text exactly as copied. UTF-8 without BOM.
        with open(path, "x", encoding="utf-8", newline="") as file:
            file.write(content)
        return path

    @staticmethod
    def read(path: str | os.PathLike[str]) -> str:
        with open(path, encoding="utf-8-sig", newline="") as file:
            return file.read()

    def delete(self, path: str | os.PathLike[str]) -> None:
        """Deletes a snippet file; a missing file is not an error. Empty category folders are removed."""
        path = Path(path)
        try:
            path.unlink()
        except FileNotFoundError:
            pass
        self._try_remove_empty_folder(path.parent)

    def move(self, current: str | os.PathLike[str], category: str, name: str, timestamp: datetime) -> Path:
        """Renames/moves a file to match a new name or category. Returns the new path."""
        current = Path(current)
        folder = self.root_folder / sanitize_file_name(category)
        file_name = self._build_file_name(name, timestamp)
        if os.path.normcase(os.path.abspath(current)) == os.path.normcase(os.path.abspath(folder / file_name)):
            return current

        folder.mkdir(parents=True, exist_ok=True)
        new_path = self._unique_path(folder, file_name)
        os.rename(current, new_path)
        self._try_remove_empty_folder(current.parent)
        return new_path

    def enumerate_snippet_files(self) -> list[Path]:
        return sorted(self.root_folder.rglob("*" + FILE_EXTENSION)) if self.root_folder.is_dir() else []

    def parse_path(self, path: str | os.PathLike[str], default_category: str) -> tuple[str, str, datetime | None]:
        """Reads category, name and local timestamp back from a path written by write()."""
        path = Path(os.path.abspath(path))
        relative = os.path.relpath(path.parent, self.root_folder)
        category = default_category if relative in (".", "") else Path(relative).parts[0]

        match = _TIMESTAMP_PREFIX.match(path.stem)
        if match:
            try:
                return category, match.group("name"), datetime.strptime(match.group("ts"), TIMESTAMP_FORMAT)
            except ValueError:
                pass
        return category, path.stem, None

    @staticmethod
    def _build_file_name(name: str, timestamp: datetime) -> str:
        return f"{timestamp.strftime(TIMESTAMP_FORMAT)}_{sanitize_file_name(name)}{FILE_EXTENSION}"

    @staticmethod
    def _unique_path(folder: Path, file_name: str) -> Path:
        path = folder / file_name
        stem = Path(file_name).stem
        i = 2
        while path.exists():
            path = folder / f"{stem}_{i}{FILE_EXTENSION}"
            i += 1
        return path

    def _try_remove_empty_folder(self, folder: Path) -> None:
        try:
            if os.path.normcase(os.path.abspath(folder)) != os.path.normcase(str(self.root_folder)) \
                    and folder.is_dir() and not any(folder.iterdir()):
                folder.rmdir()
        except OSError:
            pass
