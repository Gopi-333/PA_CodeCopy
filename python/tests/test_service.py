import os
import sqlite3
import unittest
from datetime import datetime, timedelta, timezone

from pacodecopy.detector import PadScriptDetector
from pacodecopy.filestore import SnippetFileStore
from pacodecopy.models import SaveOutcome, SnippetSource
from pacodecopy.repository import SqliteSnippetRepository
from pacodecopy.service import SnippetService, compute_hash, normalize_tags
from tests import samples
from tests.helpers import NULL_LOGGER, TempFolderMixin

# Text exactly as the C# app captured it, and the hash the C# app stored for it in index.db.
CSHARP_CAPTURED_TEXT = (
    "SET Counter TO 0\n"
    "Excel.LaunchExcel.LaunchAndOpenUnderExistingProcess Path: $'''C:\\Data\\Invoices.xlsx''' Visible: True ReadOnly: False Instance=> ExcelInstance\n"
    "Excel.ReadFromExcel.ReadAllCells Instance: ExcelInstance ReadAsText: False FirstLineIsHeader: True RangeValue=> ExcelData\n"
    "LOOP FOREACH CurrentItem IN ExcelData\n"
    "    Variables.IncreaseVariable Value: Counter IncrementValue: 1\n"
    "END\n"
    "Excel.CloseExcel.Close Instance: ExcelInstance\n"
)
CSHARP_STORED_HASH = "723DBFF52868B552D3FB70E7BCBAB3CD67CEAAE58B909E0D5B89E9659B0C125A"


class FailingInsertRepository(SqliteSnippetRepository):
    def insert(self, snippet):
        raise sqlite3.OperationalError("disk I/O error")


