"""Data classes shared by storage, service and UI."""

from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime
from enum import Enum


class SnippetSource(str, Enum):
    AUTO = "Auto"      # captured by the clipboard watcher
    MANUAL = "Manual"  # saved by the user


class SaveOutcome(Enum):
    SAVED = "saved"
    DUPLICATE = "duplicate"
    NOT_PAD_SCRIPT = "not_pad_script"
    EMPTY = "empty"
    FAILED = "failed"


@dataclass
class Snippet:
    name: str
    category: str
    tags: str
    file_path: str
    hash: str
    action_count: int
    source: SnippetSource
    content: str
    created_at: datetime       # UTC, timezone-aware
    last_copied_at: datetime   # UTC, timezone-aware
    id: int = 0


@dataclass(frozen=True)
class SaveResult:
    outcome: SaveOutcome
    snippet: Snippet | None
    message: str

    @property
    def is_success(self) -> bool:
        return self.outcome in (SaveOutcome.SAVED, SaveOutcome.DUPLICATE)
