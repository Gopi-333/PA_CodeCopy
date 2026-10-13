"""SQLite index of snippets. Same schema and date format as SqliteSnippetRepository.cs."""

from __future__ import annotations

import re
import sqlite3
from contextlib import closing
from datetime import datetime, timezone
from pathlib import Path
from typing import Iterable

from .models import Snippet, SnippetSource

_COLUMNS = "Id, Name, Category, Tags, FilePath, Hash, ActionCount, Source, Content, CreatedAt, LastCopiedAt"
_DOTNET_DATE = re.compile(r"^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(?:\.(\d+))?(Z|[+-]\d{2}:\d{2})?$")


def format_date(value: datetime) -> str:
    """.NET round-trip format in UTC with 7 fractional digits, e.g. 2026-10-12T08:00:00.1234560Z.

    The C# app writes exactly this, and the index sorts by these strings, so the format must match.
    """
    if value.tzinfo is None:
        value = value.astimezone()  # naive = local time, like .NET DateTimeKind.Unspecified
    utc = value.astimezone(timezone.utc)
    return utc.strftime("%Y-%m-%dT%H:%M:%S.") + f"{utc.microsecond:06d}0Z"


def parse_date(value: str) -> datetime:
    """Parses .NET round-trip dates (any number of fractional digits) to an aware UTC datetime."""
    match = _DOTNET_DATE.match(value.strip())
    if not match:
        return datetime.fromisoformat(value).astimezone(timezone.utc)
    base, fraction, zone = match.groups()
    micro = int((fraction or "0")[:6].ljust(6, "0"))
    parsed = datetime.strptime(base, "%Y-%m-%dT%H:%M:%S").replace(microsecond=micro)
    if zone in (None, "Z"):
        return parsed.replace(tzinfo=timezone.utc)
    return datetime.fromisoformat(parsed.isoformat() + zone).astimezone(timezone.utc)


class SqliteSnippetRepository:
    def __init__(self, database_path: str | Path) -> None:
        self._path = Path(database_path)
        self._path.parent.mkdir(parents=True, exist_ok=True)

    def initialize(self) -> None:
        with self._connect() as db:
            db.executescript(
                """
                CREATE TABLE IF NOT EXISTS Snippets (
                    Id           INTEGER PRIMARY KEY AUTOINCREMENT,
                    Name         TEXT    NOT NULL,
                    Category     TEXT    NOT NULL,
                    Tags         TEXT    NOT NULL DEFAULT '',
                    FilePath     TEXT    NOT NULL,
                    Hash         TEXT    NOT NULL UNIQUE,
                    ActionCount  INTEGER NOT NULL DEFAULT 0,
                    Source       TEXT    NOT NULL,
                    Content      TEXT    NOT NULL,
                    CreatedAt    TEXT    NOT NULL,
                    LastCopiedAt TEXT    NOT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_Snippets_Category     ON Snippets (Category);
                CREATE INDEX IF NOT EXISTS IX_Snippets_LastCopiedAt ON Snippets (LastCopiedAt);
                """
            )

    def insert(self, snippet: Snippet) -> int:
        with self._connect() as db:
            snippet.id = self._insert(db, snippet)
        return snippet.id

    def get_by_id(self, snippet_id: int) -> Snippet | None:
        return self._query_single("Id = ?", snippet_id)

    def get_by_hash(self, hash_value: str) -> Snippet | None:
        return self._query_single("Hash = ?", hash_value)

    def update(self, snippet: Snippet) -> None:
        with self._connect() as db:
            db.execute(
                "UPDATE Snippets SET Name = ?, Category = ?, Tags = ?, FilePath = ? WHERE Id = ?",
                (snippet.name, snippet.category, snippet.tags, snippet.file_path, snippet.id),
            )

    def touch_last_copied(self, snippet_id: int, when_utc: datetime) -> None:
        with self._connect() as db:
            db.execute("UPDATE Snippets SET LastCopiedAt = ? WHERE Id = ?", (format_date(when_utc), snippet_id))

    def delete(self, snippet_id: int) -> None:
        with self._connect() as db:
            db.execute("DELETE FROM Snippets WHERE Id = ?", (snippet_id,))

    def search(self, text: str | None, category: str | None) -> list[Snippet]:
        """Searches name, tags and code (case-insensitive for ASCII, like the C# app). Newest first."""
        where: list[str] = []
        params: list[object] = []
        if text and text.strip():
            # Escape LIKE wildcards so "%" or "_" typed in the search box match literally.
            pattern = "%" + text.strip().replace("\\", "\\\\").replace("%", "\\%").replace("_", "\\_") + "%"
            where.append(r"(Name LIKE ? ESCAPE '\' OR Tags LIKE ? ESCAPE '\' OR Content LIKE ? ESCAPE '\')")
            params += [pattern, pattern, pattern]
        if category and category.strip():
            where.append("Category = ?")
            params.append(category)

        sql = f"SELECT {_COLUMNS} FROM Snippets"
        if where:
            sql += " WHERE " + " AND ".join(where)
        sql += " ORDER BY LastCopiedAt DESC, Id DESC"
        with self._connect() as db:
            return [self._to_snippet(row) for row in db.execute(sql, params)]

    def get_categories(self) -> list[str]:
        with self._connect() as db:
            rows = db.execute("SELECT DISTINCT Category FROM Snippets ORDER BY Category COLLATE NOCASE")
            return [row[0] for row in rows]

    def get_all(self) -> list[Snippet]:
        return self.search(None, None)

    def replace_all(self, snippets: Iterable[Snippet]) -> None:
        """Replaces the whole index in one transaction (rolled back on any error)."""
        with self._connect() as db:
            db.execute("DELETE FROM Snippets")
            for snippet in snippets:
                snippet.id = self._insert(db, snippet)

    @staticmethod
    def _insert(db: sqlite3.Connection, snippet: Snippet) -> int:
        cursor = db.execute(
            "INSERT INTO Snippets (Name, Category, Tags, FilePath, Hash, ActionCount, Source, Content, CreatedAt, LastCopiedAt) "
            "VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
            (
                snippet.name, snippet.category, snippet.tags, snippet.file_path, snippet.hash,
                snippet.action_count, snippet.source.value, snippet.content,
                format_date(snippet.created_at), format_date(snippet.last_copied_at),
            ),
        )
        return int(cursor.lastrowid)

    def _query_single(self, condition: str, value: object) -> Snippet | None:
        with self._connect() as db:
            row = db.execute(f"SELECT {_COLUMNS} FROM Snippets WHERE {condition} LIMIT 1", (value,)).fetchone()
            return self._to_snippet(row) if row else None

    @staticmethod
    def _to_snippet(row: tuple) -> Snippet:
        try:
            source = SnippetSource(row[7])
        except ValueError:
            source = SnippetSource.MANUAL
        return Snippet(
            id=row[0], name=row[1], category=row[2], tags=row[3], file_path=row[4], hash=row[5],
            action_count=row[6], source=source, content=row[8],
            created_at=parse_date(row[9]), last_copied_at=parse_date(row[10]),
        )

    class _Connection:
        """Opens a connection per call (like the C# app with pooling off) and commits or rolls back."""

        def __init__(self, path: Path) -> None:
            self._db = sqlite3.connect(path, timeout=5)

        def __enter__(self) -> sqlite3.Connection:
            return self._db

        def __exit__(self, exc_type, exc, tb) -> None:
            with closing(self._db):
                if exc_type is None:
                    self._db.commit()
                else:
                    self._db.rollback()

    def _connect(self) -> "SqliteSnippetRepository._Connection":
        return SqliteSnippetRepository._Connection(self._path)
