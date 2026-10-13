import sqlite3
import unittest
from datetime import datetime, timedelta, timezone

from pacodecopy.models import Snippet, SnippetSource
from pacodecopy.repository import SqliteSnippetRepository, format_date, parse_date
from tests.helpers import TempFolderMixin

NOW = datetime(2026, 10, 12, 9, 0, 0, tzinfo=timezone.utc)


def new_snippet(name, category, tags, hash_value, content):
    return Snippet(name=name, category=category, tags=tags, file_path=name + ".txt", hash=hash_value,
                   action_count=1, source=SnippetSource.AUTO, content=content, created_at=NOW, last_copied_at=NOW)


class SqliteSnippetRepositoryTests(TempFolderMixin, unittest.TestCase):
    def setUp(self) -> None:
        super().setUp()
        self.repo = SqliteSnippetRepository(self.tmp / "data" / "index.db")
        self.repo.initialize()

    def test_insert_assigns_id_and_values_round_trip(self):
        snippet_id = self.repo.insert(new_snippet("Read Excel", "Excel", "excel, input", "H1", "Excel.ReadFromExcel ..."))
        loaded = self.repo.get_by_hash("H1")
        self.assertGreater(snippet_id, 0)
        self.assertEqual(snippet_id, loaded.id)
        self.assertEqual(("Read Excel", "excel, input", SnippetSource.AUTO), (loaded.name, loaded.tags, loaded.source))
        self.assertEqual(NOW, loaded.created_at)

    def test_initialize_is_idempotent(self):
        self.repo.initialize()
        self.repo.insert(new_snippet("A", "General", "", "H1", "x"))
        self.repo.initialize()
        self.assertEqual(1, len(self.repo.get_all()))

    def test_duplicate_hash_is_rejected(self):
        self.repo.insert(new_snippet("A", "General", "", "SAME", "x"))
        with self.assertRaises(sqlite3.IntegrityError):
            self.repo.insert(new_snippet("B", "General", "", "SAME", "y"))

    def test_search_matches_name_tags_content_and_filters_category(self):
        self.repo.insert(new_snippet("Read invoices", "Excel", "", "H1", "Excel.ReadFromExcel"))
        self.repo.insert(new_snippet("Login", "Web", "portal", "H2", "WebAutomation.LaunchChrome"))
        self.repo.insert(new_snippet("Send mail", "Outlook", "", "H3", "Outlook.SendEmail"))
        names = lambda items: [s.name for s in items]  # noqa: E731
        self.assertEqual(["Read invoices"], names(self.repo.search("invoice", None)))
        self.assertEqual(["Login"], names(self.repo.search("PORTAL", None)))
        self.assertEqual(["Send mail"], names(self.repo.search("SendEmail", None)))
        self.assertEqual(["Login"], names(self.repo.search(None, "Web")))
        self.assertEqual([], self.repo.search("invoice", "Web"))
        self.assertEqual(3, len(self.repo.search("  ", None)))

    def test_search_treats_like_wildcards_literally(self):
        self.repo.insert(new_snippet("Discount 100%", "General", "", "H1", "x"))
        self.repo.insert(new_snippet("Other", "General", "", "H2", "y"))
        self.assertEqual(1, len(self.repo.search("100%", None)))
        self.assertNotIn("Other", [s.name for s in self.repo.search("%", None)])
        self.assertEqual([], self.repo.search("_", None))

    def test_search_returns_most_recently_copied_first(self):
        older = new_snippet("Older", "General", "", "H1", "x")
        self.repo.insert(older)
        self.repo.insert(new_snippet("Newer", "General", "", "H2", "y"))
        self.repo.touch_last_copied(older.id, NOW + timedelta(hours=1))
        self.assertEqual(["Older", "Newer"], [s.name for s in self.repo.get_all()])

    def test_update_and_delete(self):
        snippet = new_snippet("A", "General", "", "H1", "x")
        self.repo.insert(snippet)
        snippet.name, snippet.category, snippet.tags, snippet.file_path = "Renamed", "Excel", "t1", "new.txt"
        self.repo.update(snippet)
        stored = self.repo.get_by_id(snippet.id)
        self.assertEqual(("Renamed", "Excel", "t1", "new.txt"), (stored.name, stored.category, stored.tags, stored.file_path))
        self.repo.delete(snippet.id)
        self.assertIsNone(self.repo.get_by_id(snippet.id))

    def test_get_categories_returns_distinct_sorted_values(self):
        for name, category, h in (("A", "web", "H1"), ("B", "Excel", "H2"), ("C", "Excel", "H3")):
            self.repo.insert(new_snippet(name, category, "", h, name))
        self.assertEqual(["Excel", "web"], self.repo.get_categories())

    def test_replace_all_swaps_the_index(self):
        self.repo.insert(new_snippet("Old", "General", "", "H1", "x"))
        self.repo.replace_all([new_snippet("New1", "General", "", "N1", "a"), new_snippet("New2", "General", "", "N2", "b")])
        self.assertEqual(["New1", "New2"], sorted(s.name for s in self.repo.get_all()))

    def test_replace_all_rolls_back_on_error(self):
        self.repo.insert(new_snippet("Keep", "General", "", "H1", "x"))
        with self.assertRaises(sqlite3.IntegrityError):
            self.repo.replace_all([new_snippet("Dup1", "General", "", "DUP", "a"), new_snippet("Dup2", "General", "", "DUP", "b")])
        self.assertEqual(["Keep"], [s.name for s in self.repo.get_all()])


class DateFormatTests(unittest.TestCase):
    """The index is shared with the C# app, which writes .NET round-trip dates ("o" format)."""

    def test_format_matches_dotnet_round_trip_format(self):
        value = datetime(2026, 10, 12, 13, 0, 53, 57936, tzinfo=timezone.utc)
        self.assertEqual("2026-10-12T13:00:53.0579360Z", format_date(value))

    def test_parses_value_written_by_the_csharp_app(self):
        # Real value from an index.db created by the C# app (7 fractional digits).
        parsed = parse_date("2026-10-12T13:00:53.0579368Z")
        self.assertEqual(datetime(2026, 10, 12, 13, 0, 53, 57936, tzinfo=timezone.utc), parsed)

    def test_parses_offsets_and_missing_fraction(self):
        self.assertEqual(datetime(2026, 1, 1, 10, 0, tzinfo=timezone.utc), parse_date("2026-01-01T12:00:00+02:00"))
        self.assertEqual(datetime(2026, 1, 1, 12, 0, tzinfo=timezone.utc), parse_date("2026-01-01T12:00:00Z"))

    def test_text_order_matches_time_order(self):
        a = format_date(datetime(2026, 1, 1, 9, 59, 59, 999999, tzinfo=timezone.utc))
        b = format_date(datetime(2026, 1, 1, 10, 0, 0, tzinfo=timezone.utc))
        self.assertLess(a, b)


if __name__ == "__main__":
    unittest.main()
