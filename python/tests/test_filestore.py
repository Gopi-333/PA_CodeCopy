import os
import unittest
from datetime import datetime

from pacodecopy.filestore import SnippetFileStore, sanitize_file_name
from tests.helpers import TempFolderMixin

TIMESTAMP = datetime(2026, 10, 12, 14, 30, 5)


class SanitizeTests(unittest.TestCase):
    def test_sanitize_makes_names_safe(self):
        cases = {
            "Read Excel": "Read Excel",
            'a<b>c:d"e/f\\g|h?i*j': "a_b_c_d_e_f_g_h_i_j",
            "  many    spaces\there ": "many spaces here",
            "CON": "_CON",
            "nul.txt": "_nul.txt",
            "...": "Snippet",
            "": "Snippet",
            None: "Snippet",
            "///": "Snippet",
        }
        for value, expected in cases.items():
            with self.subTest(value=value):
                self.assertEqual(expected, sanitize_file_name(value))

    def test_sanitize_truncates_long_names(self):
        self.assertEqual(60, len(sanitize_file_name("x" * 200)))


class SnippetFileStoreTests(TempFolderMixin, unittest.TestCase):
    def setUp(self) -> None:
        super().setUp()
        self.store = SnippetFileStore(self.tmp / "Snippets")

    def test_write_cannot_escape_the_root_folder(self):
        path = self.store.write("../../outside", "..\\..\\evil", TIMESTAMP, "x")
        self.assertTrue(os.path.abspath(path).startswith(str(self.store.root_folder) + os.sep))

    def test_write_creates_category_folder_and_round_trips_content(self):
        code = "SET a TO 1\r\nSET b TO 'ü'"
        path = self.store.write("Excel", "Read file", TIMESTAMP, code)
        self.assertEqual(self.store.root_folder / "Excel" / "20261012_143005_Read file.txt", path)
        self.assertEqual(code, self.store.read(path))
        self.assertEqual(ord("S"), path.read_bytes()[0])  # no UTF-8 BOM, CRLF kept as-is
        self.assertIn(b"\r\n", path.read_bytes())

    def test_write_never_overwrites(self):
        first = self.store.write("General", "Same", TIMESTAMP, "one")
        second = self.store.write("General", "Same", TIMESTAMP, "two")
        self.assertNotEqual(first, second)
        self.assertEqual(("one", "two"), (self.store.read(first), self.store.read(second)))

    def test_parse_path_reads_back_category_name_and_timestamp(self):
        path = self.store.write("Web", "Login flow", TIMESTAMP, "x")
        self.assertEqual(("Web", "Login flow", TIMESTAMP), self.store.parse_path(path, "General"))

    def test_parse_path_uses_default_category_for_files_in_root(self):
        self.store.root_folder.mkdir(parents=True)
        path = self.store.root_folder / "loose.txt"
        path.write_text("x")
        self.assertEqual(("General", "loose", None), self.store.parse_path(path, "General"))

    def test_move_renames_and_removes_empty_old_folder(self):
        path = self.store.write("Old", "Name", TIMESTAMP, "x")
        moved = self.store.move(path, "New", "Renamed", TIMESTAMP)
        self.assertFalse(path.exists())
        self.assertTrue(moved.exists())
        self.assertFalse((self.store.root_folder / "Old").exists())
        self.assertEqual("New", moved.parent.name)

    def test_delete_ignores_missing_files(self):
        self.store.delete(self.store.root_folder / "General" / "missing.txt")

    def test_reads_files_written_with_bom(self):
        self.store.root_folder.mkdir(parents=True)
        path = self.store.root_folder / "bom.txt"
        path.write_bytes("\ufeffSET a TO 1".encode("utf-8"))
        self.assertEqual("SET a TO 1", self.store.read(path))


if __name__ == "__main__":
    unittest.main()
