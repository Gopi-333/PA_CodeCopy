"""Detect PAD code, remove duplicates, write the file and index it (port of SnippetService.cs).

Snippet code is never logged, because PAD flows can contain passwords. Only names and short
hash prefixes are logged.
"""

from __future__ import annotations

import hashlib
import logging
import os
import sqlite3
from datetime import datetime, timezone
from pathlib import Path
from typing import Callable

from .detector import DetectionResult, PadScriptDetector
from .filestore import SnippetFileStore
from .models import SaveOutcome, SaveResult, Snippet, SnippetSource
from .repository import SqliteSnippetRepository


def normalize(text: str) -> str:
    """Line endings and trailing whitespace do not change the hash."""
    lines = text.replace("\r\n", "\n").replace("\r", "\n").split("\n")
    return "\n".join(line.rstrip() for line in lines).strip("\n")


def compute_hash(text: str) -> str:
    """Upper-case hex SHA-256 of the normalised text (same value as the C# app)."""
    return hashlib.sha256(normalize(text).encode("utf-8")).hexdigest().upper()


def normalize_tags(tags: str | None) -> str:
    """'excel,  Invoices ,excel' -> 'excel, Invoices'."""
    if not tags or not tags.strip():
        return ""
    seen: dict[str, str] = {}
    for tag in (t.strip() for t in tags.split(",")):
        if tag and tag.lower() not in seen:
            seen[tag.lower()] = tag
    return ", ".join(seen.values())


def _default_name(detection: DetectionResult) -> str:
    # The file name already starts with the date/time, so it is not repeated here.
    if detection.first_action is None:
        return "Snippet"
    if detection.action_count > 1:
        return f"{detection.first_action} +{detection.action_count - 1} more"
    return detection.first_action


def _short(hash_value: str) -> str:
    return hash_value[:8]