class SnippetServiceTests(TempFolderMixin, unittest.TestCase):
    def setUp(self) -> None:
        super().setUp()
        self.now = datetime(2026, 10, 12, 8, 0, 0, tzinfo=timezone.utc)
        self.repo = SqliteSnippetRepository(self.tmp / "index.db")
        self.repo.initialize()
        self.root = self.tmp / "Snippets"
        self.service = self._service(self.repo)

    def _service(self, repo):
        return SnippetService(repo, SnippetFileStore(self.root), PadScriptDetector(3), NULL_LOGGER, lambda: self.now)

    def _files(self):
        return list(self.root.rglob("*.txt")) if self.root.exists() else []

    def save_auto(self, text, **kwargs):
        return self.service.save(text, SnippetSource.AUTO, require_pad_script=True, **kwargs)

    def test_auto_save_of_pad_code_writes_file_and_index(self):
        result = self.save_auto(samples.EXCEL_LOOP)
        self.assertEqual(SaveOutcome.SAVED, result.outcome)
        snippet = result.snippet
        self.assertTrue(os.path.exists(snippet.file_path))
        self.assertEqual(samples.EXCEL_LOOP, SnippetFileStore.read(snippet.file_path))
        self.assertEqual("Excel.LaunchExcel +5 more", snippet.name)
        self.assertEqual(("General", 6), (snippet.category, snippet.action_count))
        self.assertIsNotNone(self.repo.get_by_hash(snippet.hash))

    def test_auto_save_ignores_non_pad_text(self):
        self.assertEqual(SaveOutcome.NOT_PAD_SCRIPT, self.save_auto(samples.PROSE).outcome)
        self.assertEqual([], self.repo.get_all())
        self.assertFalse(self.root.exists())

    def test_manual_save_accepts_any_text_with_given_details(self):
        result = self.service.save(samples.PROSE, SnippetSource.MANUAL, False, name="Notes", category="Misc", tags="a, b ,a")
        self.assertEqual(SaveOutcome.SAVED, result.outcome)
        self.assertEqual(("Notes", "Misc", "a, b"), (result.snippet.name, result.snippet.category, result.snippet.tags))
        self.assertEqual("Misc", os.path.basename(os.path.dirname(result.snippet.file_path)))

    def test_copy_from_a_pad_flow_goes_to_the_flow_folder_with_the_prefix(self):
        result = self.save_auto(samples.EXCEL_LOOP, category="Invoice processing", name_prefix="Main")
        self.assertEqual("Main - Excel.LaunchExcel +5 more", result.snippet.name)
        self.assertEqual("Invoice processing", result.snippet.category)
        self.assertEqual("Invoice processing", os.path.basename(os.path.dirname(result.snippet.file_path)))

    def test_explicit_name_wins_over_prefix_and_blank_prefix_is_ignored(self):
        named = self.service.save(samples.EXCEL_LOOP, SnippetSource.MANUAL, False, name="Mine", name_prefix="Main")
        self.assertEqual("Mine", named.snippet.name)
        plain = self.service.save(samples.IF_ELSE, SnippetSource.MANUAL, False, name_prefix="  ")
        self.assertEqual(("File.WriteText +2 more", "General"), (plain.snippet.name, plain.snippet.category))

    def test_empty_text_is_not_saved(self):
        for text in (None, "  \r\n "):
            with self.subTest(text=text):
                self.assertEqual(SaveOutcome.EMPTY, self.service.save(text, SnippetSource.MANUAL, False).outcome)

    def test_same_code_with_different_line_endings_is_a_duplicate(self):
        first = self.save_auto(samples.EXCEL_LOOP)
        self.now += timedelta(minutes=5)
        second = self.save_auto(samples.EXCEL_LOOP.replace("\n", "\r\n") + "\r\n  ")
        self.assertEqual(SaveOutcome.DUPLICATE, second.outcome)
        self.assertEqual(first.snippet.id, second.snippet.id)
        self.assertEqual(1, len(self.repo.get_all()))
        self.assertEqual(self.now, self.repo.get_by_id(first.snippet.id).last_copied_at)
        self.assertEqual(1, len(self._files()))

    def test_duplicate_whose_file_was_deleted_is_saved_again(self):
        first = self.save_auto(samples.SINGLE_MESSAGE_BOX)
        os.remove(first.snippet.file_path)
        second = self.save_auto(samples.SINGLE_MESSAGE_BOX)
        self.assertEqual(SaveOutcome.SAVED, second.outcome)
        self.assertTrue(os.path.exists(second.snippet.file_path))
        self.assertEqual(1, len(self.repo.get_all()))

    def test_failed_index_insert_removes_the_written_file(self):
        failing = FailingInsertRepository(self.tmp / "index.db")
        result = self._service(failing).save(samples.EXCEL_LOOP, SnippetSource.AUTO, True)
        self.assertEqual(SaveOutcome.FAILED, result.outcome)
        self.assertEqual([], self._files())

    def test_update_details_moves_file_and_updates_index(self):
        snippet = self.save_auto(samples.EXCEL_LOOP).snippet
        old_path = snippet.file_path
        self.service.update_details(snippet, "Excel loop", "Excel", "excel,loop")
        self.assertFalse(os.path.exists(old_path))
        self.assertTrue(os.path.exists(snippet.file_path))
        self.assertTrue(snippet.file_path.endswith("_Excel loop.txt"))
        stored = self.repo.get_by_id(snippet.id)
        self.assertEqual(("Excel loop", "Excel", "excel, loop", snippet.file_path),
                         (stored.name, stored.category, stored.tags, stored.file_path))

    def test_delete_removes_file_and_row(self):
        snippet = self.save_auto(samples.EXCEL_LOOP).snippet
        self.service.delete(snippet)
        self.assertFalse(os.path.exists(snippet.file_path))
        self.assertIsNone(self.repo.get_by_id(snippet.id))

    def test_read_content_falls_back_to_index_copy(self):
        snippet = self.save_auto(samples.IF_ELSE).snippet
        os.remove(snippet.file_path)
        self.assertEqual(samples.IF_ELSE, self.service.read_content(snippet))

    def test_rebuild_index_picks_up_files_and_keeps_tags(self):
        tagged = self.save_auto(samples.EXCEL_LOOP, tags="keep-me").snippet
        removed = self.save_auto(samples.IF_ELSE).snippet
        os.remove(removed.file_path)
        (self.root / "Imported").mkdir(parents=True)
        (self.root / "Imported" / "20260101_120000_From backup.txt").write_text(samples.SINGLE_MESSAGE_BOX, encoding="utf-8")

        self.assertEqual(2, self.service.rebuild_index())
        self.assertEqual("keep-me", self.repo.get_by_hash(tagged.hash).tags)
        imported = self.repo.get_by_hash(compute_hash(samples.SINGLE_MESSAGE_BOX))
        self.assertEqual(("From backup", "Imported", 1), (imported.name, imported.category, imported.action_count))
        self.assertIsNone(self.repo.get_by_hash(removed.hash))

    def test_change_snippets_folder_redirects_new_files(self):
        new_root = self.tmp / "Other"
        self.service.change_snippets_folder(str(new_root))
        snippet = self.save_auto(samples.EXCEL_LOOP).snippet
        self.assertTrue(snippet.file_path.startswith(str(new_root)))

    def test_hash_ignores_line_endings_and_trailing_whitespace_only(self):
        self.assertEqual(compute_hash("a\r\nb  \r\n"), compute_hash("a\nb"))
        self.assertNotEqual(compute_hash("a\nb"), compute_hash("a\n  b"))

    def test_hash_matches_the_csharp_app(self):
        self.assertEqual(CSHARP_STORED_HASH, compute_hash(CSHARP_CAPTURED_TEXT))
        self.assertEqual(CSHARP_STORED_HASH, compute_hash(CSHARP_CAPTURED_TEXT.replace("\n", "\r\n")))

    def test_normalize_tags(self):
        self.assertEqual("excel, Invoices", normalize_tags("excel,  Invoices ,EXCEL,"))
        self.assertEqual("", normalize_tags("  "))


if __name__ == "__main__":
    unittest.main()