class SnippetService:
    def __init__(
        self,
        repository: SqliteSnippetRepository,
        file_store: SnippetFileStore,
        detector: PadScriptDetector,
        logger: logging.Logger,
        utc_now: Callable[[], datetime] | None = None,
    ) -> None:
        self._repository = repository
        self.file_store = file_store
        self._detector = detector
        self._logger = logger
        self._utc_now = utc_now or (lambda: datetime.now(timezone.utc))
        self.default_category = "General"

    def change_snippets_folder(self, root_folder: str) -> None:
        """New snippets go to the new folder; existing snippets keep their paths."""
        self.file_store = SnippetFileStore(root_folder)
        self._logger.info("Snippets folder changed to '%s'.", self.file_store.root_folder)

    def set_detection_threshold(self, threshold: int) -> None:
        self._detector.threshold = threshold

    def analyze(self, text: str | None) -> DetectionResult:
        return self._detector.analyze(text)

    def save(
        self,
        text: str | None,
        source: SnippetSource,
        require_pad_script: bool,
        name: str | None = None,
        category: str | None = None,
        tags: str | None = None,
    ) -> SaveResult:
        if text is None or not text.strip():
            return SaveResult(SaveOutcome.EMPTY, None, "Nothing to save: the text is empty.")

        detection = self._detector.analyze(text)
        if require_pad_script and not detection.is_pad_script:
            return SaveResult(SaveOutcome.NOT_PAD_SCRIPT, None, "Clipboard text is not Power Automate Desktop code.")

        hash_value = compute_hash(text)
        now = self._utc_now()
        try:
            existing = self._repository.get_by_hash(hash_value)
            if existing is not None:
                if os.path.exists(existing.file_path):
                    self._repository.touch_last_copied(existing.id, now)
                    existing.last_copied_at = now
                    return SaveResult(SaveOutcome.DUPLICATE, existing, f"Already saved as '{existing.name}'.")
                # The file was deleted outside the app: drop the stale row and save again.
                self._logger.warning("Snippet '%s' (%s) file is missing; saving it again.", existing.name, _short(hash_value))
                self._repository.delete(existing.id)

            snippet = Snippet(
                name=name.strip() if name and name.strip() else _default_name(detection),
                category=category.strip() if category and category.strip() else self.default_category,
                tags=normalize_tags(tags),
                file_path="",
                hash=hash_value,
                action_count=detection.action_count,
                source=source,
                content=text,
                created_at=now,
                last_copied_at=now,
            )
            snippet.file_path = str(self.file_store.write(snippet.category, snippet.name, now.astimezone(), text))
            try:
                self._repository.insert(snippet)
            except BaseException:
                # Keep disk and index in sync: no file without an index row.
                self._try_delete_file(snippet.file_path)
                raise

            self._logger.info(
                "Saved snippet '%s' (%s, %d actions, %s).", snippet.name, _short(hash_value), snippet.action_count, source.value
            )
            return SaveResult(SaveOutcome.SAVED, snippet, f"Saved '{snippet.name}'.")
        except (OSError, sqlite3.Error) as ex:
            self._logger.error("Failed to save snippet (%s).", _short(hash_value), exc_info=True)
            return SaveResult(SaveOutcome.FAILED, None, f"Could not save the snippet: {ex}")

    def search(self, text: str | None, category: str | None) -> list[Snippet]:
        return self._repository.search(text, category)

    def get_categories(self) -> list[str]:
        return self._repository.get_categories()

    def read_content(self, snippet: Snippet) -> str:
        """Reads the code from disk, falling back to the indexed copy if the file is gone."""
        try:
            return self.file_store.read(snippet.file_path)
        except OSError:
            self._logger.warning("Could not read '%s'; using the indexed copy.", snippet.file_path)
            return snippet.content

    def update_details(self, snippet: Snippet, name: str, category: str, tags: str) -> None:
        """Renames / re-categorises a snippet and moves its file to match."""
        if not name or not name.strip():
            raise ValueError("Name is required.")

        new_category = category.strip() if category and category.strip() else self.default_category
        old_path = snippet.file_path
        new_path = old_path
        if os.path.exists(old_path):
            new_path = str(self.file_store.move(old_path, new_category, name.strip(), snippet.created_at.astimezone()))

        snippet.name = name.strip()
        snippet.category = new_category
        snippet.tags = normalize_tags(tags)
        snippet.file_path = new_path
        try:
            self._repository.update(snippet)
        except BaseException:
            # Put the file back so the index still points at it.
            if new_path != old_path and os.path.exists(new_path):
                os.rename(new_path, old_path)
            raise
        self._logger.info("Updated snippet '%s' (#%d).", snippet.name, snippet.id)

    def delete(self, snippet: Snippet) -> None:
        self._repository.delete(snippet.id)
        self._try_delete_file(snippet.file_path)
        self._logger.info("Deleted snippet '%s' (#%d).", snippet.name, snippet.id)

    def rebuild_index(self) -> int:
        """Rebuilds the index from the snippet files. Keeps tags/source of snippets still on disk."""
        previous = {s.hash: s for s in self._repository.get_all()}
        rebuilt: dict[str, Snippet] = {}

        for path in self.file_store.enumerate_snippet_files():
            try:
                content = self.file_store.read(path)
            except (OSError, UnicodeDecodeError):
                self._logger.warning("Skipped unreadable file during rebuild: '%s'.", path)
                continue
            if not content.strip():
                continue

            hash_value = compute_hash(content)
            if hash_value in rebuilt:
                self._logger.warning("Duplicate snippet file skipped during rebuild: '%s'.", path)
                continue

            category, name, timestamp = self.file_store.parse_path(path, self.default_category)
            created = (
                timestamp.astimezone(timezone.utc)
                if timestamp is not None
                else datetime.fromtimestamp(Path(path).stat().st_mtime, timezone.utc)
            )
            old = previous.get(hash_value)
            rebuilt[hash_value] = Snippet(
                name=old.name if old else name,
                category=category,
                tags=old.tags if old else "",
                file_path=str(path),
                hash=hash_value,
                action_count=self._detector.analyze(content).action_count,
                source=old.source if old else SnippetSource.MANUAL,
                content=content,
                created_at=old.created_at if old else created,
                last_copied_at=old.last_copied_at if old else created,
            )

        self._repository.replace_all(rebuilt.values())
        self._logger.info("Index rebuilt from '%s': %d snippets.", self.file_store.root_folder, len(rebuilt))
        return len(rebuilt)

    def _try_delete_file(self, path: str) -> None:
        try:
            self.file_store.delete(path)
        except OSError:
            self._logger.warning("Could not delete file '%s'.", path)
